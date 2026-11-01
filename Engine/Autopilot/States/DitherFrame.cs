using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// Dithers only on a repeat frame. After a slew away and back, re-centring lands within the plate-solve
    /// threshold, tens of pixels against a 3-5 pixel dither, so the slew already did the job.
    /// </summary>
    internal sealed class DitherFrame(string name, Context context) : StateBase(name)
    {
        protected internal override Aborts AbortsOn => Aborts.All;

        // Which target the skip was last reported for, so a dither skipped for lack of guiding is said
        // once per target, not on every frame.
        private string? _skipReportedTargetKey;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (context.Frame.Target?.ShouldDither != true) return AutopilotStateResult.Completed;
            if (!context.Frame.HasSkippedSlew) return AutopilotStateResult.Completed;
            // After a flip N.I.N.A. restarts guiding on a new star, so the frame already lands somewhere
            // new and a dither would only cost another settle.
            if (context.Frame.HasFlipped) return AutopilotStateResult.Completed;

            // Without a running guider there's no lock position to move. We say so once per target, or
            // a mis-set project never dithers and nobody finds out.
            if (context.Guider is not { IsConnected: true, IsGuiding: true } guider)
            {
                string skipped = "Dither requested but the guider is not guiding, so frames of " +
                                 $"{context.Frame.Target?.TargetName ?? "the current target"} are not being dithered.";
                if (ShouldReportSkipped(context.Frame.Target))
                {
                    context.Log(skipped);
                }
                else
                {
                    context.LogDebug(skipped);
                }
                return AutopilotStateResult.Completed;
            }

            context.AutopilotStatus = AutopilotStatus.Dithering;
            try
            {
                await guider.DitherAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A dither that fails costs stacking quality, never the frame.
                context.LogWarning($"Dither threw ({ex.Message}), so capturing without it.");
            }

            return AutopilotStateResult.Completed;
        }

        private bool ShouldReportSkipped(AutopilotNextResponse? target)
        {
            string targetKey = $"{target?.TargetName}|{CentringTracker.KeyOf(target)}";
            if (targetKey == _skipReportedTargetKey) return false;
            _skipReportedTargetKey = targetKey;
            return true;
        }
    }
}
