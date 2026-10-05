using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Closes the night's reported session after the shutdown sequence, before waiting for dusk again.
    /// It's what makes an autopilot left on all week report each night.
    /// </summary>
    internal sealed class EndSession(string name, Context context) : StateBase(name) {

        // Required. This runs because the window closed, so the default watcher would cancel it at once
        // and the machine would loop Secure -> Shutdown -> EndSession -> abort -> Secure forever, mailing
        // a session end each time. RunSequence opts out for its shutdown phase for the same reason.
        protected internal override bool ShouldWatchObservingWindow => false;

        // Sets no AutopilotStatus on purpose. See StartSession.
        public override Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            context.ReportSessionEnd();
            return Task.FromResult(AutopilotStateResult.Completed);
        }
    }
}
