using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>Always returns Completed, since report failures are only logged.</summary>
    internal sealed class ReportResult(string name, Context context) : StateBase(name) {
        // The complete call carries a per-exposure client_ref, so retries are safe on the server. An
        // unreported frame never counts as delivered, so it's worth retrying. Only the mount-limit
        // watcher runs here, so nothing else can cut a retry short.
        private const int MaxAttempts = 3;
        private static readonly TimeSpan RetryBackoffStep = TimeSpan.FromSeconds(5);

        // Daylight isn't watched, since the frame is on disk and a report cut short at dawn is a frame
        // the server never hears about. The mount is still tracking the last target here.
        protected internal override bool ShouldWatchMountLimits => true;
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            context.AutopilotStatus = AutopilotStatus.Reporting;
            AutopilotNextResponse? target = context.CurrentTarget;
            CaptureResult? capture = context.LastCapture;

            if (target?.ImageId is int imageId) {
                string? clientRef = context.CurrentClientRef;
                if (capture is { IsSuccess: true } succeeded) {
                    await ReportCompleteAsync(target, imageId, succeeded, clientRef, cancellationToken);
                } else {
                    AstraeusWebClient client = context.Observatory.AstraeusWebClient;
                    string failReason = capture?.FailReason ?? "imaging step failed";
                    await WithRetryAsync(
                        () => client.PostAutopilotFailAsync(imageId, failReason, clientRef),
                        $"the failed frame of {target.TargetName}", cancellationToken);
                }
            }

            context.CurrentTarget = null;
            context.LastCapture = null;
            context.IsCurrentTargetStarted = false;
            context.CurrentClientRef = null;
            return AutopilotStateResult.Completed;
        }

        private async Task ReportCompleteAsync(AutopilotNextResponse target, int imageId, CaptureResult succeeded,
            string? clientRef, CancellationToken cancellationToken) {
            AstraeusWebClient client = context.Observatory.AstraeusWebClient;
            // Read once per frame, since the report tells the server whether an upload follows and the
            // queued upload makes it follow. Both must see the same value.
            bool shouldUploadToCloud = context.IsCloudUploadEnabled;
            bool isCentred = context.IsFrameCentred;
            // The "Captured: ..." line already reported the frame. The ids only matter for debugging.
            context.LogDebug($"Reporting image {imageId} complete: plate_solved={succeeded.IsPlateSolved}, " +
                             $"centred={isCentred}, cloud upload {(shouldUploadToCloud ? "on" : "off")}.");
            if (!shouldUploadToCloud && !context.HasNotedCloudUploadOffTonight) {
                context.HasNotedCloudUploadOffTonight = true;
                context.Log("Cloud upload is off: tonight's frames and previews stay on this machine.");
            }
            JObject? response = await WithRetryAsync(
                () => client.PostAutopilotCompleteAsync(imageId, succeeded, clientRef, shouldUploadToCloud, isCentred),
                $"the frame of {target.TargetName}", cancellationToken);

            // Remembered for the poll. While the mount keeps tracking this target, a pinned side it's
            // already on can still be honoured. Any other slew re-chooses the side.
            context.LastDeliveredProjectId = target.ProjectId;
            context.LastDeliveredCoordinates = target.Ra is double ra && target.Dec is double dec
                ? new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees) : null;

            // The server's plan, when the project asked for calibration and masters apply. Kept even if
            // the frame isn't uploaded, since the calibrated copy is wanted on disk either way.
            ReadCompleteReply(response, out CalibrationPlan? plan, out int? replyCaptureId);
            PendingUpload? upload = null;
            if (shouldUploadToCloud) {
                if (replyCaptureId is int captureId && !string.IsNullOrEmpty(succeeded.FilePath)) {
                    upload = new PendingUpload(captureId, succeeded.FilePath!, succeeded.PreviewJpeg) {
                        ShouldKeepRaw = plan?.ShouldKeepRaw ?? true,
                    };
                } else {
                    context.LogWarning(string.IsNullOrEmpty(succeeded.FilePath)
                        ? "The frame was not saved to disk, so there is nothing to upload."
                        : $"The server gave {Path.GetFileName(succeeded.FilePath)} no upload slot, so it stays " +
                          "on this machine only.");
                }
            }
            // Queued behind anything an earlier secure interrupted. CalibrateFrame and QueueUpload
            // work the queue oldest first.
            if (!string.IsNullOrEmpty(succeeded.FilePath) && (plan != null || upload != null)) {
                context.PendingFrames.Add(new PendingFrame(succeeded.FilePath!) {
                    Plan = plan,
                    Upload = upload,
                    // While the camera still names this frame's target.
                    UploadKey = context.Camera?.BuildR2Filename(succeeded.FilePath!),
                });
            }
        }

        // A badly shaped reply mustn't fault the machine. Each part is read on its own, so a malformed
        // calibration plan leaves the frame uncalibrated but still uploaded, and the other way round.
        private void ReadCompleteReply(JObject? response, out CalibrationPlan? plan, out int? captureId) {
            try {
                captureId = ResponseFields.Int(response, "capture_id");
            } catch (Exception ex) {
                context.LogWarning($"The server's reply to a frame's report had no usable capture id ({ex.Message}); " +
                                   "the frame stays on this machine.");
                captureId = null;
            }
            try {
                plan = CalibrationPlan.Parse(response?["calibration"]);
            } catch (Exception ex) {
                context.LogWarning($"The server's calibration plan for a frame was malformed ({ex.Message}); " +
                                   "the frame is not calibrated.");
                plan = null;
            }
        }

        /// <summary>
        /// Runs a report call up to MaxAttempts times with backoff, returning null once they're spent. Only
        /// the delay takes the token, so a cancel mid-retry returns null instead of throwing out of the state.
        /// </summary>
        private async Task<JObject?> WithRetryAsync(Func<Task<JObject?>> call, string what,
            CancellationToken cancellationToken) {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++) {
                JObject? response = await call();
                if (response != null) return response;
                if (attempt == MaxAttempts) break;
                context.LogWarning($"Report of {what} failed (attempt {attempt}/{MaxAttempts}); retrying.");
                try {
                    await Task.Delay(RetryBackoffStep * attempt, cancellationToken);
                } catch (OperationCanceledException) {
                    return null;
                }
            }
            context.LogError($"Report of {what} failed after {MaxAttempts} attempts; " +
                             "the server will re-offer this exposure.");
            return null;
        }
    }
}
