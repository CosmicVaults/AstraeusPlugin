using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    internal abstract class StateBase(string name) {
        public string Name { get; } = name;

        private readonly Dictionary<AutopilotStateResult, StateBase> _transitions = new();

        ///////////// Transition wiring /////////////
        public StateBase OnCompleted(StateBase next) => On(AutopilotStateResult.Completed, next);
        public StateBase OnFailed(StateBase next)    => On(AutopilotStateResult.Failed, next);
        public StateBase OnPass(StateBase next)      => On(AutopilotStateResult.Pass, next);
        public StateBase OnYes(StateBase next)       => On(AutopilotStateResult.Yes, next);
        public StateBase OnNo(StateBase next)        => On(AutopilotStateResult.No, next);

        /// <summary>Throws if the same result is wired twice.</summary>
        public StateBase On(AutopilotStateResult result, StateBase next) {
            _transitions.Add(result, next);
            return this;
        }

        internal bool TryNext(AutopilotStateResult result, out StateBase next)
            => _transitions.TryGetValue(result, out next!);

        ///////////// Watchers this state opts into /////////////

        /// <summary>
        /// Runs this state under the safety watcher. It aborts with Unsafe when conditions turn, or with
        /// RoofClosed when a roof we're imaging under stops reading open. Only risky states turn it on.
        /// </summary>
        protected internal virtual bool ShouldWatchSafety => false;

        /// <summary>
        /// Runs this state under the mount-limit watcher, which aborts it with LimitReached. Only states
        /// where the mount is on a target turn it on.
        /// </summary>
        protected internal virtual bool ShouldWatchMountLimits => false;

        /// <summary>
        /// Runs this state under the daylight watcher, which aborts it with WindowClosed when the observing
        /// window closes. States that run outside the window turn it off.
        /// </summary>
        protected internal virtual bool ShouldWatchObservingWindow => true;

        public virtual Task OnEntryAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public abstract Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken);

        /// <summary>Always runs once on exit, even after an unsafe abort or a fault.</summary>
        public virtual Task OnExitAsync() => Task.CompletedTask;
    }
}
