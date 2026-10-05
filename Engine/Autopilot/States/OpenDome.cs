using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// With RefuseUnsafeShutterMove on, N.I.N.A. won't open the shutter while the mount is unparked, and
    /// won't park for you, so we park first or the roof never opens. Also clears IsSessionSecured, since on
    /// the resume path the roof can be open for hours before anything unparks.
    /// </summary>
    internal sealed class OpenDome(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;

        // A roof that won't open fails the same way every pass, all night, with only an ERROR line each
        // time. Unsafe conditions aren't counted, since waiting those out is normal.
        private const int FailedOpensBeforeAlert = 3;
        private int _consecutiveFailedOpens;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            // Not open until it reads open, so the safety watcher doesn't mistake a roof still opening
            // for one that closed mid-night.
            context.IsRoofExpectedOpen = false;
            if (context.Dome == null) {
                context.LogError("Roof control is set to Operate but no dome is available, so cannot open the shutter.");
                return CountFailedOpen("no dome is available");
            }
            // Before anything else moves. A dropped dome gets one reconnect, and if it stays gone the
            // open fails here, before a park it could never use.
            if (!await context.EnsureConnectedAsync(context.Dome, cancellationToken)) {
                context.LogError("Dome is not connected, so cannot open the shutter.");
                return CountFailedOpen("the dome is not connected");
            }
            if (context.Dome.IsShutterOpen()) {
                _consecutiveFailedOpens = 0;
                context.IsSessionSecured = false;
                context.IsRoofExpectedOpen = true;
                return AutopilotStateResult.Completed;
            }
            if (!context.IsSafe()) {
                context.LogWarning("Tried to open dome shutter when unsafe");
                return AutopilotStateResult.Failed;
            }

            context.AutopilotStatus = AutopilotStatus.OpeningRoof;
            if (!await EnsureMountParkedForShutterAsync(cancellationToken)) {
                return CountFailedOpen("the mount could not be made ready for the roof to move");
            }

            // Logged like the close path, so a night's log shows the roof's every move.
            string reason = context.HasOpenedRoofTonight ? "conditions are safe again" : "the night is starting";
            context.Log($"Opening roof ({reason}).");
            Stopwatch watch = Stopwatch.StartNew();
            if (!await context.Dome.OpenShutterAsync(cancellationToken)) {
                return CountFailedOpen("the roof did not open");
            }
            context.Log($"Roof open in {Context.FormatElapsed(watch)} ({reason}).");
            _consecutiveFailedOpens = 0;
            context.HasOpenedRoofTonight = true;
            context.IsSessionSecured = false;
            context.IsRoofExpectedOpen = true;
            return AutopilotStateResult.Completed;
        }

        private AutopilotStateResult CountFailedOpen(string reason) {
            _consecutiveFailedOpens++;
            if (_consecutiveFailedOpens == FailedOpensBeforeAlert) {
                // Fixed first line, since the server keys its alert cooldown on it.
                context.ReportCritical("Autopilot cannot open the roof.",
                    new {
                        reason,
                        attempts = _consecutiveFailedOpens,
                        roof = context.Dome?.ShutterStatusName ?? "no dome",
                    });
            }
            return AutopilotStateResult.Failed;
        }

        /// <summary>
        /// Returns false when the open can't succeed, including for a disconnected mount, since N.I.N.A.
        /// can't confirm it's clear of the roof.
        /// </summary>
        private async Task<bool> EnsureMountParkedForShutterAsync(CancellationToken cancellationToken) {
            if (!context.IsShutterMoveRefusedWithUnparkedMount) return true;

            if (!await context.EnsureConnectedAsync(context.Mount, cancellationToken)
                || context.Mount is not { } mount) {
                context.LogError(
                    "The roof will not open: N.I.N.A. refuses shutter moves with the mount unparked, and the " +
                    "mount is not connected to confirm it is parked.");
                return false;
            }

            if (mount.IsParked) return true;

            // Off by default, since parking here slews the mount under a closed roof. N.I.N.A. won't do
            // it for the same reason, as a park position only reachable with the roof open means a
            // collision. A stalled night can be recovered. Bent metal can't.
            if (!context.ShouldParkMountToOpenRoof) {
                context.LogError(
                    "The roof will not open: N.I.N.A. refuses shutter moves with the mount unparked. Park " +
                    "the mount, or turn on 'Park mount to open roof' if the park position is safe under a " +
                    "closed roof.");
                return false;
            }

            context.LogWarning(
                "Parking the mount under the closed roof so N.I.N.A. will open it ('Park mount to open " +
                "roof' is on).");
            try {
                await mount.ParkAsync(cancellationToken);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                context.LogError($"Could not park the mount before opening the roof: {ex.Message}");
                return false;
            }

            if (!mount.IsParked) {
                context.LogError(
                    "Mount does not report AtPark after the park attempt. Not opening the roof, as N.I.N.A. " +
                    "would refuse it anyway. Check the mount.");
                return false;
            }

            context.Log("Mount parked. Opening the roof.");
            return true;
        }
    }
}
