using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    internal sealed class WaitForDomeOpen(string name, Context context) : WaitForConditionState(name)
    {
        protected override Context RunContext => context;

        protected override AutopilotStatus Status => AutopilotStatus.WaitingRoof;
        protected override async Task<bool> IsSatisfiedAsync(CancellationToken cancellationToken)
        {
            // A dropped dome can't show the roof opening, so it gets a reconnect (at most once per
            // ReconnectCooldown) and the wait goes on either way.
            await context.Devices.EnsureConnectedAsync(context.Dome, cancellationToken);
            // A null dome is a misconfiguration. Treat it as not open so we keep waiting and never image
            // without a roof.
            bool isOpen = context.Dome?.IsShutterOpen() ?? false;
            if (isOpen) context.Roof.SeenOpen(didAutopilotOpenIt: false);
            return isOpen;
        }

        protected override string WaitingMessage()
        {
            return "Waiting for Dome Shutter to Open";
        }
    }
}