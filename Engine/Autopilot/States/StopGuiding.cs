using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Stops guiding before anything moves the mount, since a guider left running fights the slew. Never
    /// fails the run. A guider that won't stop is logged and the slew goes ahead.
    /// </summary>
    internal sealed class StopGuiding(string name, Context context) : StateBase(name) {
        // The roof is open and the mount on target, as in the other watched states.
        protected internal override bool ShouldWatchSafety => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (context.Guider is not { IsConnected: true } guider) {
                return AutopilotStateResult.Completed;
            }

            try {
                if (await guider.StopGuidingAsync(cancellationToken) == GuidingStopOutcome.NotConfirmed) {
                    context.LogWarning("Guider did not confirm it stopped. Slewing anyway.");
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                context.LogWarning($"Stopping guiding threw ({ex.Message}). Slewing anyway.");
            }

            return AutopilotStateResult.Completed;
        }
    }
}
