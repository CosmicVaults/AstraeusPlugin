using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>Waits the scheduler's RetryAfter, or fixedSeconds for other "try again shortly" edges.</summary>
    internal sealed class WaitForNextPoll(string name, Context context, int? fixedSeconds = null)
        : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;

        private int _waitSeconds;

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            // Set by PollForTarget, since CurrentTarget is already null here. Clamped because Task.Delay
            // throws on a negative span, and a server bug mustn't fault the machine.
            _waitSeconds = Math.Max(0, fixedSeconds ?? context.NextPollDelaySeconds);
            return Task.CompletedTask;
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            await Task.Delay(TimeSpan.FromSeconds(_waitSeconds), cancellationToken);
            return AutopilotStateResult.Completed;
        }
    }
}