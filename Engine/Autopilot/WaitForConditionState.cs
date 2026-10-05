using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// The machine's watcher handles daylight and stop aborts, so subclasses only supply the
    /// condition, status and message.
    /// </summary>
    internal abstract class WaitForConditionState(string name) : StateBase(name) {
        protected abstract Context RunContext { get; }

        protected abstract AutopilotStatus Status { get; }

        /// <summary>May attempt connections etc. as a side effect.</summary>
        protected abstract Task<bool> IsSatisfiedAsync(CancellationToken cancellationToken);

        protected abstract string WaitingMessage();

        protected virtual int PollIntervalSeconds => Context.SafetyPollIntervalSeconds;

        public sealed override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            RunContext.AutopilotStatus = Status;
            // A "still waiting" line on the first unsatisfied poll and every Nth after, so an overnight
            // wait stays visibly alive in the log without a line every poll.
            int pollCount = 0;
            while (!await IsSatisfiedAsync(cancellationToken)) {
                if (pollCount++ % Context.WaitHeartbeatPolls == 0) RunContext.Log(WaitingMessage());
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), cancellationToken);
            }
            return AutopilotStateResult.Completed;
        }
    }
}