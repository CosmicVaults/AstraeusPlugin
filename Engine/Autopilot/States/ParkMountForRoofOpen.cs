using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// The park N.I.N.A. needs before it will move the shutter. With RefuseUnsafeShutterMove on, N.I.N.A.
    /// won't open the shutter while the mount is unparked and won't park for you, so the mount is parked
    /// here first or the roof never opens. Completed at once when N.I.N.A. does not refuse, when the roof
    /// already reads open, when the mount is parked, or when OpenDome is about to refuse anyway. Failed when
    /// the park can't be had, counted towards the "cannot open the roof" alert like a failed open.
    /// </summary>
    internal sealed class ParkMountForRoofOpen(string name, Context context) : StateBase(name)
    {
        protected internal override Aborts AbortsOn => Aborts.Unsafe | Aborts.RoofClosed | Aborts.WindowClosed;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (!context.IsShutterMoveRefusedWithUnparkedMount) return AutopilotStateResult.Completed;

            // OpenDome fails and counts a missing or dropped dome itself, and refuses while unsafe, so a park
            // for a roof that is not going to open would move the mount for nothing. The dome gets one
            // reconnect, so one that dropped and comes back open is not parked under either.
            if (context.Dome is not { } dome) return AutopilotStateResult.Completed;
            if (!dome.IsConnected && !await context.Devices.EnsureConnectedAsync(dome, cancellationToken))
            {
                return AutopilotStateResult.Completed;
            }
            if (dome.IsShutterOpen() || !context.IsSafe()) return AutopilotStateResult.Completed;

            // A disconnected mount fails the open too, since N.I.N.A. can't confirm it's clear of the roof.
            if (!await context.Devices.EnsureConnectedAsync(context.Mount, cancellationToken)
                || context.Mount is not { } mount)
            {
                context.LogError(
                    "The roof will not open: N.I.N.A. refuses shutter moves with the mount unparked, and the " +
                    "mount is not connected to confirm it is parked.");
                return Fail();
            }

            if (mount.IsParked) return AutopilotStateResult.Completed;

            // Off by default, since parking here slews the mount under a closed roof. N.I.N.A. won't do
            // it for the same reason, as a park position only reachable with the roof open means a
            // collision. A stalled night can be recovered. Bent metal can't.
            if (!context.CanMountMoveUnderClosedRoof)
            {
                context.LogError(
                    "The roof will not open: N.I.N.A. refuses shutter moves with the mount unparked. Park " +
                    "the mount, or turn on 'Mount can move under closed roof' if the park position is safe " +
                    "with the roof closed.");
                return Fail();
            }

            context.LogWarning(
                "Parking the mount under the closed roof so N.I.N.A. will open it ('Mount can move under " +
                "closed roof' is on).");
            try
            {
                await mount.ParkAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.LogError($"Could not park the mount before opening the roof: {ex.Message}");
                return Fail();
            }

            if (!mount.IsParked)
            {
                context.LogError(
                    "Mount does not report AtPark after the park attempt. Not opening the roof, as N.I.N.A. " +
                    "would refuse it anyway. Check the mount.");
                return Fail();
            }

            context.Log("Mount parked. Opening the roof.");
            return AutopilotStateResult.Completed;
        }

        // Counted with the failed opens, so a mount that can never be made ready still reaches the alert.
        private AutopilotStateResult Fail()
        {
            if (context.Roof.RecordFailedOpen())
            {
                OpenDome.ReportCannotOpen(context, "the mount could not be made ready for the roof to move");
            }
            return AutopilotStateResult.Failed;
        }
    }
}
