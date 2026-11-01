using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    internal abstract class StateBase(string name)
    {
        public string Name { get; } = name;

        public TransitionsTable Transitions { get; } = new();

        ///////////// Transition wiring /////////////
        public StateBase OnCompleted(StateBase next) => On(AutopilotStateResult.Completed, next);
        public StateBase OnFailed(StateBase next) => On(AutopilotStateResult.Failed, next);
        public StateBase OnPass(StateBase next) => On(AutopilotStateResult.Pass, next);
        public StateBase OnYes(StateBase next) => On(AutopilotStateResult.Yes, next);
        public StateBase OnNo(StateBase next) => On(AutopilotStateResult.No, next);
        public StateBase OnUnsafe(StateBase next) => On(AutopilotStateResult.Unsafe, next);
        public StateBase OnRoofClosed(StateBase next) => On(AutopilotStateResult.RoofClosed, next);
        public StateBase OnLimitReached(StateBase next) => On(AutopilotStateResult.LimitReached, next);
        public StateBase OnWindowClosed(StateBase next) => On(AutopilotStateResult.WindowClosed, next);

        /// <summary>
        /// Shortcut to adding transition to Transition Table
        /// </summary>
        public StateBase On(AutopilotStateResult result, StateBase next)
        {
            Transitions.On(result, next);
            return this;
        }

        ///////////// Aborts this state opts into /////////////

        /// <summary>
        /// The watchers that may abort this state, each injecting the result of the same name. 
        /// </summary>
        protected internal virtual Aborts AbortsOn => Aborts.RoofClosed | Aborts.WindowClosed;

        public virtual Task OnEntryAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public abstract Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken);

        /// <summary>Always runs once on exit, even after an unsafe abort or a fault.</summary>
        public virtual Task OnExitAsync() => Task.CompletedTask;
    }
}
