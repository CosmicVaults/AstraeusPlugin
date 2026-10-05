using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Opens the night's reported session at dusk, since to an observer "the autopilot started" means a night
    /// is beginning. Runs before the start-up sequence so one that fails reports "started" then "failed".
    /// </summary>
    internal sealed class StartSession(string name, Context context) : StateBase(name) {

        // Runs at dusk before the observing window opens, like RefreshSession and PrepareSmartAutofocus.
        // An abort part-way through would leave the night half-opened.
        protected internal override bool ShouldWatchObservingWindow => false;

        // Sets no AutopilotStatus, so the dashboard stays on WaitingForNight until ConnectSwitch moves
        // it on. A status for this one quick step would only flicker.
        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            await context.StartSessionAsync();
            return AutopilotStateResult.Completed;
        }
    }
}
