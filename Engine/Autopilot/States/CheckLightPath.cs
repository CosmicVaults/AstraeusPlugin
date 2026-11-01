using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// A cover, cap or mirror cover left closed gives frame after frame of noise with a few "stars" and no
    /// solve, for a whole night if nobody is told. Cloud looks the same in these numbers, so we only alert
    /// once per run of such frames and the night carries on. Always Completed.
    /// </summary>
    internal sealed class CheckLightPath(string name, Context context) : StateBase(name)
    {
        // Noise through a closed cover has read up to 34 "stars". Real narrowband frames read about 100.
        private const int DarkFrameStarLimit = 50;
        private const int DarkFramesBeforeAlert = 3;

        // The frame is on disk, so neither dawn nor the roof may cut the report after this short. The
        // mount is still tracking the target.
        protected internal override Aborts AbortsOn => Aborts.LimitReached;

        public override Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (context.Frame.Target is { } target && context.Frame.LastCapture is { } result)
            {
                CheckLight(target, result);
            }
            return Task.FromResult(AutopilotStateResult.Completed);
        }

        private void CheckLight(AutopilotNextResponse target, CaptureResult result)
        {
            NightSession night = context.Night;
            bool isDark = result.WasPlateSolveAttempted && !result.IsPlateSolved &&
                          (result.DetectedStars ?? 0) < DarkFrameStarLimit;
            if (!isDark)
            {
                night.DarkFrameStarCounts.Clear();
                night.HasReportedDarkFrames = false;
                return;
            }
            night.DarkFrameStarCounts.Add(result.DetectedStars ?? 0);
            if (night.HasReportedDarkFrames || night.DarkFrameStarCounts.Count < DarkFramesBeforeAlert) return;

            night.HasReportedDarkFrames = true;
            string flatPanel = context.FlatPanel is { IsConnected: true } panel
                ? panel.CoverStateName
                : "not connected";
            string roof = context.Dome is { IsConnected: true } dome
                ? (dome.IsShutterOpen() ? "open" : "not open")
                : "not connected";
            context.ReportCritical("Autopilot: frames show no stars. The light path may be blocked.", new
            {
                target = target.TargetName,
                frames = night.DarkFrameStarCounts.Count,
                stars = string.Join(", ", night.DarkFrameStarCounts),
                median_adu = result.Median,
                flat_panel = flatPanel,
                roof,
                check = "the flat panel cover, dust cap and mirror covers; cloud gives the same numbers"
            });
        }
    }
}
