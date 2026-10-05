using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Engine.Components;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Image.Interfaces;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Astrometry.RiseAndSet;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Observatory {
        public Authenticator Authenticator { get; private set; }
        public WebSocketClient WebSocketClient { get; private set; }
        public AstraeusWebClient AstraeusWebClient { get; private set; }
        public AstraeusSettings Settings { get; private set; }
        public IImageDataFactory ImageDataFactory { get; private set; }
        public IPlateSolverFactory PlateSolverFactory { get; private set; }
        public INighttimeCalculator NighttimeCalculator { get; }
        public Autopilot.SequenceRunner? SequenceRunner { get; private set; }

        internal Imaging.CaptureUploadQueue CaptureUploads { get; private set; }
        private readonly List<IComponent> _components = [];
        private bool _isShutDown;
        private WebSocketBus WebSocketBus { get; set; }

        /// <summary>
        /// How long the socket may stay silent before we stop trusting its Open state.
        /// </summary>
        private static readonly TimeSpan SocketStaleAfter = TimeSpan.FromSeconds(180);

        ///////////// Reconnect policy /////////////
      private static readonly TimeSpan InitialReconnectBackoff = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan StableConnectionAge = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DisplacedBackoff = TimeSpan.FromMinutes(5);
        // How often the loop looks in on the session restore while signed out. The restore has its
        // own backoff, so this only bounds how quickly a successful restore is acted on.
        private static readonly TimeSpan UnauthenticatedPoll = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MinReconnectWait = TimeSpan.FromSeconds(1);

        // Ticks so they can be read and written atomically. The disconnect policy runs on the
        // socket's receive thread and the loop reads them on its own.
        private long _reconnectBackoffTicks;
        private long _nextConnectAttemptTicks;

        ///////////// Update required /////////////

        public const string UpdateRequiredMessage =
            "This version of Astraeus is no longer supported. Install the latest from CosmicVaults.com and restart N.I.N.A.";

        // An int for Interlocked, since the HTTP handler and the socket can both report it at once.
        private int _isUpdateRequired;

        /// <summary>
        /// The server refused this plugin version. Stays set until N.I.N.A. restarts, which installing the
        /// update needs anyway.
        /// </summary>
        public bool IsUpdateRequired => Volatile.Read(ref _isUpdateRequired) == 1;

        public event Action? UpdateRequired;

        public TelemetryCadence Cadence { get; private set; }

        /// <summary>
        /// What N.I.N.A.'s exposures are for while one of the plugin's routines drives them.
        /// </summary>
        public Imaging.FrameLabel FrameLabel { get; } = new();

        public Observatory(AstraeusSettings settings,
            IPlateSolverFactory plateSolverFactory,
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IFilterWheelMediator filterWheelMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            IWeatherDataMediator weatherDataMediator,
            IFocuserMediator focuserMediator,
            IFlatDeviceMediator flatMediator,
            IDomeMediator domeMediator,
            IRotatorMediator rotatorMediator,
            ISwitchMediator switchMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageDataFactory imageDataFactory,
            IGuiderMediator guiderMediator,
            IDomeFollower domeFollower,
            INighttimeCalculator nighttimeCalculator,
            IAutoFocusVMFactory? autoFocusFactory = null,
            IImageHistoryVM? imageHistory = null,
            Autopilot.SequenceRunner? sequenceRunner = null,
            IMeridianFlipVMFactory? meridianFlipFactory = null) {
            PlateSolverFactory = plateSolverFactory;
            NighttimeCalculator = nighttimeCalculator;
            Settings = settings;
            ImageDataFactory = imageDataFactory;
            SequenceRunner = sequenceRunner;
            AstraeusWebClient = new AstraeusWebClient(this);
            Authenticator = new Authenticator(AstraeusWebClient, Settings, this);
            AstraeusWebClient.Authenticator = Authenticator;
            WebSocketClient = new WebSocketClient(Authenticator);
            WebSocketClient.Disconnected += OnSocketDisconnected;
            WebSocketClient.UpdateRequired += MarkUpdateRequired;
            WebSocketBus = new WebSocketBus(WebSocketClient);
            Cadence = new TelemetryCadence(WebSocketBus);

            if (SequenceRunner != null) SequenceRunner.Observatory = this;

            _components.Add(new Mount(this, WebSocketBus, telescopeMediator, imagingMediator, filterWheelMediator,
                domeMediator, domeFollower, meridianFlipFactory));
            _components.Add(new MasterFrameGenerator(this, WebSocketBus, imageDataFactory));
            _components.Add(new Camera(this, WebSocketBus, cameraMediator, imagingMediator, imageSaveMediator));
            _components.Add(new FilterWheel(this, WebSocketBus, filterWheelMediator));
            _components.Add(new SafetyMonitor(this, WebSocketBus, safetyMonitorMediator));
            _components.Add(new Weather(this, WebSocketBus, weatherDataMediator));
            _components.Add(new Focuser(this, WebSocketBus, focuserMediator, autoFocusFactory, imageHistory));
            _components.Add(new FlatPanel(this, WebSocketBus, flatMediator));
            _components.Add(new Dome(this, WebSocketBus, domeMediator));
            _components.Add(new Rotator(this, WebSocketBus, rotatorMediator));
            _components.Add(new SwitchHub(this, WebSocketBus, switchMediator));
            _components.Add(new Guider(this, WebSocketBus, guiderMediator));
            _components.Add(new Feed.WebcamFeed(this, WebSocketBus));
            _components.Add(new Settings(this, WebSocketBus));
            _components.Add(new Autopilot.Autopilot(this, WebSocketBus));
            // After the Camera and the Autopilot. ShutDown destroys in this order, so both producers
            // stop before the queue stops consuming.
            CaptureUploads = new Imaging.CaptureUploadQueue(this, WebSocketBus);
            _components.Add(CaptureUploads);
            _components.Add(new AstraeusLoggerComponent(this, WebSocketBus));

            Log($"Observatory loaded. Astraeus {AstraeusVersion.Current}, server {ServerEndpoints.Host}.", "system");
        }

        public async Task Awake() {
            await Authenticator.Awake();
            foreach (IComponent component in _components) {
                try {
                    await component.Awake();
                } catch (Exception ex) {
                    LogError($"Exception raised in awake of {component.GetType()}: {ex.Message}.", "system");
                }
            }
            await CheckWebSocketConnectionAsync();
        }

        public async Task Start() {
            foreach (IComponent component in _components) {
                try {
                    await component.Start();
                } catch (Exception ex) {
                    LogError($"Exception raised during start of {component.GetType()}: {ex.Message}.", "system");
                }
            }
        }

        public async Task Update() {
            // Nothing more goes to the server, not even a session restore. LateUpdate still runs, so
            // the mount limit and roof backstops keep working.
            if (IsUpdateRequired) return;
            if (!Authenticator.IsAuthenticated) {
                if (IsSocketOpen) {
                    await WebSocketClient.StopAsync();
                }
                await Authenticator.TryRestoreSessionAsync();
                return;
            }
            bool isConnectionOpen = await CheckWebSocketConnectionAsync();
            if (!isConnectionOpen) return;

            foreach (IComponent component in _components) {
                try {
                    await component.Update();
                } catch (Exception ex) {
                    LogError($"Exception occurred when updating {component}: {ex.Message}", "system");
                }
            }
        }

        public async Task LateUpdate() {
            Dictionary<string, object> deviceData = new ();
            foreach (IComponent component in _components) {
                if (TryGetUpdatePayload(component) is not { } payload) continue;
                deviceData[component.DeviceType] = payload;
            }
            Cadence.IsBusy = _components.Any(component => component.IsBusy);
            
            // Periodic telemetry goes only to somebody who can see it. Components still push their
            // own events (connections, notifications, previews) regardless of who is watching.
            if (deviceData.Count > 0 && Cadence.ShouldPublish) {
                await WebSocketBus.SendAsync(new DashboardUpdate(deviceData));
            }

            // Each in its own try, so one that throws can't stop the others' late update, including
            // the mount's limit backstop.
            foreach (IComponent component in _components) {
                try {
                    await component.LateUpdate();
                } catch (Exception ex) {
                    LogError($"Exception occurred in the late update of {component}: {ex.Message}", "system");
                }
            }
        }

        private JsonElement? TryGetUpdatePayload(IComponent component) {
            try {
                return component.GetUpdateMessage()?.Payload;
            } catch (Exception ex) {
                LogError($"Exception occurred building the update of {component}: {ex.Message}", "system");
                return null;
            }
        }

        public async Task ShutDownAsync() {
            if (_isShutDown) return;
            _isShutDown = true;

            // Sent before the components go, while the socket is still open and the update can still travel.
            await WebSocketBus.SendAsync(new NinaConnectionUpdate(false));
            foreach (IComponent component in _components) {
                try {
                    await component.Destroy();
                } catch (Exception ex) {
                    LogError($"Exception raised during destroy of {component.GetType()}: {ex.Message}.", "system");
                }
            }
            // A clean close, so the server runs its disconnect now rather than after a TCP timeout.
            await WebSocketClient.StopAsync();
        }

        public bool IsSocketOpen => WebSocketClient.State == WebSocketState.Open;

        public TimeSpan NextTickDelay {
            get {
                if (IsUpdateRequired || !Authenticator.IsAuthenticated) return UnauthenticatedPoll;
                if (!IsSocketOpen) {
                    TimeSpan wait = NextConnectAttemptUtc - DateTime.UtcNow;
                    return wait < MinReconnectWait ? MinReconnectWait : wait;
                }
                return Cadence.HasSubscribers ? Cadence.CurrentInterval : TelemetryCadence.IdleFallback;
            }
        }

        private DateTime NextConnectAttemptUtc =>
            new(Volatile.Read(ref _nextConnectAttemptTicks), DateTimeKind.Utc);

        /// <summary>Whether the socket is open. Reconnects first if it isn't and an attempt is due.</summary>
        private async Task<bool> CheckWebSocketConnectionAsync() {
            if (!Authenticator.IsAuthenticated) return false;
            if (IsSocketOpen && DateTime.UtcNow - WebSocketClient.LastInboundUtc > SocketStaleAfter) {
                // Torn down before logging, so the line goes to NINA's log file and isn't sent down
                // the socket that's about to close.
                await WebSocketClient.StopAsync();
                LogWarning($"Nothing received for over {SocketStaleAfter.TotalSeconds:F0}s; " +
                           "treated the websocket as dead and reconnecting", "system");
            }

            if (!IsSocketOpen) {
                if (DateTime.UtcNow < NextConnectAttemptUtc) return false;
                bool didOpen = await WebSocketClient.OpenConnectionAsync();
                if (!didOpen) {
                    TimeSpan delay = ScheduleReconnect();
                    LogWarning($"Failed opening WS; next attempt in {delay.TotalSeconds:F0}s", "system");
                    return false;
                }
                Log("Websocket open", "system");
                await WebSocketBus.SendAsync(new NinaConnectionUpdate(true));
                await WebSocketBus.FlushPendingAsync();
                WebSocketBus.NotifyConnected();
            }
            return true;
        }

        private TimeSpan ScheduleReconnect() {
            TimeSpan current = TimeSpan.FromTicks(Volatile.Read(ref _reconnectBackoffTicks));
            TimeSpan next = current == TimeSpan.Zero
                ? InitialReconnectBackoff
                : TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaxReconnectBackoff.Ticks));
            Volatile.Write(ref _reconnectBackoffTicks, next.Ticks);
            Volatile.Write(ref _nextConnectAttemptTicks, (DateTime.UtcNow + next).Ticks);
            return next;
        }

        private void OnSocketDisconnected(DisconnectInfo info) {
            // Our own stop (stale socket, plugin disabled, signed out, shutting down). The loop that
            // stopped it decides what's next, so nothing is booked or delayed here.
            if (info.IsDeliberate) return;

            if (info.CloseStatus is { } refusal && (int)refusal == WebSocketClient.CloseCodeUpdateRequired) {
                MarkUpdateRequired();
                return;
            }

            if (info.CloseStatus is { } status && (int)status == WebSocketClient.CloseCodeDisplaced) {
                Volatile.Write(ref _reconnectBackoffTicks, 0);
                Volatile.Write(ref _nextConnectAttemptTicks, (DateTime.UtcNow + DisplacedBackoff).Ticks);
                LogWarning("Another N.I.N.A. client connected to this account and took over the connection; " +
                           $"not reconnecting for {DisplacedBackoff.TotalMinutes:F0} minutes.", "system");
                return;
            }

            if (info.ConnectionAge >= StableConnectionAge) {
                Volatile.Write(ref _reconnectBackoffTicks, 0);
                Volatile.Write(ref _nextConnectAttemptTicks, 0);
                // The age and the close frame (or its absence) are what match a drop against the
                // server's own log for that connection.
                LogWarning($"Websocket connection lost after {info.ConnectionAge.TotalMinutes:F1} min " +
                           $"({info.CloseDescription ?? "no close frame"}); reconnecting.", "system");
            } else {
                TimeSpan delay = ScheduleReconnect();
                LogWarning($"Websocket closed {info.ConnectionAge.TotalSeconds:F0}s after connecting " +
                           $"({info.CloseDescription ?? "no close frame"}); " +
                           $"retrying in {delay.TotalSeconds:F0}s.", "system");
            }
        }

        /// <summary>
        /// Called on a 426 or a socket close of 4026. Runs once, and never throws. The socket stop isn't
        /// awaited, because this can be raised from inside the socket's own open or receive path.
        /// </summary>
        public void MarkUpdateRequired() {
            if (Interlocked.Exchange(ref _isUpdateRequired, 1) == 1) return;
            LogCritical(UpdateRequiredMessage, "system");
            try {
                Notification.ShowError(UpdateRequiredMessage);
            } catch (Exception ex) {
                LocalLog.Warning($"Could not show the update notice in N.I.N.A.: {ex.Message}");
            }
            WebSocketClient.StopAsync().ObserveFaults(exception =>
                LocalLog.Warning($"Stopping the websocket after the update notice failed: {exception.Message}"));
            try {
                UpdateRequired?.Invoke();
            } catch (Exception ex) {
                LocalLog.Warning($"An update notice handler failed: {ex.Message}");
            }
        }

        ///////////// Observing window /////////////

        private const double SunsetAltitudeDegrees = -0.833;
        private const double NauticalTwilightAltitudeDegrees = -12.0;
        private const double AstronomicalTwilightAltitudeDegrees = -18.0;

        /// <summary>
        /// Whether it's before the chosen dusk or after the chosen dawn. Shared by the state machine and
        /// the Dome's roof watchdog so both agree on when the night is over.
        /// </summary>
        public bool IsOutsideObservingWindow() {
            RiseAndSetEvent twilight = OperatingWindowTwilight();
            if (twilight.Set is DateTime dusk && twilight.Rise is DateTime dawn) {
                return DateTime.Now < dusk || DateTime.Now > dawn;
            }
            // No dusk or dawn today means polar summer or polar night. The sun's current altitude
            // says which.
            return !IsSunBelowOperatingWindowAltitude();
        }

        /// <summary>Whether today's chosen dawn has passed. False on a day without one.</summary>
        public bool IsAfterDawn() {
            RiseAndSetEvent twilight = OperatingWindowTwilight();
            return twilight.Rise is DateTime dawn && DateTime.Now > dawn;
        }

        private RiseAndSetEvent OperatingWindowTwilight() {
            NighttimeData data = NighttimeCalculator.Calculate();
            return Settings.GetAutopilotOperatingWindow() switch {
                Autopilot.OperatingWindow.Nautical => data.NauticalTwilightRiseAndSet,
                Autopilot.OperatingWindow.Astronomical => data.TwilightRiseAndSet,
                _ => data.SunRiseAndSet
            };
        }

        private bool IsSunBelowOperatingWindowAltitude() {
            double windowAltitudeDegrees = Settings.GetAutopilotOperatingWindow() switch {
                Autopilot.OperatingWindow.Nautical => NauticalTwilightAltitudeDegrees,
                Autopilot.OperatingWindow.Astronomical => AstronomicalTwilightAltitudeDegrees,
                _ => SunsetAltitudeDegrees
            };
            IAstrometrySettings astrometry = Settings.ProfileService.ActiveProfile.AstrometrySettings;
            ObserverInfo observer = new ObserverInfo {
                Latitude = astrometry.Latitude,
                Longitude = astrometry.Longitude,
                Elevation = astrometry.Elevation
            };
            double sunAltitudeDegrees = AstroUtil.GetSunAltitude(DateTime.Now, observer);
            return sunAltitudeDegrees < windowAltitudeDegrees;
        }

        ///////////// Logging /////////////
        // Every line goes to NINA's log file, so it has the whole night, and to the dashboard while
        // the socket is up. DEBUG stays local. File writes go through LocalLog so AstraeusLogSink
        // doesn't send the line twice. Caller attributes are forwarded so the file shows the real call site.

        public void Log(string message, string device, LogCategory category = LogCategory.System,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) {
            LocalLog.Info($"[{device}] {message}", member, file, line);
            Publish(device, category, message, "INFO", isToast: false);
        }

        public void LogWarning(string message, string device, LogCategory category = LogCategory.System,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) {
            LocalLog.Warning($"[{device}] {message}", member, file, line);
            Publish(device, category, message, "WARNING", isToast: false);
        }

        public void LogError(string message, string device, LogCategory category = LogCategory.System,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) {
            LocalLog.Error($"[{device}] {message}", member, file, line);
            Publish(device, category, message, "ERROR", isToast: true);
        }

        public void LogDebug(string message, string device, LogCategory category = LogCategory.System,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) {
            LocalLog.Debug($"[{device}] {message}", member, file, line);
        }

        /// <summary>
        /// One step above LogError, for a state somebody needs to know about. Logs only. Use ReportEvent
        /// to send the email too, or Context.ReportCritical from inside the autopilot, which does both.
        /// </summary>
        public void LogCritical(string message, string device, LogCategory category = LogCategory.System,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) {
            LocalLog.Error($"[{device}] {message}", member, file, line);
            Publish(device, category, message, "CRITICAL", isToast: true);
        }

        private void Publish(string device, LogCategory category, string message, string severity, bool isToast) {
            if (!IsSocketOpen) return;
            string categoryName = category.ToString().ToLowerInvariant();
            string redactedMessage = UserProfilePath.Redact(message);
            _ = WebSocketBus.SendAsync(new WsNotification(device, categoryName, redactedMessage,
                isToast: isToast, severity: severity));
        }

        ///////////// Alerts /////////////
        // Things the observers may want an email about. Sent over HTTP because they matter most on an
        // unattended night, which is when the socket is most likely down.

        /// <summary>
        /// Fire and forget. Never throws and never blocks, so it's safe from a mediator event handler or a
        /// finally block. Takes no CancellationToken because the machine's is already cancelled on the stop
        /// path. The web client's 10-second timeout bounds it.
        /// </summary>
        public void ReportEvent(string kind, string message, object? details = null, string? clientRef = null) {
            _ = Task.Run(async () => {
                try {
                    await ReportEventAsync(kind, message, details, clientRef);
                } catch (Exception ex) {
                    LocalLog.Warning($"[alerts] Could not report {kind}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// The awaitable form. There's no outbox, so an event raised while signed out is lost. That suits
        /// alerts, since a "roof opened" four hours late is worse than nothing.
        /// </summary>
        public async Task<bool> ReportEventAsync(string kind, string message, object? details = null,
            string? clientRef = null) {
            if (Authenticator?.IsAuthenticated != true) {
                LocalLog.Warning($"[alerts] Not signed in, so dropping {kind}: {message}");
                return false;
            }

            return await AstraeusWebClient.PostNotificationEventAsync(kind, message, WithRigSnapshot(details),
                clientRef);
        }

        /// <summary>
        /// The call site's details on top of a rig snapshot, with the call site winning any clash. Built here
        /// because the server stores no mount or safety state, and its roof and autopilot rows come over the
        /// socket, which is down on the nights these alerts matter.
        /// </summary>
        private Dictionary<string, object> WithRigSnapshot(object? details) {
            Dictionary<string, object> merged = RigSnapshot();
            if (details is null) return merged;
            // Call sites pass anonymous objects. Nulls are skipped here because NullValueHandling drops
            // null properties but not null dictionary entries.
            foreach (PropertyInfo property in details.GetType().GetProperties()) {
                if (property.GetValue(details) is { } value)
                    merged[property.Name] = value;
            }
            return merged;
        }

        /// <summary>
        /// Read from each device's mediator, since a tick's LastInfo is empty outside a tick and while the
        /// socket is down. A device not in use is left out, not reported as missing.
        /// </summary>
        private Dictionary<string, object> RigSnapshot() {
            var snapshot = new Dictionary<string, object>();
            try {
                if (TryGetComponent(out Dome? dome) && (dome.IsConnected || dome.HasDefaultDevice)) {
                    snapshot["roof"] = DescribeShutter(dome);
                    if ((dome.ConnectedDeviceId ?? dome.DefaultDevice) is { } roofDeviceId)
                        snapshot["roof_device_id"] = roofDeviceId;
                }
                if (TryGetComponent(out Mount? mount) && (mount.IsConnected || mount.HasDefaultDevice))
                    snapshot["mount"] = !mount.IsConnected ? "disconnected" : mount.IsParked ? "parked" : "not parked";
                if (TryGetComponent(out Autopilot.Autopilot? autopilot)) {
                    snapshot["autopilot_state"] = autopilot.StateName;
                    if (autopilot.SessionStartedUtc is { } startedUtc)
                        snapshot["session_started_at"] =
                            startedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                }
                if (TryGetComponent(out SafetyMonitor? safetyMonitor)
                    && (safetyMonitor.IsConnected || safetyMonitor.HasDefaultDevice))
                    snapshot["safety"] = !safetyMonitor.IsConnected ? "disconnected"
                        : safetyMonitor.IsSafe() ? "safe" : "unsafe";
                if (TryGetComponent(out Weather? weather) && weather.Snapshot() is { Count: > 0 } readings)
                    snapshot["weather"] = readings;
            } catch (Exception ex) {
                // The snapshot is only context. The alert itself must still go.
                LocalLog.Warning($"[alerts] Could not read the rig for an alert: {ex.Message}");
            }
            return snapshot;
        }

        /// <summary>The shutter in the email's words: open, closed, opening, closing, error or disconnected.</summary>
        private static string DescribeShutter(Dome dome) {
            string name = dome.ShutterStatusName;
            return (name.StartsWith("Shutter", StringComparison.Ordinal) ? name["Shutter".Length..] : name)
                .ToLowerInvariant();
        }

        public T GetComponent<T>() where T : class {
            foreach (IComponent component in _components) {
                if (component is T match)
                    return match;
            }
            throw new InvalidOperationException(
                $"Component implementing {typeof(T).Name} not found");
        }

        public IEnumerable<T> GetComponents<T>() where T : class {
            foreach (IComponent component in _components) {
                if (component is T match)
                    yield return match;
            }
        }

        public bool TryGetComponent<T>([NotNullWhen(true)] out T? component) where T : class {
            foreach (IComponent candidate in _components) {
                if (candidate is T match) {
                    component = match;
                    return true;
                }
            }
            component = null;
            return false;
        }
    }
}
