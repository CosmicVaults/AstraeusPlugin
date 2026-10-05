using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Hands every frame waiting to go up to the background upload queue. That takes microseconds, so there's
    /// no safety watcher. Daylight isn't watched, so the night's last frames still reach the queue.
    /// </summary>
    internal sealed class QueueUpload(string name, Context context) : StateBase(name) {
        // The mount is still tracking the last target here.
        protected internal override bool ShouldWatchMountLimits => true;
        protected internal override bool ShouldWatchObservingWindow => false;

        public override Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            List<PendingFrame> due = context.PendingFrames.Where(frame => frame.Upload != null).ToList();
            if (due.Count == 0)
                return Task.FromResult(AutopilotStateResult.Completed);

            if (context.Camera is null || !context.Observatory.Authenticator.IsAuthenticated) {
                context.LogWarning($"Upload skipped for {due.Count} frame(s): " +
                                   (context.Camera is null ? "no camera component." : "not authenticated."));
                context.DropPendingUploads();
                return Task.FromResult(AutopilotStateResult.Completed);
            }

            foreach (PendingFrame frame in due) {
                PendingUpload upload = frame.Upload!;
                // The key was fixed when the frame was reported, since it carries the target name and a
                // frame held back by a secure is handed over later. Built here only if a frame has none.
                context.Observatory.CaptureUploads.TryEnqueue(upload,
                    frame.UploadKey ?? context.Camera.BuildR2Filename(upload.FilePath), "autopilot",
                    LogCategory.Autopilot);
                frame.Upload = null;
            }
            context.PendingFrames.RemoveAll(frame => frame.IsFinished);
            return Task.FromResult(AutopilotStateResult.Completed);
        }
    }
}
