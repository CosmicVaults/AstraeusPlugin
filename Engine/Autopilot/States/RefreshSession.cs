using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Renews the access token at dusk. Refresh recovers by itself, but renewing now means a failure shows up
    /// while someone may still be around, not at 3am.
    /// </summary>
    internal sealed class RefreshSession(string name, Context context) : StateBase(name) {

        // Runs at dusk, before the observing window opens.
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            // Checked before and after, since the refresh itself is where a refused version often shows.
            if (!context.Observatory.IsUpdateRequired
                && await context.Observatory.Authenticator.ForceRefreshAsync()) {
                return AutopilotStateResult.Completed;
            }

            if (context.Observatory.IsUpdateRequired) {
                context.SessionEndReason ??= "this Astraeus version is no longer supported";
                context.LogError("The server no longer supports this version of Astraeus, so the autopilot " +
                                 "will not run tonight. Install the latest from CosmicVaults.com and restart N.I.N.A.");
                return AutopilotStateResult.Failed;
            }

            context.LogError("Not signed in to Astraeus. Sign in from the plugin options to run tonight.");
            return AutopilotStateResult.Failed;
        }
    }
}
