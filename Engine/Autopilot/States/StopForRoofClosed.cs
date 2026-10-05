using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// The roof stopped reading open while imaging, with the safety monitor still Safe. Stops the exposure,
    /// guider and mount without parking, since the park position may only be reachable with the roof open.
    /// In Operate mode only the autopilot should move this roof, so it disables instead of reopening it.
    /// </summary>
    internal sealed class StopForRoofClosed(string name, Context context) : StateBase(name) {
        // It's the response to the roof, so it finishes whatever the clock says.
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            // A dome that dropped off reads as "not open", so it gets one reconnect first. If the roof
            // still reads open it was never closed, and the night carries on from the next poll.
            if (await IsRoofOpenAfterDomeReconnectAsync(cancellationToken)) {
                context.LogWarning(
                    "The dome dropped off and has reconnected with the roof still open, so carrying on imaging.");
                await context.ReportStartedImageFailedAsync("cycle aborted: the dome dropped off");
                return AutopilotStateResult.Resumed;
            }

            context.IsRoofExpectedOpen = false;
            string shutter = context.Dome?.ShutterStatusName ?? "no dome";
            bool isOperated = context.RoofMode == RoofMode.Operate;

            StopExposure();
            // Before the mount stops, since a guider left running on a stopped mount chases the sky.
            await StopGuidingAsync(cancellationToken);
            string mount = await StopMountAsync();
            // Wait-for-open carries on once the roof reopens, so the flat panel is left for
            // PrepareFlatPanel to check then. Operate ends the night here, so it gets the end-of-night
            // close, which with the roof already shut only moves a cover allowed to move under it.
            if (isOperated) {
                await FlatPanelControl.CloseAfterRoofAsync(context, "the roof closed", hasAlreadyTried: false,
                    cancellationToken);
            }

            await context.ReportStartedImageFailedAsync("cycle aborted: the roof closed");

            // Fixed first line, since the server keys its alert cooldown on it.
            context.ReportCritical(
                "Roof closed while the autopilot was imaging. The mount was stopped, not parked.",
                new {
                    shutter,
                    roof_mode = context.RoofMode.ToString(),
                    mount,
                    action = isOperated
                        ? "autopilot disabled (check the roof and the mount before imaging again)"
                        : "waiting for safe conditions and for the roof to open again",
                });

            if (!isOperated) return AutopilotStateResult.Completed;

            context.SessionEndReason ??=
                $"The roof closed on its own while imaging (shutter {shutter}). " +
                "The mount was stopped where it was, not parked.";
            // The Dome's watchdog takes the roof back from here.
            context.IsRoofUnderAutopilotControl = false;
            return AutopilotStateResult.Failed;
        }

        private async Task<bool> IsRoofOpenAfterDomeReconnectAsync(CancellationToken cancellationToken) {
            if (context.Dome is not { IsConnected: false } dome) return false;
            if (!await context.EnsureConnectedAsync(dome, cancellationToken)) return false;
            return dome.IsShutterOpen();
        }

        private void StopExposure() {
            try {
                if (context.Camera is { IsConnected: true } camera) camera.StopExposure();
            } catch (Exception ex) {
                context.LogWarning($"Could not stop the exposure: {ex.Message}");
            }
        }

        private async Task StopGuidingAsync(CancellationToken cancellationToken) {
            if (context.Guider is not { IsConnected: true } guider) return;
            try {
                if (await guider.StopGuidingAsync(cancellationToken) == GuidingStopOutcome.NotConfirmed) {
                    context.LogWarning("Guider did not confirm it stopped. Stopping the mount anyway.");
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                context.LogWarning($"Could not stop guiding: {ex.Message}");
            }
        }

        /// <summary>Stops tracking and any slew, and describes the mount for the alert.</summary>
        private async Task<string> StopMountAsync() {
            if (context.Mount is not { IsConnected: true } mount) return "not connected";
            if (mount.IsParked) return "parked";
            try {
                if (await mount.StopMovementAsync()) return "stopped where it was, not parked";
                context.LogWarning("Mount did not confirm it stopped. Check the mount.");
                return "did not confirm it stopped";
            } catch (Exception ex) {
                context.LogWarning($"Could not stop the mount: {ex.Message}");
                return "could not be stopped";
            }
        }
    }
}
