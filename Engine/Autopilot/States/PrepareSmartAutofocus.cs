using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Turns N.I.N.A.'s filter-wheel offsets off again, in case the user switched them back on, and pulls the
    /// server's offset table into N.I.N.A.'s filter settings. An unreachable server doesn't stop the night.
    /// </summary>
    internal sealed class PrepareSmartAutofocus(string name, Context context) : StateBase(name) {

        // Runs at dusk, before the observing window opens.
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!context.IsSmartAutofocusEnabled)
                return AutopilotStateResult.Completed;

            context.Observatory.Settings.EnforceSmartAutofocusProfile();

            if (context.Observatory.TryGetComponent<Settings>(out Settings? settings)) {
                await settings.PullAutofocusOffsetsAsync();
            } else {
                context.LogWarning(
                    "Smart Autofocus: settings component unavailable, so N.I.N.A.'s filter offsets were not refreshed.");
            }

            return AutopilotStateResult.Completed;
        }
    }
}
