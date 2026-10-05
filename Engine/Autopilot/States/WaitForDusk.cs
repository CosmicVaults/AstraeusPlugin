using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal sealed class WaitForDusk(string name, Context context) : WaitForConditionState(name) {
        protected override Context RunContext => context;

        protected internal override bool ShouldWatchObservingWindow => false;

        // Clears last night's autofocus history so the first frame gets a start-of-night focus again.
        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            context.ResetAutofocusTracking();
            return base.OnEntryAsync(cancellationToken);
        }

        protected override AutopilotStatus Status => AutopilotStatus.WaitingForNight;

        protected override Task<bool> IsSatisfiedAsync(CancellationToken cancellationToken) {
            return Task.FromResult(!context.IsOutsideObservingWindow());
        }

        protected override string WaitingMessage() {
            return "Waiting for Dusk";
        }
    }
}
