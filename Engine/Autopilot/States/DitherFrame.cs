using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Dithers only on a repeat frame. After a slew away and back, re-centring lands within the plate-solve
    /// threshold, tens of pixels against a 3-5 pixel dither, so the slew already did the job.
    /// </summary>
    internal sealed class DitherFrame(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (context.CurrentTarget?.ShouldDither != true) return AutopilotStateResult.Completed;
            if (!context.HasSkippedSlew) return AutopilotStateResult.Completed;
            // After a flip N.I.N.A. restarts guiding on a new star, so the frame already lands somewhere
            // new and a dither would only cost another settle.
            if (context.HasFlippedThisFrame) return AutopilotStateResult.Completed;

            // Without a running guider there's no lock position to move. We say so once per target, or
            // a mis-set project never dithers and nobody finds out.
            if (context.Guider is not { IsConnected: true, IsGuiding: true } guider) {
                string skipped = "Dither requested but the guider is not guiding, so frames of " +
                                 $"{context.CurrentTarget?.TargetName ?? "the current target"} are not being dithered.";
                if (context.ShouldReportDitherSkipped()) {
                    context.Log(skipped);
                } else {
                    context.LogDebug(skipped);
                }
                return AutopilotStateResult.Completed;
            }

            context.AutopilotStatus = AutopilotStatus.Dithering;
            try {
                await guider.DitherAsync(cancellationToken);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                // A dither that fails costs stacking quality, never the frame.
                context.LogWarning($"Dither threw ({ex.Message}), so capturing without it.");
            }

            return AutopilotStateResult.Completed;
        }
    }
}
