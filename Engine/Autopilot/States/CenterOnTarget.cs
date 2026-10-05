using System;
using NINA.Astrometry;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal sealed class CenterOnTarget(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!await context.EnsureConnectedAsync(context.Mount, cancellationToken) || context.Mount == null) {
                context.LogError("Mount is not connected, so cannot centre.");
                // Counted like any other failed centring, so the frame is not reported as centred.
                context.RecordCentringFailure("the mount is not connected");
                return AutopilotStateResult.Failed;
            }
            AutopilotNextResponse? target = context.CurrentTarget;
            if (target?.Ra is not double ra || target.Dec is not double dec) {
                context.Log("No coordinates provided, so skipping plate solve.");
                return AutopilotStateResult.Completed;
            }
            context.AutopilotStatus = AutopilotStatus.PlateSolving;
            Coordinates hint = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);

            bool isCentred;
            try {
                isCentred = await CenterWithDomeRetryAsync(context.Mount, hint, cancellationToken);
            } catch (OperationCanceledException) {
                throw; // a watcher (safety, mount limit or daylight) aborted, so let the machine handle it
            } catch (Exception ex) {
                // N.I.N.A.'s centring solver throws when the plate solve fails. Treat it as a recoverable
                // Failed so the machine doesn't fault and stop the whole run.
                context.LogError($"Plate solve / centring failed on {target.TargetName}: {ex.Message}");
                return CaptureWithoutCentring(target, ex.Message);
            }

            if (!isCentred) {
                context.LogError($"Plate solve / centring failed on {target.TargetName}.");
                return CaptureWithoutCentring(target, "the centring solver returned no solution");
            }

            // The mount's "Centred: RA=…, Separation=…" line has already said so.
            context.ClearCentringFailures();
            return AutopilotStateResult.Completed;
        }

        private async Task<bool> CenterWithDomeRetryAsync(Mount mount, Coordinates hint,
            CancellationToken cancellationToken) {
            try {
                return await mount.CenterAsync(hint, cancellationToken);
            } catch (Exception ex) when (Mount.IsDomeGeometryFailure(ex)) {
                // Only turning the dome failed, so the field is almost certainly centred. One more
                // centring checks it. Its first solve should land inside the threshold, so there's no
                // slew and no dome to turn.
                WarnDomeGeometryOnce(ex.Message);
                return await mount.CenterAsync(hint, cancellationToken);
            }
        }

        // A roll-off roof driver normally says it can't set azimuth, so N.I.N.A. leaves it alone. One
        // that claims it can (say a dome driver standing in for a roof) makes N.I.N.A. work out a dome
        // position from a radius nobody set, and throw.
        private void WarnDomeGeometryOnce(string message) {
            if (context.HasWarnedDomeGeometryTonight) {
                context.Log($"N.I.N.A. could not turn the dome after the centring slew again ({message}), so " +
                            "checking the centring once more.");
                return;
            }
            context.HasWarnedDomeGeometryTonight = true;
            context.LogWarning(
                $"N.I.N.A. could not turn the dome after the centring slew (\"{message}\"); checking the " +
                "centring once more. To stop this, set the dome radius in N.I.N.A.'s Dome options, or use a " +
                "roof driver that reports it cannot set its azimuth.");
        }

        // Failed still leads to the capture, since an observatory with no plate solver set up must
        // still get its frames. Counted so CheckTargetChanged retries once, and reported with the frame.
        private AutopilotStateResult CaptureWithoutCentring(AutopilotNextResponse target, string reason) {
            context.RecordCentringFailure(reason);
            context.LogWarning(
                $"Capturing {target.TargetName} on the mount's own coordinates without a plate solve. " +
                "The frame will be reported as not centred.");
            return AutopilotStateResult.Failed;
        }
    }
}