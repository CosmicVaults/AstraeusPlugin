using NINA.Astrometry;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>Captures one frame. The server hands out one image per poll and tracks repeats itself.</summary>
    internal sealed class CaptureImage(string name, Context context) : StateBase(name)
    {
        private const int UseCameraCurrentValue = -1;

        protected internal override Aborts AbortsOn => Aborts.All;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            AutopilotNextResponse? target = context.Frame.Target;
            if (target == null || context.Camera == null)
            {
                context.LogError("No target or camera, so cannot capture.");
                return AutopilotStateResult.Failed;
            }
            // A dropped camera gets one reconnect, and is cooled again if cooling is on. If it stays
            // gone, the capture below fails and the frame is reported failed.
            await context.Devices.EnsureConnectedAsync(context.Camera, cancellationToken);

            double exposure = target.Exposure ?? AutopilotConstants.DefaultExposureSeconds;
            short binning = target.Binning ?? 1;
            int gain = target.Gain ?? UseCameraCurrentValue;
            int offset = target.Offset ?? UseCameraCurrentValue;
            // The server keeps these sensible, but they go straight to the camera, so a wrong one is
            // refused here and reported as a failed frame rather than handed to the driver.
            bool isCapturable = exposure > 0 && exposure <= AutopilotConstants.MaxExposureSeconds
                                && binning >= 1 && binning <= AutopilotConstants.MaxBinning;
            if (!isCapturable)
            {
                context.LogError($"The server asked for {exposure}s at bin {binning}x{binning}, which the camera " +
                                 "cannot take, so the frame is reported failed.");
                return AutopilotStateResult.Failed;
            }
            Coordinates? hint = target.Ra is double ra && target.Dec is double dec
                ? new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees)
                : null;
            // Frames the server already holds for this image, so the file pattern's frame number counts
            // up like the "frame n/N" line, one behind it.
            int frameNumber = target.CapturesComplete ?? 0;

            context.AutopilotStatus = AutopilotStatus.Capturing;
            string gainText = gain >= 0 ? $", gain {gain}" : "";
            string offsetText = offset >= 0 ? $", offset {offset}" : "";
            context.Log($"Capturing {target.TargetName} ({exposure}s, bin {binning}x{binning}{gainText}{offsetText}).");
            CaptureResult result = await context.Camera.CaptureAsync(exposure, binning, hint, gain, offset,
                frameNumber, cancellationToken);
            context.Frame.LastCapture = result;
            if (!result.IsSuccess)
            {
                context.LogWarning($"Capture failed: {result.FailReason}.");
                return AutopilotStateResult.Failed;
            }

            context.Log($"Captured: HFR={result.Hfr:F2}, stars={result.DetectedStars}, " +
                        $"file={Path.GetFileName(result.FilePath)}.");
            return AutopilotStateResult.Completed;
        }
    }
}
