using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// The roof watcher tripped, with the safety monitor still Safe. Did the dome merely drop off and come
    /// back with the roof still open? A dome that dropped reads as "not open", so it gets one reconnect, and
    /// if the shutter then reads Open the roof was never closed and the night carries on from the next poll.
    /// Anything else is a real closing, and StopForRoofClosed follows.
    /// </summary>
    internal sealed class IsRoofStillOpen(string name, Context context) : DecisionState(name)
    {
        // It's the response to the roof, so it finishes whatever the clock says.
        protected internal override Aborts AbortsOn => Aborts.None;

        protected override async Task<bool> DecideAsync(CancellationToken cancellationToken)
        {
            if (context.Dome is not { IsConnected: false } dome) return false;
            if (!await context.Devices.EnsureConnectedAsync(dome, cancellationToken)) return false;
            if (!dome.IsShutterOpen()) return false;

            context.LogWarning(
                "The dome dropped off and has reconnected with the roof still open, so carrying on imaging.");
            await context.ReportStartedImageFailedAsync("cycle aborted: the dome dropped off");
            return true;
        }
    }
}
