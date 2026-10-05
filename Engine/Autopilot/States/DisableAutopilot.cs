using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal sealed class DisableAutopilot(string name, Context context) : StateBase(name) {
        
        public override Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            // Only the machine giving up reaches here, since a user switching off cancels the token
            // instead. So if nothing upstream recorded a cause, the session still ends as a failure.
            context.SessionEndReason ??= "The autopilot disabled itself and will not image again tonight.";
            context.DisableAutopilot();
            context.AutopilotStatus = AutopilotStatus.Disabled;
            return Task.FromResult(AutopilotStateResult.Disabled);
        }
    }
}