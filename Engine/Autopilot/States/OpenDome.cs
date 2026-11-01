using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// Opens the roof under Operate, after ParkMountForRoofOpen has made the mount ready where N.I.N.A.
    /// needs it parked. Also clears the night's secured flag, since on the resume path the roof can be open
    /// for hours before anything unparks.
    /// </summary>
    internal sealed class OpenDome(string name, Context context) : StateBase(name)
    {
        protected internal override Aborts AbortsOn => Aborts.Unsafe | Aborts.RoofClosed | Aborts.WindowClosed;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            context.Roof.Opening();
            if (context.Dome == null)
            {
                context.LogError("Roof control is set to Operate but no dome is available, so cannot open the shutter.");
                return CountFailedOpen("no dome is available");
            }
            // Before anything else moves. A dropped dome gets one reconnect, and if it stays gone the
            // open fails here, before a park it could never use.
            if (!await context.Devices.EnsureConnectedAsync(context.Dome, cancellationToken))
            {
                context.LogError("Dome is not connected, so cannot open the shutter.");
                return CountFailedOpen("the dome is not connected");
            }
            if (context.Dome.IsShutterOpen())
            {
                context.Roof.RecordOpened();
                context.Night.IsSecured = false;
                context.Roof.SeenOpen(didAutopilotOpenIt: false);
                return AutopilotStateResult.Completed;
            }
            if (!context.IsSafe())
            {
                context.LogWarning("Tried to open dome shutter when unsafe");
                return AutopilotStateResult.Failed;
            }

            context.AutopilotStatus = AutopilotStatus.OpeningRoof;

            // Logged like the close path, so a night's log shows the roof's every move.
            string reason = context.Roof.HasOpenedTonight ? "conditions are safe again" : "the night is starting";
            context.Log($"Opening roof ({reason}).");
            Stopwatch watch = Stopwatch.StartNew();
            if (!await context.Dome.OpenShutterAsync(cancellationToken))
            {
                return CountFailedOpen("the roof did not open");
            }
            context.Log($"Roof open in {Context.FormatElapsed(watch)} ({reason}).");
            context.Roof.RecordOpened();
            context.Night.IsSecured = false;
            context.Roof.SeenOpen(didAutopilotOpenIt: true);
            return AutopilotStateResult.Completed;
        }

        private AutopilotStateResult CountFailedOpen(string reason)
        {
            if (context.Roof.RecordFailedOpen()) ReportCannotOpen(context, reason);
            return AutopilotStateResult.Failed;
        }

        /// <summary>
        /// The alert for a roof that will not open, shared with the park before it. Fixed first line, since
        /// the server keys its alert cooldown on it.
        /// </summary>
        internal static void ReportCannotOpen(Context context, string reason)
        {
            context.ReportCritical("Autopilot cannot open the roof.",
                new
                {
                    reason,
                    attempts = context.Roof.FailedOpenCount,
                    roof = context.Dome?.ShutterStatusName ?? "no dome",
                });
        }
    }
}
