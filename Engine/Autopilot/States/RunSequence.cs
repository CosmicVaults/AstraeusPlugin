using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal enum SequencePhase { Startup, Shutdown }

    /// <summary>Runs the user's optional N.I.N.A. start-up (dusk) or shutdown (dawn) sequence.</summary>
    internal sealed class RunSequence(string name, Context context, SequencePhase phase)
        : StateBase(name) {

        private string? _path;

        // The shutdown sequence runs because the window closed, so the daylight watcher would cancel it
        // at once and the machine would loop Secure -> Shutdown -> abort -> Secure forever. Start-up runs
        // inside the window and keeps the default watcher.
        protected internal override bool ShouldWatchObservingWindow => phase != SequencePhase.Shutdown;

        private string Label => phase == SequencePhase.Startup ? "Start up" : "Shutdown";

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            _path = phase == SequencePhase.Startup ? context.StartupSequencePath : context.ShutdownSequencePath;
            context.AutopilotStatus = phase == SequencePhase.Startup
                ? AutopilotStatus.RunningStartup
                : AutopilotStatus.RunningShutdown;
            if (_path != null)
                context.Log($"Running user-defined {Label} sequence.");
            return Task.CompletedTask;
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (_path == null)
                return AutopilotStateResult.Completed;

            bool hasCompleted = await context.RunSequenceAsync(_path, cancellationToken);
            if (hasCompleted) {
                context.Log($"{Label} sequence completed.");
                return AutopilotStateResult.Completed;
            }

            context.LogWarning($"{Label} sequence could not be completed.");
            return AutopilotStateResult.Failed;
        }
    }
}
