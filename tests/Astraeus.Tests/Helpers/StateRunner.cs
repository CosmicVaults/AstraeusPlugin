using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    internal static class StateRunner {
        /// <summary>Runs one state as the machine does: entry, execute, and exit whatever happened.</summary>
        public static async Task<AutopilotStateResult> RunAsync(StateBase state, CancellationToken cancellationToken = default) {
            await state.OnEntryAsync(cancellationToken);
            try {
                return await state.ExecuteAsync(cancellationToken);
            } finally {
                await state.OnExitAsync();
            }
        }
    }
}