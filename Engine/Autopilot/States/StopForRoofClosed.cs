using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// The roof stopped reading open while imaging, with the safety monitor still Safe, and IsRoofStillOpen
    /// has ruled out a dome that merely dropped off. Under Operate only the autopilot should move this roof,
    /// so it stops the exposure, guider and mount without parking and disables. A roof that isn't ours goes
    /// on to the emergency secure, as for unsafe conditions.
    /// </summary>
    internal sealed class StopForRoofClosed(string name, Context context) : StateBase(name)
    {
        // It's the response to the roof, so it finishes whatever the clock says.
        protected internal override Aborts AbortsOn => Aborts.None;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (context.RoofMode != RoofMode.Operate) return AutopilotStateResult.Completed;

            context.Roof.IsExpectedOpen = false;
            string shutter = context.Dome?.ShutterStatusName ?? "no dome";

            if (ImagingStop.Exposure(context) is Exception exposureError)
            {
                context.LogWarning($"Could not stop the exposure: {exposureError.Message}");
            }
            // Before the mount stops, since a guider left running on a stopped mount chases the sky.
            (GuidingStopOutcome guiding, Exception? guidingError) = await ImagingStop.GuidingAsync(context, cancellationToken);
            if (guidingError is not null)
            {
                context.LogWarning($"Could not stop guiding: {guidingError.Message}");
            }
            else if (guiding == GuidingStopOutcome.NotConfirmed)
            {
                context.LogWarning("Guider did not confirm it stopped. Stopping the mount anyway.");
            }
            string mount = await DescribeMountStopAsync();
            // The night ends here, so it gets the end-of-night close, which with the roof already shut
            // only moves a cover allowed to move under it.
            await FlatPanelControl.CloseAfterRoofAsync(context, "the roof closed", hasAlreadyTried: false,
                cancellationToken);

            await context.ReportStartedImageFailedAsync("cycle aborted: the roof closed");

            // Fixed first line, since the server keys its alert cooldown on it.
            context.ReportCritical(
                "Roof closed while the autopilot was imaging. The mount was stopped, not parked.",
                new
                {
                    shutter,
                    roof_mode = context.RoofMode.ToString(),
                    mount,
                    action = "autopilot disabled (check the roof and the mount before imaging again)",
                });

            context.Night.RecordEndReason(
                $"The roof closed on its own while imaging (shutter {shutter}). " +
                "The mount was stopped where it was, not parked.");
            context.Roof.Released();
            return AutopilotStateResult.Failed;
        }

        /// <summary>Stops tracking and any slew, and describes the mount for the alert.</summary>
        private async Task<string> DescribeMountStopAsync()
        {
            (ImagingStop.MountOutcome outcome, Exception? error) = await ImagingStop.MountAsync(context);
            switch (outcome)
            {
                case ImagingStop.MountOutcome.NotConnected:
                    return "not connected";
                case ImagingStop.MountOutcome.Parked:
                    return "parked";
                case ImagingStop.MountOutcome.Resting:
                    return "already still";
                case ImagingStop.MountOutcome.Stopped:
                    return "stopped where it was, not parked";
                case ImagingStop.MountOutcome.NotConfirmed:
                    context.LogWarning("Mount did not confirm it stopped. Check the mount.");
                    return "did not confirm it stopped";
                default:
                    context.LogWarning($"Could not stop the mount: {error?.Message}");
                    return "could not be stopped";
            }
        }
    }
}
