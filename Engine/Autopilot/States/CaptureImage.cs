using NINA.Astrometry;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>Captures one frame. The server hands out one image per poll and tracks repeats itself.</summary>
    internal sealed class CaptureImage(string name, Context context) : StateBase(name) {
        private const int UseCameraCurrentValue = -1;
        // Noise through a closed cover has read up to 34 "stars". Real narrowband frames read about 100.
        private const int DarkFrameStarLimit = 50;
        private const int DarkFramesBeforeAlert = 3;

        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            AutopilotNextResponse? target = context.CurrentTarget;
            if (target == null || context.Camera == null) {
                context.LogError("No target or camera, so cannot capture.");
                return AutopilotStateResult.Failed;
            }
            // A dropped camera gets one reconnect, and is cooled again if cooling is on. If it stays
            // gone, the capture below fails and the frame is reported failed.
            await context.EnsureConnectedAsync(context.Camera, cancellationToken);

            double exposure = target.Exposure ?? Context.DefaultExposureSeconds;
            short  binning  = target.Binning ?? 1;
            int    gain    = target.Gain ?? UseCameraCurrentValue;
            int    offset   = target.Offset ?? UseCameraCurrentValue;
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
            context.LastCapture = result;
            if (!result.IsSuccess) {
                context.LogWarning($"Capture failed: {result.FailReason}.");
                return AutopilotStateResult.Failed;
            }

            context.Log($"Captured: HFR={result.Hfr:F2}, stars={result.DetectedStars}, " +
                        $"file={Path.GetFileName(result.FilePath)}.");
            CheckPointing(target, result);
            CheckLightPath(target, result);
            return AutopilotStateResult.Completed;
        }

        // A cover, cap or mirror cover left closed gives frame after frame of noise with a few "stars"
        // and no solve, for a whole night if nobody is told. Cloud looks the same in these numbers, so we only
        // alert once per run of such frames and the night carries on.
        private void CheckLightPath(AutopilotNextResponse target, CaptureResult result) {
            bool isDark = result.WasPlateSolveAttempted && !result.IsPlateSolved &&
                          (result.DetectedStars ?? 0) < DarkFrameStarLimit;
            if (!isDark) {
                context.DarkFrameStarCounts.Clear();
                context.HasReportedDarkFrames = false;
                return;
            }
            context.DarkFrameStarCounts.Add(result.DetectedStars ?? 0);
            if (context.HasReportedDarkFrames || context.DarkFrameStarCounts.Count < DarkFramesBeforeAlert) return;

            context.HasReportedDarkFrames = true;
            string flatPanel = context.FlatPanel is { IsConnected: true } panel
                ? panel.CoverStateName
                : "not connected";
            string roof = context.Dome is { IsConnected: true } dome
                ? (dome.IsShutterOpen() ? "open" : "not open")
                : "not connected";
            context.ReportCritical("Autopilot: frames show no stars. The light path may be blocked.", new {
                target = target.TargetName,
                frames = context.DarkFrameStarCounts.Count,
                stars = string.Join(", ", context.DarkFrameStarCounts),
                median_adu = result.Median,
                flat_panel = flatPanel,
                roof,
                check = "the flat panel cover, dust cap and mirror covers; cloud gives the same numbers"
            });
        }

        // Only the frame's own solve can tell that the field is 2° off from where the mount says it is.
        // A miss beyond tolerance sends the next frame through a slew and solve. The limit is never
        // tighter than N.I.N.A.'s pointing tolerance, since centring stops anywhere inside that. Once
        // centring has failed on this target we only log the miss, as each retry costs minutes.
        private void CheckPointing(AutopilotNextResponse target, CaptureResult result) {
            if (result.PointingOffsetArcmin is not double offsetArcmin) return;
            double limitArcmin = Math.Max(context.RecenterToleranceArcmin, context.PointingToleranceArcmin);
            if (offsetArcmin <= limitArcmin) return;
            string miss = DescribeMiss(offsetArcmin, limitArcmin);
            if (context.IsCentringGivenUp) {
                context.Log(
                    $"Frame solved {miss} from {target.TargetName}. " +
                    "Centring is unavailable for this target, so the next frame is not re-centred.");
                return;
            }
            context.IsLastFrameOffTarget = true;
            context.LogWarning(
                $"Frame solved {miss} from {target.TargetName}, so the next frame will slew and plate solve " +
                "before exposing.");
        }

        // Two decimals, or three when two would print a miss just over the tolerance as equal to it.
        private static string DescribeMiss(double offsetArcmin, double limitArcmin) {
            string format = offsetArcmin.ToString("F2", CultureInfo.InvariantCulture)
                            == limitArcmin.ToString("F2", CultureInfo.InvariantCulture) ? "F3" : "F2";
            return $"{offsetArcmin.ToString(format, CultureInfo.InvariantCulture)}' (tolerance " +
                   $"{limitArcmin.ToString(format, CultureInfo.InvariantCulture)}')";
        }
    }
}
