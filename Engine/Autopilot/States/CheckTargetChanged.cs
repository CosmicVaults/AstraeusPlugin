using NINA.Astrometry;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Skips the slew and plate solve when the mount is already centred and rotated for the target. The
    /// scheduler re-issues the same target for every sub-frame, so otherwise we'd solve before every one.
    /// </summary>
    internal sealed class CheckTargetChanged(string name, Context context) : DecisionState(name) {
        protected override Task<bool> DecideAsync(CancellationToken cancellationToken) {
            AutopilotNextResponse? target = context.CurrentTarget;

            // After a flip N.I.N.A. slews back to these coordinates, so the mount reports itself on
            // target even if the field is rotated 180° or the post-flip re-centre never ran. Only a plate
            // solve would notice, so take the slewing branch. A last frame that solved off target asks
            // for the same. Both flags are cleared here so only this one frame pays for it.
            if (context.IsRecenterRequired || context.IsLastFrameOffTarget) {
                context.IsRecenterRequired = false;
                context.IsLastFrameOffTarget = false;
                context.HasSkippedSlew = false;
                context.Log(
                    $"Re-centring {target?.TargetName ?? "the current target"}, " +
                    "slewing and plate solving rather than trusting where the mount says it is pointing.");
                return Task.FromResult(true);
            }

            // The last frame here was taken without a working centring. We try once more, then capture
            // on the mount's own coordinates instead of spending a solve on every frame.
            if (context.IsCentringRetryDue) {
                context.HasSkippedSlew = false;
                context.Log(
                    $"Centring failed on the last frame of {target?.TargetName ?? "the current target"} " +
                    $"({context.CentringFailureReason}), so retrying the slew and plate solve once.");
                return Task.FromResult(true);
            }
            if (context.ShouldAnnounceCentringGivenUp()) {
                context.LogWarning(
                    $"Centring is unavailable for {target?.TargetName ?? "the current target"} " +
                    $"({context.CentringFailureReason}), so capturing on the mount's own coordinates " +
                    "until the target changes. Frames are reported to the server as not centred.");
            }

            if (context.Mount == null || target?.Ra is not double ra || target.Dec is not double dec) {
                context.HasSkippedSlew = false;
                return Task.FromResult(true);
            }

            Coordinates coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
            if (!context.Mount.IsPointingAt(coordinates, context.RecenterToleranceDegrees)) {
                context.HasSkippedSlew = false;
                return Task.FromResult(true); // Yes -> slew + rotate + centre
            }

            if (HasRotationChanged(out string? reason)) {
                context.HasSkippedSlew = false;
                context.Log($"Already centred on {target.TargetName}, but {reason}, so slewing anyway.");
                return Task.FromResult(true);
            }

            context.HasSkippedSlew = true;
            // "Within tolerance" was judged on the mount's own coordinates. Once centring is given up
            // that's all we have, so the log line doesn't claim a centring.
            context.Log(context.IsCentringGivenUp
                ? $"Staying on {target.TargetName} at the mount's own coordinates. Centring is " +
                  "unavailable for it, so no slew or plate solve."
                : $"Already centred on {target.TargetName} (within {context.RecenterToleranceArcmin:F1}'), so " +
                  "skipping slew and plate solve.");
            return Task.FromResult(false); // No -> skip to capture
        }

        private bool HasRotationChanged(out string? reason) {
            reason = null;
            if (context.Rotator is not { IsConnected: true } rotator) return false;

            double tolerance = context.RotationToleranceDegrees;

            if (context.RequestedSkyAngle() is double skyAngle) {
                if (!rotator.IsSynced || rotator.SkyPosition is not float currentAngle) return false;
                if (ShortestArc(skyAngle, currentAngle) <= tolerance) return false;
                reason = $"the requested position angle {skyAngle:F2}° differs from the current {currentAngle:F2}°";
                return true;
            }

            if (context.RequestedMechanicalAngle() is double mechanicalAngle) {
                if (rotator.MechanicalPosition is not float currentAngle) return false;
                if (ShortestArc(mechanicalAngle, currentAngle) <= tolerance) return false;
                reason = $"the requested mechanical angle {mechanicalAngle:F2}° differs from the current " +
                         $"{currentAngle:F2}°";
                return true;
            }

            return false;
        }

        private static double ShortestArc(double firstDegrees, double secondDegrees) =>
            Math.Abs(AstroUtil.EuclidianModulus(firstDegrees - secondDegrees + 180, 360) - 180);
    }
}
