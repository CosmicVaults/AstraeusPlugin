using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>
    /// Stands in for the imaging chain: every watcher is on, as it is for an exposure, and it runs until a watcher or
    /// the stop ends it, or for a fixed stretch of the clock when a test gives one.
    /// </summary>
    internal sealed class ExposingState(string name, List<string> trace, TimeSpan? duration = null, TimeProvider? clock = null)
        : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            trace.Add($"{Name}: entry");
            return Task.CompletedTask;
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            trace.Add($"{Name}: execute");
            if (duration is TimeSpan stretch && clock != null) {
                await Task.Delay(stretch, clock, cancellationToken);
            } else {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return AutopilotStateResult.Completed;
        }

        public override Task OnExitAsync() {
            trace.Add($"{Name}: exit");
            return Task.CompletedTask;
        }
    }
}