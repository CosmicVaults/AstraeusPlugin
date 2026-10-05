using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    internal abstract class DecisionState(string name) : StateBase(name) {

        protected abstract Task<bool> DecideAsync(CancellationToken cancellationToken);

        public sealed override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
            => await DecideAsync(cancellationToken) ? AutopilotStateResult.Yes : AutopilotStateResult.No;
    }
}