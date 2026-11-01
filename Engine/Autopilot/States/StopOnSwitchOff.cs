using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// The deliberate stop. Someone switching the autopilot off is usually taking manual control, so this
    /// stops the exposure, guiding and tracking, and nothing else: no park and no roof. The session-end
    /// email warns them instead of a separate alert, which on every switch-off would teach people to
    /// ignore alerts. Never throws, since it is the machine's last word on the way out.
    /// </summary>
    internal sealed class StopOnSwitchOff(string name, Context context) : StateBase(name)
    {
        protected internal override Aborts AbortsOn => Aborts.None;

        private bool IsRoofInUseTonight()
        {
            return context.Roof.HasOpenedTonight || (context.Night.IsStartupComplete && context.Night.IsOpen);
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {

            // 1. Stop Exposing
            if (ImagingStop.Exposure(context) is Exception exposureError)
            {
                context.LogWarning($"Could not stop the exposure while shutting down: {exposureError.Message}");
            }

            // 2. Stop guiding. Before the mount, or the guider chases the drifting sky. On no token, since a
            // deliberate stop finishes whatever cancelled the run.
            (GuidingStopOutcome guiding, Exception? guidingError) =
                await ImagingStop.GuidingAsync(context, CancellationToken.None);
            if (guidingError is not null)
            {
                context.LogWarning($"Could not stop guiding while shutting down: {guidingError.Message}");
            }
            else if (guiding == GuidingStopOutcome.NotConfirmed)
            {
                context.LogWarning("Could not stop guiding while shutting down. Check the guider.");
            }

            // 3. Stop Mount
            (ImagingStop.MountOutcome mount, Exception? mountError) = await ImagingStop.MountAsync(context);
            if (mountError is not null)
            {
                context.LogWarning($"Could not stop tracking while shutting down: {mountError.Message}");
            }
            else if (mount == ImagingStop.MountOutcome.NotConfirmed)
            {
                context.LogWarning("Could not stop tracking while shutting down. Check the mount.");
            }

            // 4. Report Failed image
            try
            {
                await context.ReportStartedImageFailedAsync("cycle aborted: the autopilot was stopped");
            }
            catch (Exception ex)
            {
                context.LogWarning($"Could not report the interrupted image to the server: {ex.Message}");
            }


            //5. Report if the roof is open or closed
            bool isRoofOurs = context.RoofMode == RoofMode.Operate || context.Roof.HasOpenedTonight;
            if (context.IsStillSecured)
            {
                string securedAt = context.Night.SecuredAtUtc is DateTime securedAtUtc
                    ? $" at {securedAtUtc.ToLocalTime():HH:mm}" : "";
                string roof = isRoofOurs ? "the roof closed" : "the roof left to whoever operates it";
                context.Log($"Autopilot stopped: the mount was parked and {roof}{securedAt}, " +
                            "and both are as they were left.");
                return AutopilotStateResult.Completed;
            }

            bool isRoofOpen = context.Dome is { IsConnected: true } dome && !dome.IsShutterClosed();
            List<string> unsecured = new();
            if (context.Mount is not { IsConnected: true } connectedMount) unsecured.Add("the mount not connected");
            else if (!connectedMount.IsParked) unsecured.Add("the mount NOT parked");
            if (isRoofOpen)
            {
                unsecured.Add("the roof NOT closed");
            }
            else if (isRoofOurs && context.Dome is not { IsConnected: true } && IsRoofInUseTonight())
            {
                // Unread isn't closed, so it's named like an open roof rather than left out.
                unsecured.Add("the roof state unknown (dome not connected)");
            }

            if (unsecured.Count == 0)
            {
                string roof = context.Dome is { IsConnected: true } ? " and the roof closed" : "";
                context.Log($"Autopilot stopped with the mount parked{roof}.");
                return AutopilotStateResult.Completed;
            }

            string roofNote = isRoofOpen && !context.IsRoofClosedOnUnsafe
                ? " N.I.N.A.'s 'Close on unsafe' is off, so nothing will close the roof if conditions turn."
                : "";
            context.LogWarning(
                $"Autopilot stopped with {string.Join(" and ", unsecured)}. Secure the observatory if you " +
                "are done." + roofNote);
            return AutopilotStateResult.Completed;
        }
    }
}