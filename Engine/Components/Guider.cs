using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Guider(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        IGuiderMediator guiderMediator)
        : DeviceComponent<IGuiderVM, IGuiderConsumer, GuiderInfo>(guiderMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "guider";
        public override string DisplayName { get; } = "Guider";
        public override WsMessage? GetUpdateMessage() {
            if (!IsConnected || LastInfo == null)
                return null;
            
            GuiderPayload payload = new GuiderPayload() {
                DeviceId = DeviceId,
                IsConnected = IsConnected
            };
            return new GuiderUpdate(payload, "dashboard");
        }

        public override WsMessage? GetDeviceStaticInfo() {
            GuiderInfo info = guiderMediator.GetInfo();
            GuiderPayload payload = new GuiderPayload() {
                DeviceId = info.DeviceId,
                Name = info.Name,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                DriverVersion = info.DriverVersion,
                CanClearCalibration = info.CanClearCalibration,
                CanGetLockPosition = info.CanGetLockPosition,
                CanShiftRate = info.CanSetShiftRate
            };
            return new GuiderUpdate(payload);
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            return Task.CompletedTask;
        }

        ///////////// Guiding state /////////////
        // GuiderInfo carries no "is guiding" flag, so we track it from the mediator's own events.

        /// <summary>
        /// Only a hint, from the starts and stops N.I.N.A. has seen. Written by guider events and read by
        /// the autopilot's thread.
        /// </summary>
        private volatile bool _isGuiding;
        public bool IsGuiding {
            get => _isGuiding;
            private set => _isGuiding = value;
        }

        public override async Task Start() {
            await base.Start();
            guiderMediator.GuidingStarted += OnGuidingStarted;
            guiderMediator.GuidingStopped += OnGuidingStopped;
        }

        public override async Task Destroy() {
            guiderMediator.GuidingStarted -= OnGuidingStarted;
            guiderMediator.GuidingStopped -= OnGuidingStopped;
            await base.Destroy();
        }

        private Task OnGuidingStarted(object sender, EventArgs eventArgs) {
            IsGuiding = true;
            return Task.CompletedTask;
        }

        private Task OnGuidingStopped(object sender, EventArgs eventArgs) {
            IsGuiding = false;
            return Task.CompletedTask;
        }

        protected override Task OnMediatorDisconnected(object sender, EventArgs eventArgs) {
            IsGuiding = false;
            return base.OnMediatorDisconnected(sender, eventArgs);
        }

        ///////////// Start, stop and dither /////////////
        // For the autopilot and the hub's mount commands. IsGuiding only sees starts and stops made
        // through N.I.N.A., so it's never a reason to skip asking the guider. Guiding stopped in PHD2
        // or by a guide-camera error leaves it true, and every later start would be skipped. Guiding
        // started in PHD2 leaves it false, and the stop before a slew or park would be skipped.

        // N.I.N.A.'s PHD2 stop waits for Stopped with no limit, so a hung guide camera or PHD2 dropping
        // out would hold the caller as long as its token allows. Every stop comes before something that
        // matters more, like a park, a roof close or stopping tracking at a limit, so it gets this long.
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Asks even when we think guiding is running. A running guider answers at once, and one that has
        /// stopped or lost its star gets started again. Callers image unguided on false.
        /// </summary>
        public async Task<bool> StartGuidingAsync(CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            bool wasGuiding = IsGuiding;
            if (wasGuiding) {
                LogDebug("Checking guiding is still running...");
            } else {
                Log("Starting guiding...");
            }
            bool hasStarted = await guiderMediator.StartGuiding(forceCalibration: false, NoProgress, cancellationToken);
            IsGuiding = hasStarted;
            if (hasStarted && wasGuiding) {
                LogDebug("Guiding is running.");
            } else {
                Log(hasStarted ? "Guiding started." : "Guiding failed to start.");
            }
            return hasStarted;
        }

        /// <summary>
        /// Waits at most StopTimeout for the guider to confirm. All but one N.I.N.A. guider answer false
        /// when nothing was running, so false is reported as unconfirmed only if we knew guiding was on.
        /// Never throws except on the caller's cancellation.
        /// </summary>
        public async Task<GuidingStopOutcome> StopGuidingAsync(CancellationToken cancellationToken) {
            if (!IsConnected) return GuidingStopOutcome.NotRunning;
            bool wasGuiding = IsGuiding;
            if (wasGuiding) Log("Stopping guiding...");

            // Cancelled on a timeout too, so N.I.N.A.'s wait stops polling PHD2 in the background.
            using CancellationTokenSource stopCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool>? stop = null;
            bool hasStopped;
            try {
                stop = guiderMediator.StopGuiding(stopCancellation.Token);
                hasStopped = await stop.WaitAsync(StopTimeout, cancellationToken);
            } catch (TimeoutException) {
                stopCancellation.Cancel();
                stop?.ObserveFaults(exception =>
                    LogDebug($"The abandoned guiding stop ended with: {exception.Message}"));
                IsGuiding = false;
                LogWarning($"The guider did not confirm the stop within {StopTimeout.TotalSeconds:F0} s, " +
                           "so carrying on without it.");
                return GuidingStopOutcome.NotConfirmed;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                stop?.ObserveFaults(exception =>
                    LogDebug($"The cancelled guiding stop ended with: {exception.Message}"));
                throw;
            } catch (Exception ex) {
                // Not an answer, so the flag is left as it was and the next stop asks again.
                LogWarning($"Stopping guiding failed: {ex.Message}");
                return GuidingStopOutcome.NotConfirmed;
            }

            IsGuiding = false;
            if (hasStopped) {
                Log("Guiding stopped.");
                return GuidingStopOutcome.Stopped;
            }
            if (!wasGuiding) return GuidingStopOutcome.NotRunning;
            Log("The guider did not confirm the stop; it may already have been stopped outside N.I.N.A.");
            return GuidingStopOutcome.NotConfirmed;
        }

        /// <summary>
        /// Dithers and waits for the guider to settle. Returns true without dithering when not guiding,
        /// so callers needn't check first.
        /// </summary>
        public async Task<bool> DitherAsync(CancellationToken cancellationToken) {
            if (!IsConnected || !IsGuiding) return true;
            Log("Dithering...");
            bool hasDithered = await guiderMediator.Dither(cancellationToken);
            if (!hasDithered) LogWarning("Dither did not complete.");
            return hasDithered;
        }

    }

    public enum GuidingStopOutcome {
        Stopped,
        /// <summary>Nothing was running to stop, or no guider is connected.</summary>
        NotRunning,
        /// <summary>
        /// Guiding was thought to be running and the guider didn't confirm the stop, or it didn't answer
        /// in time, or the stop threw. Treated as stopped from here on, except after a throw.
        /// </summary>
        NotConfirmed,
    }
}
