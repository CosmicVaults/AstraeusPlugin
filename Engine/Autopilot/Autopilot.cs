using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using NINA.Astrometry;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    public partial class Autopilot(Observatory observatory, IWebSocketBus webSocketBus)
        : ServiceComponent(observatory, webSocketBus)
    {
        public override string DeviceType => "autopilot";
        public override LogCategory DefaultLogCategory => LogCategory.Autopilot;

        // The running machine's context, or null. Written by the machine's task as it starts and
        // finishes and read by the telemetry loop, so access is Volatile across threads.
        private Context? _machineContext;
        private readonly SemaphoreSlim _startStopLock = new(1, 1);
        private CancellationTokenSource? _machineCancellation;
        private Task _machineTask = Task.CompletedTask;

        /// <summary>
        /// True while the machine runs, not counting a stopped one still unwinding.
        /// </summary>
        public bool IsRunning =>
            !_machineTask.IsCompleted && _machineCancellation is { IsCancellationRequested: false };

        /// <summary>
        /// True while the running machine is handling the roof, from waiting for safe conditions until
        /// a secure finishes. The Dome's roof watchdog stands down only then, so it still alerts while
        /// the machine idles until dusk.
        /// </summary>
        public bool IsRoofUnderControl =>
            IsRunning && (Volatile.Read(ref _machineContext)?.Roof.IsUnderAutopilotControl ?? false);

        public string StateName
        {
            get
            {
                bool isEnabled = IsEnabled;
                return StatusOf(isEnabled, isEnabled ? Volatile.Read(ref _machineContext) : null)
                    .ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// When tonight's session started, or null between nights.
        /// </summary>
        public DateTime? SessionStartedUtc =>
            Volatile.Read(ref _machineContext)?.Night is { IsOpen: true } night ? night.StartedUtc : null;

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

        public override Task Awake()
        {
            // Always start disabled. Write the setting directly so no start or stop runs before the
            // components are ready.
            Observatory.Settings.SetIsAutopilotEnabled(false);
            return base.Awake();
        }

        public override async Task Start()
        {
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

        /// <summary>
        /// Cancels the machine and waits, bounded, for it to unwind. Observatory.ShutDownAsync calls it
        /// before any component is destroyed, so the stop runs with the camera, guider and mount still
        /// alive. Destroy calls it again, which is harmless.
        /// </summary>
        public async Task StopMachineAsync()
        {
            _machineCancellation?.Cancel();
            // Long enough to stop the exposure, guider and tracking, but too short to hold NINA's
            // close for a park. We don't secure on close. The roof is left as found, like any stop.
            await Task.WhenAny(_machineTask, Task.Delay(MachineStopTimeoutOnDestroy));
        }

        public override async Task Destroy()
        {
            WebSocketBus.Connected -= OnSocketConnected;
            await StopMachineAsync();
            await base.Destroy();
        }

        ///////////// Dashboard update /////////////
        public override WsMessage? GetUpdateMessage() => new AutopilotUpdate(BuildPayload());

        // IsEnabled wins, since a stopped machine unwinds for a while and "Capturing" would show a
        // night already called off. Enabled with no machine yet means the start is in flight or the
        // previous run is still unwinding (see RunAfterAsync), so that shows as Idle.
        private static AutopilotStatus StatusOf(bool isEnabled, Context? context) =>
            isEnabled ? context?.AutopilotStatus ?? AutopilotStatus.Idle : AutopilotStatus.Disabled;

        private AutopilotPayload BuildPayload()
        {
            bool isEnabled = IsEnabled;
            Context? context = isEnabled ? Volatile.Read(ref _machineContext) : null;
            AutopilotStatus status = StatusOf(isEnabled, context);
            AutopilotNextResponse? target = context?.Frame.Target;
            DateTime? safeSince = context?.SafeSinceUtc;

            AutopilotPayload payload = new AutopilotPayload
            {
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
        public override async Task LateUpdate()
        {
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
        protected override async Task HandleCommandAsync(WsCommand command)
        {
            switch (command.Action)
            {
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
        public void OnProfileChanged()
        {
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
        private async Task ApplyEnabledStateAsync()
        {
            await _startStopLock.WaitAsync();
            try
            {
                bool isEnabled = IsEnabled;
                if (isEnabled && !IsRunning)
                {
                    Task previousMachine = _machineTask; // may be a stopped machine still unwinding
                    CancellationTokenSource? previousCancellation = _machineCancellation;
                    _machineCancellation = new CancellationTokenSource();
                    _machineTask = RunAfterAsync(previousMachine, previousCancellation, _machineCancellation.Token);
                }
                else if (!isEnabled)
                {
                    _machineCancellation?.Cancel(); // the machine observes the token and unwinds itself
                }
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        // Waits for any previous run to finish before starting the next, so two machines never drive
        // the hardware at once. The previous run's token is disposed once nothing can observe it.
        private async Task RunAfterAsync(Task previousMachine, CancellationTokenSource? previousCancellation,
            CancellationToken cancellationToken)
        {
            try
            {
                await previousMachine;
            }
            catch
            {
                // the previous run logged its own failures //
            }
            previousCancellation?.Dispose();

            await Task.Run(() => RunMachineAsync(cancellationToken));
        }

        ///////////// State-machine driver /////////////
        private async Task RunMachineAsync(CancellationToken cancellationToken)
        {
            Context context = new Context(Observatory, _mount, _camera, _filterWheel, _focuser, _weather,
                _rotator, _guider, _flatPanel, _safetyMonitor, _dome, _switchHub);
            context.OnStatusChanged += OnMachineStateChanged;
            Volatile.Write(ref _machineContext, context);

            string? faultReason = null;
            try
            {
                await BuildAndRunMachineAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                faultReason = $"Autopilot machine could not run: {ex.Message}";
                LogError(faultReason);
            }
            finally
            {
                context.OnStatusChanged -= OnMachineStateChanged;
                Interlocked.CompareExchange(ref _machineContext, null, context);
                if (!cancellationToken.IsCancellationRequested) IsEnabled = false;
                Observatory.Cadence.Signal();
                context.ReportSessionEnd(faultReason);
            }
        }

        private static async Task BuildAndRunMachineAsync(Context context, CancellationToken cancellationToken)
        {
            AutopilotStates states = new AutopilotStates(context);
            states.WireTransitions();
            await new StateMachine(context).RunAsync(states.CreateRoutes(), cancellationToken);
        }
    }
}
