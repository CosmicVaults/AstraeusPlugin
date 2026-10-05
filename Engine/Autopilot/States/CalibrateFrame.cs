using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Runs before the upload gate so a frame that isn't uploaded still gets its calibrated copy. Not
    /// watched for daylight since the frame is on disk and the work takes seconds.
    /// </summary>
    internal sealed class CalibrateFrame(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            List<PendingFrame> due = context.PendingFrames.Where(frame => frame.Plan != null).ToList();
            if (due.Count > 0) {
                context.AutopilotStatus = AutopilotStatus.Calibrating;
                FrameCalibrator calibrator = new FrameCalibrator(context.Observatory);
                foreach (PendingFrame frame in due) {
                    await CalibrateAsync(calibrator, frame, cancellationToken);
                }
            }
            context.PendingFrames.RemoveAll(frame => frame.IsFinished);
            return AutopilotStateResult.Completed;
        }

        private async Task CalibrateAsync(FrameCalibrator calibrator, PendingFrame frame,
            CancellationToken cancellationToken) {
            CalibrationPlan plan = frame.Plan!;
            string fileName = Path.GetFileName(frame.FilePath);
            context.Log($"Calibrating {fileName}: {plan.Describe()}.");

            // A cancellation propagates from here with the frame still queued and its plan intact.
            CalibrationOutcome outcome = await calibrator.CalibrateAsync(frame.FilePath, plan, cancellationToken);

            if (outcome.IsSuccess) {
                string notes = outcome.Warnings.Count > 0 ? $" Warnings: {string.Join(" ", outcome.Warnings)}" : "";
                string? outputFileName = Path.GetFileName(outcome.OutputPath);
                context.Log($"Calibrated {fileName} in {outcome.DurationSeconds:F1}s -> {outputFileName}.{notes}");
            } else if (outcome.SkipReason != null) {
                string rawFrameFate = frame.Upload != null
                    ? "The raw frame is uploaded uncalibrated."
                    : "The raw frame stands.";
                context.Log($"Calibration of {fileName} skipped: {outcome.SkipReason} {rawFrameFate}");
            } else {
                context.LogWarning($"Calibration of {fileName} failed: {outcome.Error} The raw frame stands.");
            }

            frame.Plan = null;
            if (frame.Upload is { } upload) {
                frame.Upload = upload with {
                    CalibratedFilePath = outcome.OutputPath,
                    Provenance = CalibrationProvenance.From(plan, outcome),
                };
            }
        }
    }
}
