using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Stops the guider, then the mount's tracking and any slew. The guider goes first because, left running
    /// on a stopped mount, it chases the sky and still reports itself as guiding.
    /// </summary>
    internal sealed class DisableTracking(string name, Context context) : StateBase(name) {
        // A stop is always worth finishing. It's also the first step of the mount-limit abort, and if
        // cancelled at dawn a limit breach would pass for a routine end of night.
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            // A still or disconnected mount with no guiding running has nothing to stop. Otherwise we'd
            // ask the guider once a minute through an empty night.
            if (context.Mount is { IsConnected: false } or { IsResting: true }
                && context.Guider is not { IsGuiding: true }) {
                return AutopilotStateResult.Completed;
            }

            if (context.Guider is { IsConnected: true } guider) {
                try {
                    if (await guider.StopGuidingAsync(cancellationToken) == GuidingStopOutcome.NotConfirmed) {
                        context.LogWarning("Guider did not confirm it stopped; stopping tracking anyway.");
                    }
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    context.LogWarning($"Stopping guiding threw: {ex.Message}; stopping tracking anyway.");
                }
            }

            if (context.Mount == null) {
                context.LogWarning("Mount component not available; nothing to stop.");
                return AutopilotStateResult.Failed;
            }
            if (!context.Mount.IsConnected) {
                return AutopilotStateResult.Completed;
            }
            // Parked, or tracking off and not slewing, so nothing to stop. This state follows every
            // empty poll, and stopping a still mount made N.I.N.A. refuse the tracking change when
            // parked and log "Mount movement stopped." for a mount that never moved, once a minute.
            if (context.Mount is { IsConnected: true, IsResting: true }) {
                return AutopilotStateResult.Completed;
            }
            // The async stop gives the driver time to report it. Read straight back, a mount still
            // slowing down looks like one that refused.
            if (await context.Mount.StopMovementAsync()) {
                return AutopilotStateResult.Completed;
            }
            context.LogWarning("Unable to stop telescope movement for unknown reason.");
            return AutopilotStateResult.Failed;
        }
    }
}
