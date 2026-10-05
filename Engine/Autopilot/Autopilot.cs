using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using NINA.Astrometry;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    public class Autopilot(Observatory observatory, IWebSocketBus webSocketBus)
        : ServiceComponent(observatory, webSocketBus) {
        public override string DeviceType => "autopilot";
        public override LogCategory DefaultLogCategory => LogCategory.Autopilot;

        ///////////// Public events /////////////
        public event EventHandler<SettingChangedEventArgs>? SettingsChanged;

        /// <summary>
        /// wireKey must match the setting's [JsonPropertyName] in AutopilotSettingsPayload, since the
        /// Settings component filters the payload down to it.
        /// </summary>
        private void RaiseChanged(string? wireKey, [CallerMemberName] string propertyName = "") =>
            SettingsChanged?.Invoke(this, new SettingChangedEventArgs(propertyName, wireKey));

        ///////////// Settings-backed properties /////////////

        /// <summary>
        /// The only way the state machine starts and stops, used by the WS command, the UI and
        /// plugin-disable.
        /// </summary>
        public bool IsEnabled {
            get => Observatory.Settings.IsAutopilotEnabled();
            set {
                if (value == IsEnabled) return;
                Observatory.Settings.SetIsAutopilotEnabled(value);
                RaiseChanged("is_autopilot_enabled");
                Observatory.Cadence.Signal();
                ApplyEnabledStateAsync().ObserveFaults(exception =>
                    LogError($"Could not {(value ? "start" : "stop")} the autopilot: {exception.Message}"));
            }
        }

        public RoofMode RoofMode {
            get => Observatory.Settings.GetAutopilotRoofMode();
            set {
                Observatory.Settings.SetAutopilotRoofMode(value);
                RaiseChanged("roof_mode");
            }
        }

        /// <summary>
        /// When false autopilot never tries to connect to anything. It waits for the required devices to be connected in
        /// N.I.N.A.
        /// </summary>
        public bool ShouldAutoConnectEquipment {
            get => Observatory.Settings.ShouldAutoConnectEquipment();
            set {
                Observatory.Settings.SetShouldAutoConnectEquipment(value);
                RaiseChanged("auto_connect_equipment");
            }
        }

        public OperatingWindow OperatingWindow {
            get => Observatory.Settings.GetAutopilotOperatingWindow();
            set {
                Observatory.Settings.SetAutopilotOperatingWindow(value);
                RaiseChanged("operating_window");
            }
        }

        /// <summary>
        /// Cools the camera once the equipment is connected and warms it at dawn after securing. A
        /// switch-off or fault doesn't warm it, since those leave the observatory as found.
        /// </summary>
        public bool IsCameraCoolingEnabled {
            get => Observatory.Settings.IsCameraCoolingEnabled();
            set {
                Observatory.Settings.SetIsCameraCoolingEnabled(value);
                RaiseChanged("is_camera_cooling_enabled");
            }
        }

        /// <summary>
        /// The temperature (°C) the autopilot cools the camera to at dusk. A cooler already set colder
        /// than this is left where it is.
        /// </summary>
        public double CameraCoolingTemperature {
            get => Observatory.Settings.GetCameraCoolingTemperature();
            set {
                Observatory.Settings.SetCameraCoolingTemperature(value);
                RaiseChanged("camera_cooling_temperature_celsius");
            }
        }

        ///////////// Mount pointing / tracking limits /////////////

        /// <summary>Master switch for the mount pointing and tracking limits. Off enforces none.</summary>
        public bool IsMountLimitEnabled {
            get => Observatory.Settings.IsMountLimitEnabled();
            set {
                Observatory.Settings.SetIsMountLimitEnabled(value);
                RaiseChanged("is_mount_limit_enabled");
            }
        }

        /// <summary>Whether the fixed minimum-altitude floor is enforced.</summary>
        public bool IsAltitudeLimitEnabled {
            get => Observatory.Settings.IsAltitudeLimitEnabled();
            set {
                Observatory.Settings.SetIsAltitudeLimitEnabled(value);
                RaiseChanged("is_altitude_limit_enabled");
            }
        }

        /// <summary>The minimum altitude (degrees) the mount may point at or track down to.</summary>
        public double MinAltitudeDegrees {
            get => Observatory.Settings.GetMountMinAltitudeDegrees();
            set {
                Observatory.Settings.SetMountMinAltitudeDegrees(value);
                RaiseChanged("min_altitude_degrees");
            }
        }

        /// <summary>Whether the profile's custom horizon is enforced as an azimuth-dependent floor.</summary>
        public bool IsCustomHorizonLimitEnabled {
            get => Observatory.Settings.IsCustomHorizonLimitEnabled();
            set {
                Observatory.Settings.SetIsCustomHorizonLimitEnabled(value);
                RaiseChanged("is_custom_horizon_limit_enabled");
            }
        }

        /// <summary>
        /// Whether a target the scheduler lets cross the meridian limit gets a flip instead of being
        /// refused. Off by default. The limit comes from N.I.N.A.'s Meridian Flip settings and only
        /// applies with the mount limits on.
        /// </summary>
        public bool IsMeridianFlipEnabled {
            get => Observatory.Settings.IsMeridianFlipEnabled();
            set {
                Observatory.Settings.SetIsMeridianFlipEnabled(value);
                RaiseChanged("is_meridian_flip_enabled");
            }
        }

        /// <summary>
        /// How close (arc-minutes) the mount must already be to a new target to skip the slew and plate
        /// solve and capture straight away.
        /// </summary>
        public double RecenterToleranceArcmin {
            get => Observatory.Settings.GetRecenterToleranceArcmin();
            set {
                Observatory.Settings.SetRecenterToleranceArcmin(value);
                RaiseChanged("recenter_tolerance_arcmin");
            }
        }

        /// <summary>
        /// Seconds the safety monitor must read Safe without a break before the autopilot reopens the
        /// roof and unparks after an unsafe spell. Zero resumes on the first Safe reading.
        /// </summary>
        public int SafeSettleSeconds {
            get => Observatory.Settings.GetSafeSettleSeconds();
            set {
                Observatory.Settings.SetSafeSettleSeconds(value);
                RaiseChanged("safe_settle_seconds");
            }
        }
        
        ///////////// Rotator /////////////

        /// <summary>The default, None, leaves the rotator alone.</summary>
        public RotatorFallbackMode RotatorFallbackMode {
            get => Observatory.Settings.GetAutopilotRotatorFallbackMode();
            set {
                Observatory.Settings.SetAutopilotRotatorFallbackMode(value);
                RaiseChanged("rotator_fallback_mode");
            }
        }

        /// <summary>
        /// The angle (degrees) for the rotator fallback modes other than None. It's a sky position angle
        /// or a mechanical angle, depending on RotatorFallbackMode.
        /// </summary>
        public double RotatorFallbackAngle {
            get => Observatory.Settings.GetAutopilotRotatorFallbackAngle();
            set {
                // Fold into [0, 360) so 720 or -90 is stored, and shown on the options page, as the
                // angle the rotator really gets driven to.
                Observatory.Settings.SetAutopilotRotatorFallbackAngle(AstroUtil.EuclidianModulus(value, 360));
                RaiseChanged("rotator_fallback_angle");
            }
        }

        /// <summary>
        /// Only used when N.I.N.A.'s Dome setting "Refuse open or close if mount is unparked" is on. Off by
        /// default, since parking slews under a closed roof and the park position may only be reachable
        /// with the roof open.
        /// </summary>
        public bool ShouldParkMountToOpenRoof {
            get => Observatory.Settings.ShouldParkMountToOpenRoof();
            set {
                Observatory.Settings.SetShouldParkMountToOpenRoof(value);
                RaiseChanged("park_mount_to_open_roof");
            }
        }

        ///////////// Flat panel /////////////

        public FlatPanelMode FlatPanelMode {
            get => Observatory.Settings.GetAutopilotFlatPanelMode();
            set {
                Observatory.Settings.SetAutopilotFlatPanelMode(value);
                RaiseChanged("flat_panel_mode");
            }
        }

        /// <summary>
        /// Only matters for OpenAndClose. When off, the cover closes before the roof at dawn, or stays open
        /// with a warning if the roof is already shut. Bad-weather closes leave the cover alone either way.
        /// </summary>
        public bool ShouldOperateFlatPanelWithRoofClosed {
            get => Observatory.Settings.ShouldOperateFlatPanelWithRoofClosed();
            set {
                Observatory.Settings.SetShouldOperateFlatPanelWithRoofClosed(value);
                RaiseChanged("flat_panel_operates_with_roof_closed");
            }
        }

        ///////////// Autofocus /////////////

        public bool IsAutofocusEnabled {
            get => Observatory.Settings.IsAutofocusEnabled();
            set {
                Observatory.Settings.SetIsAutofocusEnabled(value);
                RaiseChanged("is_autofocus_enabled");
            }
        }

        public bool ShouldAutofocusOnTemperatureChange {
            get => Observatory.Settings.ShouldAutofocusOnTemperatureChange();
            set {
                Observatory.Settings.SetShouldAutofocusOnTemperatureChange(value);
                RaiseChanged("autofocus_on_temperature_change");
            }
        }

        /// <summary>Temperature change (°C) since the last autofocus that triggers a refocus.</summary>
        public double AutofocusTemperatureThreshold {
            get => Observatory.Settings.GetAutofocusTemperatureThreshold();
            set {
                Observatory.Settings.SetAutofocusTemperatureThreshold(value);
                RaiseChanged("autofocus_temperature_threshold");
            }
        }

        public bool ShouldAutofocusOnFilterChange {
            get => Observatory.Settings.ShouldAutofocusOnFilterChange();
            set {
                Observatory.Settings.SetShouldAutofocusOnFilterChange(value);
                RaiseChanged("autofocus_on_filter_change");
            }
        }

        public bool ShouldAutofocusOnTimeInterval {
            get => Observatory.Settings.ShouldAutofocusOnTimeInterval();
            set {
                Observatory.Settings.SetShouldAutofocusOnTimeInterval(value);
                RaiseChanged("autofocus_on_time_interval");
            }
        }

        public int AutofocusIntervalMinutes {
            get => Observatory.Settings.GetAutofocusIntervalMinutes();
            set {
                Observatory.Settings.SetAutofocusIntervalMinutes(value);
                RaiseChanged("autofocus_interval_minutes");
            }
        }

        /// <summary>
        /// When on, the server decides when to refocus and owns the per-filter focus positions. We post
        /// each autofocus result and move the focuser to the server's suggested position in between.
        /// </summary>
        public bool IsSmartAutofocusEnabled {
            get => Observatory.Settings.IsSmartAutofocusEnabled();
            set {
                Observatory.Settings.SetIsSmartAutofocusEnabled(value);
                RaiseChanged("is_smart_autofocus_enabled");
            }
        }

        ///////////// Cloud upload /////////////

        /// <summary>
        /// Whether frames and their previews go up to cloud storage. Off keeps everything on this
        /// machine, though the live preview still reaches the dashboard. Read fresh for each frame.
        /// </summary>
        public bool IsCloudUploadEnabled {
            get => Observatory.Settings.IsCloudUploadEnabled();
            set {
                Observatory.Settings.SetIsCloudUploadEnabled(value);
                RaiseChanged("is_cloud_upload_enabled");
            }
        }

        /// <summary>
        /// Absolute path to a NINA advanced-sequencer .json run once at dusk by the startup state.
        /// Null/empty disables it. Machine-local (not in the web contract).
        /// </summary>
        public string? StartupSequencePath {
            get => Observatory.Settings.GetAutopilotStartupSequencePath();
            set {
                Observatory.Settings.SetAutopilotStartupSequencePath(value);
                RaiseChanged(null);
            }
        }

        /// <summary>
        /// Absolute path to a NINA advanced-sequencer .json run once at dawn by the shutdown state.
        /// Null/empty disables it. Machine-local (not in the web contract).
        /// </summary>
        public string? ShutdownSequencePath {
            get => Observatory.Settings.GetAutopilotShutdownSequencePath();
            set {
                Observatory.Settings.SetAutopilotShutdownSequencePath(value);
                RaiseChanged(null);
            }
        }

        ///////////// Runtime state machine /////////////
        // The running machine's context, or null. Written by the machine's task as it starts and
        // finishes and read by the telemetry loop, so access is Volatile across threads.
        private Context? _machineContext;
        private readonly SemaphoreSlim _startStopLock = new(1, 1);
        private CancellationTokenSource? _machineCancellation;
        private Task _machineTask = Task.CompletedTask;

        /// <summary>True while the machine runs, not counting a stopped one still unwinding.</summary>
        public bool IsRunning =>
            !_machineTask.IsCompleted && _machineCancellation is { IsCancellationRequested: false };

        /// <summary>
        /// True while the running machine is handling the roof, from waiting for safe conditions until
        /// a secure finishes. The Dome's roof watchdog stands down only then, so it still alerts while
        /// the machine idles until dusk.
        /// </summary>
        public bool IsRoofUnderControl =>
            IsRunning && (Volatile.Read(ref _machineContext)?.IsRoofUnderAutopilotControl ?? false);

        public string StateName {
            get {
                bool isEnabled = IsEnabled;
                return StatusOf(isEnabled, isEnabled ? Volatile.Read(ref _machineContext) : null)
                    .ToString().ToLowerInvariant();
            }
        }

        /// <summary>When tonight's session started, or null between nights.</summary>
        public DateTime? SessionStartedUtc => Volatile.Read(ref _machineContext)?.SessionStartedUtc;

        ///////////// Component references (resolved in Start) /////////////
        private Mount? _mount;
        private Camera? _camera;
        private FilterWheel? _filterWheel;
        private Focuser? _focuser;
        private Weather? _weather;
        private Rotator? _rotator;
        private Guider? _guider;
        private FlatPanel? _flatPanel;
        private SafetyMonitor? _safetyMonitor;
        private Dome? _dome;
        private SwitchHub? _switchHub;

        ///////////// Lifecycle /////////////
        private static readonly TimeSpan MachineStopTimeoutOnDestroy = TimeSpan.FromSeconds(30);

        public override Task Awake() {
            // Always start disabled. Write the setting directly so no start or stop runs before the
            // components are ready.
            Observatory.Settings.SetIsAutopilotEnabled(false);
            return base.Awake();
        }

        public override async Task Start() {
            await base.Start();
            WebSocketBus.Connected += OnSocketConnected;
            Observatory.TryGetComponent<Mount>(out _mount);
            Observatory.TryGetComponent<Camera>(out _camera);
            Observatory.TryGetComponent<FilterWheel>(out _filterWheel);
            Observatory.TryGetComponent<Focuser>(out _focuser);
            Observatory.TryGetComponent<Weather>(out _weather);
            Observatory.TryGetComponent<Rotator>(out _rotator);
            Observatory.TryGetComponent<Guider>(out _guider);
            Observatory.TryGetComponent<FlatPanel>(out _flatPanel);
            Observatory.TryGetComponent<SafetyMonitor>(out _safetyMonitor);
            Observatory.TryGetComponent<Dome>(out _dome);
            Observatory.TryGetComponent<SwitchHub>(out _switchHub);
        }

        public override async Task Destroy() {
            WebSocketBus.Connected -= OnSocketConnected;
            // NINA is closing or the plugin is being torn down, so stop the machine before its
            // components are destroyed under it.
            _machineCancellation?.Cancel();
            // Long enough to stop the exposure, guider and tracking, but too short to hold NINA's
            // close for a park. We don't secure on close. The roof is left as found, like any stop.
            await Task.WhenAny(_machineTask, Task.Delay(MachineStopTimeoutOnDestroy));
            await base.Destroy();
        }

        ///////////// Dashboard update /////////////
        public override WsMessage? GetUpdateMessage() => new AutopilotUpdate(BuildPayload());

        // IsEnabled wins, since a stopped machine unwinds for a while and "Capturing" would show a
        // night already called off. Enabled with no machine yet means the start is in flight or the
        // previous run is still unwinding (see RunAfterAsync), so that shows as Idle.
        private static AutopilotStatus StatusOf(bool isEnabled, Context? context) =>
            isEnabled ? context?.AutopilotStatus ?? AutopilotStatus.Idle : AutopilotStatus.Disabled;

        private AutopilotPayload BuildPayload() {
            bool isEnabled = IsEnabled;
            Context? context = isEnabled ? Volatile.Read(ref _machineContext) : null;
            AutopilotStatus status = StatusOf(isEnabled, context);
            AutopilotNextResponse? target = context?.CurrentTarget;
            DateTime? safeSince = context?.SafeSinceUtc;

            AutopilotPayload payload = new AutopilotPayload {
                // The browser matches these keys literally, and ToLower() would give "Idle" a dotless
                // i on a Turkish-locale machine.
                State = status.ToString().ToLowerInvariant(),
                IsEnabled = isEnabled,
                IsRunning = IsRunning,
                ImageId = target?.ImageId,
                TargetName = target?.TargetName,
                // Message stays unset. The card's message line comes from the autopilot's notification
                // stream, which already reaches the browser.
                //
                // While WaitForSafe settles, send when the Safe run began and how long it must last,
                // and the browser counts up. Elapsed time would change the record every pass, and the
                // event path compares by value, so the server would store a frame each time.
                // Invariant culture because a custom format's ":" is the culture's time separator.
                SafeSince = safeSince?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                SafeSettleSeconds = safeSince is null ? null : context?.SafeSettleSeconds,
            };
            return payload;
        }

        ///////////// Durable state /////////////
        // The last payload sent for the server to store, or null if none on this connection.
        // AutopilotPayload is a record, so this compares by value.
        private AutopilotPayload? _lastPublishedState;

        /// <summary>
        /// Pushes the runtime state on the event path, since telemetry only goes out while someone's watching
        /// and the morning page load needs it. Sent here so it stays in order with the telemetry frame and a
        /// burst of changes collapses into one frame.
        /// </summary>
        public override async Task LateUpdate() {
            if (!Observatory.IsSocketOpen) return;
            AutopilotPayload payload = BuildPayload();
            if (payload == _lastPublishedState) return;
            _lastPublishedState = payload;
            await WebSocketBus.SendAsync(new AutopilotUpdate(payload, context: "event"));
        }

        // A new socket may be a server that knows nothing about this run. Without this, the diff
        // above would stay quiet until the next state change, which on a slow night is hours.
        private void OnSocketConnected() => _lastPublishedState = null;

        // A state change gets its own frame. Otherwise the loop sits out its sleep, and "Slewing" two
        // seconds after the mount moves looks like lag. Cheap with nobody watching, since telemetry
        // still waits for ShouldPublish and the event path only sends on a change.
        private void OnMachineStateChanged(object? sender, EventArgs eventArgs) => Observatory.Cadence.Signal();

        ///////////// Commands /////////////
        protected override async Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "start": IsEnabled = true; break;
                case "stop": IsEnabled = false; break;
                default: return;
            }

            // The dashboard's toggle waits on this. The new state follows on the event path.
            await BroadcastMessageReceivedAsync(command.Id, true);
        }

        /// <summary>
        /// After a N.I.N.A. profile switch, stops the autopilot and leaves it off, as after every start.
        /// Its settings belong to the profile, and the new profile's weren't chosen for tonight's run.
        /// </summary>
        public void OnProfileChanged() {
            bool wasRunning = IsRunning;
            Observatory.Settings.SetIsAutopilotEnabled(false);
            RaiseChanged("is_autopilot_enabled", nameof(IsEnabled));
            ApplyEnabledStateAsync().ObserveFaults(exception =>
                LogError($"Could not stop the autopilot after the profile change: {exception.Message}"));
            if (wasRunning) LogWarning("N.I.N.A.'s profile changed, so the autopilot has been switched off.");
        }

        ///////////// Start / stop /////////////
        // Serialised, and decided from the setting as it stands when it runs, so two quick toggles
        // cannot leave a machine running with the setting off, or stopped with it on.
        private async Task ApplyEnabledStateAsync() {
            await _startStopLock.WaitAsync();
            try {
                bool isEnabled = IsEnabled;
                if (isEnabled && !IsRunning) {
                    Task previousMachine = _machineTask; // may be a stopped machine still unwinding
                    CancellationTokenSource? previousCancellation = _machineCancellation;
                    _machineCancellation = new CancellationTokenSource();
                    _machineTask = RunAfterAsync(previousMachine, previousCancellation, _machineCancellation.Token);
                } else if (!isEnabled) {
                    _machineCancellation?.Cancel(); // the machine observes the token and unwinds itself
                }
            } finally {
                _startStopLock.Release();
            }
        }

        // Waits for any previous run to finish before starting the next, so two machines never drive
        // the hardware at once. The previous run's token is disposed once nothing can observe it.
        private async Task RunAfterAsync(Task previousMachine, CancellationTokenSource? previousCancellation,
            CancellationToken cancellationToken) {
            try {
                await previousMachine;
            } catch {
                // the previous run logged its own failures //
            }
            previousCancellation?.Dispose();

            await Task.Run(() => RunMachineAsync(cancellationToken));
        }

        ///////////// State-machine driver /////////////
        private async Task RunMachineAsync(CancellationToken cancellationToken) {
            Context context = new Context(Observatory, _mount, _camera, _filterWheel, _focuser, _weather,
                _rotator, _guider, _flatPanel, _safetyMonitor, _dome, _switchHub);
            context.OnStatusChanged += OnMachineStateChanged;
            Volatile.Write(ref _machineContext, context);

            string? faultReason = null;
            try {
                await BuildAndRunMachineAsync(context, cancellationToken);
            } catch (Exception ex) {
                faultReason = $"Autopilot machine could not run: {ex.Message}";
                LogError(faultReason);
            } finally {
                context.OnStatusChanged -= OnMachineStateChanged;
                Interlocked.CompareExchange(ref _machineContext, null, context);
                if (!cancellationToken.IsCancellationRequested) IsEnabled = false;
                Observatory.Cadence.Signal();
                context.ReportSessionEnd(faultReason);
            }
        }

        private static async Task BuildAndRunMachineAsync(Context context, CancellationToken cancellationToken) {
            AutopilotStates states = new AutopilotStates(context);
            states.WireTransitions();
            StateMachine.AbortRoutes abortRoutes = states.CreateAbortRoutes();
            await new StateMachine(context).RunAsync(states.EntryState, abortRoutes, cancellationToken);
        }
    }
}
