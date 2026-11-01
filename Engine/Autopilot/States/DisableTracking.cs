using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// Stops the guider, then the mount's tracking and any slew. The guider goes first because, left running
    /// on a stopped mount, it chases the sky and still reports itself as guiding.
    /// </summary>
    internal sealed class DisableTracking(string name, Context context) : StateBase(name)
    {
        protected internal override Aborts AbortsOn => Aborts.None;   // A stop is always worth finishing.

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            // A still or disconnected mount with no guiding running has nothing to stop. 
            if (context.Mount is { IsConnected: false } or { IsResting: true }
                && context.Guider is not { IsGuiding: true })
            {
                return AutopilotStateResult.Completed;
            }

            (GuidingStopOutcome guiding, Exception? guidingError) = await ImagingStop.GuidingAsync(context, cancellationToken);
            if (guidingError is not null)
            {
                context.LogWarning($"Stopping guiding threw: {guidingError.Message}; stopping tracking anyway.");
            }
            else if (guiding == GuidingStopOutcome.NotConfirmed)
            {
                context.LogWarning("Guider did not confirm it stopped; stopping tracking anyway.");
            }

            if (context.Mount == null)
            {
                context.LogWarning("Mount component not available; nothing to stop.");
                return AutopilotStateResult.Failed;
            }

            // A disconnected, parked or still mount has nothing to stop, and the shared stop leaves it
            // alone.
            (ImagingStop.MountOutcome outcome, Exception? error) = await ImagingStop.MountAsync(context);
            switch (outcome)
            {
                case ImagingStop.MountOutcome.NotConnected:
                case ImagingStop.MountOutcome.Parked:
                case ImagingStop.MountOutcome.Resting:
                case ImagingStop.MountOutcome.Stopped:
                    return AutopilotStateResult.Completed;
                case ImagingStop.MountOutcome.NotConfirmed:
                    context.LogWarning("Unable to stop telescope movement for unknown reason.");
                    return AutopilotStateResult.Failed;
                default:
                    context.LogWarning($"Could not stop the mount: {error?.Message}");
                    return AutopilotStateResult.Failed;
            }
        }
    }
}
