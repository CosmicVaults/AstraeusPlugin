using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>
    /// A stand-in for a state. It returns what the test says and writes what happened to a shared trace, so
    /// a routing test can read the exact order of entries, executions and exits. It opts out of every
    /// watcher, since the watchers read devices and the routing tests have none.
    /// </summary>
    internal sealed class FakeState(string name, List<string> trace,
        Func<CancellationToken, Task<AutopilotStateResult>> execute) : StateBase(name) {

        /// <summary>A state that returns the same result every time.</summary>
        public FakeState(string name, List<string> trace, AutopilotStateResult result)
            : this(name, trace, _ => Task.FromResult(result)) { }

        public int Executions { get; private set; }

        protected internal override bool ShouldWatchRoof => false;
        protected internal override bool ShouldWatchObservingWindow => false;

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            trace.Add($"{Name}: entry");
            return Task.CompletedTask;
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            trace.Add($"{Name}: execute");
            Executions++;
            return await execute(cancellationToken);
        }

        public override Task OnExitAsync() {
            trace.Add($"{Name}: exit");
            return Task.CompletedTask;
        }
    }
}