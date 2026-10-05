using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Runs after autofocus because N.I.N.A.'s autofocus stops and restarts guiding around its sweep. Asks
    /// the guider every frame, since one stopped outside N.I.N.A. would otherwise stay stopped all night.
    /// </summary>
    internal sealed class StartGuiding(string name, Context context) : StateBase(name) {
        // Confirming a running guide is one round trip. Anything slower was a real start (a guide star
        // found and settled on), so it gets its own line.
        private static readonly TimeSpan ConfirmationTime = TimeSpan.FromSeconds(5);

        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            await context.EnsureConnectedAsync(context.Guider, cancellationToken);
            if (context.Guider is not { IsConnected: true } guider) {
                return AutopilotStateResult.Completed;
            }

            bool wasGuiding = guider.IsGuiding;
            context.AutopilotStatus = AutopilotStatus.Guiding;
            Stopwatch watch = Stopwatch.StartNew();
            try {
                if (!await guider.StartGuidingAsync(cancellationToken)) {
                    context.LogWarning(
                        $"Could not start guiding after {Context.FormatElapsed(watch)}, so capturing unguided.");
                } else if (!wasGuiding) {
                    // Settle time is the hidden cost of a slew-heavy schedule, so log it to make a night
                    // spent settling visible.
                    context.Log($"Guiding settled in {Context.FormatElapsed(watch)}.");
                } else if (watch.Elapsed >= ConfirmationTime) {
                    context.LogWarning(
                        "Guiding had stopped or lost its star since the last frame, and was restarted. " +
                        $"Settled in {Context.FormatElapsed(watch)}.");
                } else {
                    context.LogDebug("Guiding is still running.");
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                context.LogWarning($"Starting guiding threw ({ex.Message}), so capturing unguided.");
            }

            return AutopilotStateResult.Completed;
        }
    }
}
