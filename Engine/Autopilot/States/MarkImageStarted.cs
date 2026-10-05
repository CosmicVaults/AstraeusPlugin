using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Tells the server (/start/) the image is starting, right before capture as its contract requires. On
    /// failure the image stays PENDING and we go back to polling without imaging it.
    /// </summary>
    internal sealed class MarkImageStarted(string name, Context context) : StateBase(name) {
        // The round trip is bounded by the web client's timeout, but the roof is open for all of it.
        protected internal override bool ShouldWatchSafety => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (context.CurrentTarget?.ImageId is not int imageId) {
                context.LogError("No current image to mark started.");
                return AutopilotStateResult.Failed;
            }

            context.AutopilotStatus = AutopilotStatus.Reporting;
            if (!await context.Observatory.AstraeusWebClient.PostAutopilotStartAsync(imageId)) {
                context.LogWarning($"The server did not take the start of the next frame of " +
                                   $"{context.CurrentTarget?.TargetName ?? "this target"}, so asking it for " +
                                   "the next frame again.");
                return AutopilotStateResult.Failed;
            }

            context.IsCurrentTargetStarted = true;
            context.FrameWindowEndsAtUtc = DateTime.UtcNow + Context.ExposureWindow(context.CurrentTarget);
            // One dispatch is one exposure under the frame-counting contract, so this ref identifies
            // the exposure for every report of it, including retries.
            context.CurrentClientRef = Guid.NewGuid().ToString();
            context.LogDebug($"Image {imageId} marked as started.");
            // The web client takes no token, so a watcher that tripped during the round trip is seen
            // here. It's after the started flag so the secure knows to fail this image.
            cancellationToken.ThrowIfCancellationRequested();
            return AutopilotStateResult.Completed;
        }
    }
}
