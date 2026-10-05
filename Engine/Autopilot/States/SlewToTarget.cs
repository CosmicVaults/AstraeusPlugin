using NINA.Astrometry;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal sealed class SlewToTarget(string name, Context context) : StateBase(name) {
        private const double OnTargetToleranceDegrees = 1.0;

        protected internal override bool ShouldWatchSafety => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {

            // Failed here ends in the secure-and-disable path, so each refusal names itself for the
            // session-end email.
            if (context.Mount == null) {
                context.SessionEndReason = "Mount component not available, so cannot slew.";
                context.LogError(context.SessionEndReason);
                return AutopilotStateResult.Failed;
            }
            if (!await context.EnsureConnectedAsync(context.Mount, cancellationToken)) {
                context.SessionEndReason = "Mount is not connected, so cannot slew.";
                context.LogError(context.SessionEndReason);
                return AutopilotStateResult.Failed;
            }
            if (context.Mount.IsParked) {
                context.SessionEndReason = "Mount is still parked. Unable to slew.";
                context.LogError(context.SessionEndReason);
                return AutopilotStateResult.Failed;
            }
            AutopilotNextResponse? target = context.CurrentTarget;
            if (target?.Ra is not double ra || target.Dec is not double dec) {
                context.Log("No coordinates provided, so skipping slew.");
                return AutopilotStateResult.Completed;
            }
            
            context.AutopilotStatus = AutopilotStatus.Slewing;
            
            Coordinates coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
            context.Log($"Slewing to {target.TargetName} (RA {ra:F4}°, Dec {dec:F4}°).");
            bool hasSlewed = await context.Mount.SlewToAsync(coordinates, cancellationToken);
            if (!hasSlewed) {
                context.SessionEndReason = $"Slew to {target.TargetName} failed.";
                context.LogError(context.SessionEndReason);
                return AutopilotStateResult.Failed;
            }
            bool isTrackingSet = context.Mount.SetTracking(Mount.ParseTrackingMode(target.TrackingType));
            if (!isTrackingSet) {
                context.LogWarning($"Unable to set tracking to {target.TrackingType}. Continuing.");
            }

            // Checked against the live position once the mount reports itself on target, so a stale
            // pre-slew reading can't look like a breach.
            //
            // Altitude and horizon only. A meridian breach here is for the flip a few states along to
            // fix. If no flip is possible, the limit watcher catches it in the following states anyway.
            if (context.Mount.IsPointingAt(coordinates, OnTargetToleranceDegrees)
                && context.Mount.DescribeCurrentPositionBreach(shouldIncludeMeridian: false) is string breach) {
                context.LastLimitBreach = $"Mount limit reached on {target.TargetName}: {breach}";
                context.SessionEndReason = context.LastLimitBreach;
                context.LogError($"{context.LastLimitBreach}. Parking, closing the roof and disabling the autopilot.");
                return AutopilotStateResult.Failed;
            }

            return AutopilotStateResult.Completed;
        }
    }
}