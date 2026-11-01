using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// Reads the captured frame's own plate solve against the pointing tolerance. Only the frame's solve can
    /// tell that the field is 2° off from where the mount says it is. A miss beyond tolerance sends the next
    /// frame through a slew and solve. The limit is never tighter than N.I.N.A.'s pointing tolerance, since
    /// centring stops anywhere inside that. Once centring has failed on this target we only log the miss, as
    /// each retry costs minutes. Always Completed, since the frame is on disk whatever the verdict.
    /// </summary>
    internal sealed class CheckFramePointing(string name, Context context) : StateBase(name)
    {
        // The frame is on disk, so neither dawn nor the roof may cut the report after this short. The
        // mount is still tracking the target.
        protected internal override Aborts AbortsOn => Aborts.LimitReached;

        public override Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (context.Frame.Target is { } target && context.Frame.LastCapture is { } result)
            {
                CheckPointing(target, result);
            }
            return Task.FromResult(AutopilotStateResult.Completed);
        }

        private void CheckPointing(AutopilotNextResponse target, CaptureResult result)
        {
            if (result.PointingOffsetArcmin is not double offsetArcmin) return;
            double limitArcmin = Math.Max(context.RecenterToleranceArcmin, context.PointingToleranceArcmin);
            if (offsetArcmin <= limitArcmin) return;
            string miss = DescribeMiss(offsetArcmin, limitArcmin);
            if (context.Night.Centring.IsGivenUp)
            {
                context.Log(
                    $"Frame solved {miss} from {target.TargetName}. " +
                    "Centring is unavailable for this target, so the next frame is not re-centred.");
                return;
            }
            context.IsLastFrameOffTarget = true;
            context.LogWarning(
                $"Frame solved {miss} from {target.TargetName}, so the next frame will slew and plate solve " +
                "before exposing.");
        }

        // Two decimals, or three when two would print a miss just over the tolerance as equal to it.
        private static string DescribeMiss(double offsetArcmin, double limitArcmin)
        {
            string format = offsetArcmin.ToString("F2", CultureInfo.InvariantCulture)
                            == limitArcmin.ToString("F2", CultureInfo.InvariantCulture) ? "F3" : "F2";
            return $"{offsetArcmin.ToString(format, CultureInfo.InvariantCulture)}' (tolerance " +
                   $"{limitArcmin.ToString(format, CultureInfo.InvariantCulture)}')";
        }
    }
}
