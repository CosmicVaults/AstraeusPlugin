using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// The one stop sequence the stops and secures share: the exposure, then guiding, then the mount.
    /// Each part reports what happened instead of logging it, so every caller keeps its own wording and
    /// still goes on to the next part. Guiding stops before the mount, or a guider left running on a
    /// stopped mount chases the drifting sky.
    /// </summary>
    internal static class ImagingStop
    {
        /// <summary>What became of the mount. The word for an alert or a log line is the caller's.</summary>
        internal enum MountOutcome
        {
            NotConnected,
            Parked,
            Resting,        // tracking off and still, so there was nothing to stop
            Stopped,
            NotConfirmed,   // the driver did not confirm the stop
            Failed,         // the driver threw
        }

        /// <summary>
        /// Stops a connected camera's exposure. Returns the error if the camera threw, null otherwise.
        /// Never throws.
        /// </summary>
        public static Exception? Exposure(Context context)
        {
            try
            {
                if (context.Camera is { IsConnected: true } camera) camera.StopExposure();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>
        /// Stops a connected guider and reports the outcome, NotRunning when no guider is connected. A
        /// guider that threw is NotConfirmed with the error. Never throws except on the caller's cancellation.
        /// </summary>
        public static async Task<(GuidingStopOutcome Outcome, Exception? Error)> GuidingAsync(Context context,
            CancellationToken cancellationToken)
        {
            if (context.Guider is not { IsConnected: true } guider) return (GuidingStopOutcome.NotRunning, null);
            try
            {
                return (await guider.StopGuidingAsync(cancellationToken), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (GuidingStopOutcome.NotConfirmed, ex);
            }
        }

        /// <summary>
        /// Stops tracking and any slew on a connected mount that is not already parked or resting, and
        /// reports what became of it. A resting mount is left alone: stopping a still mount made N.I.N.A.
        /// refuse the tracking change when parked and log a stop for a mount that never moved. Never throws.
        /// </summary>
        public static async Task<(MountOutcome Outcome, Exception? Error)> MountAsync(Context context)
        {
            if (context.Mount is not { IsConnected: true } mount) return (MountOutcome.NotConnected, null);
            if (mount.IsParked) return (MountOutcome.Parked, null);
            if (mount.IsResting) return (MountOutcome.Resting, null);
            try
            {
                return (await mount.StopMovementAsync() ? MountOutcome.Stopped : MountOutcome.NotConfirmed, null);
            }
            catch (Exception ex)
            {
                return (MountOutcome.Failed, ex);
            }
        }
    }
}
