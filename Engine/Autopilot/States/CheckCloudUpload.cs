using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Gate on the "Upload to Cloud" setting after the report. Yes uploads the frame and preview, No goes
    /// back to polling. Read live, so a change applies from the next frame.
    /// </summary>
    internal sealed class CheckCloudUpload(string name, Context context) : DecisionState(name) {
        // Part of the post-capture tail with CalibrateFrame and QueueUpload. The night's last frame must
        // still reach the upload queue after dawn.
        protected internal override bool ShouldWatchObservingWindow => false;

        protected override Task<bool> DecideAsync(CancellationToken cancellationToken) {
            bool isEnabled = context.IsCloudUploadEnabled;
            // Off keeps every queued frame on this machine, including any deferred from before the
            // switch was turned off.
            if (!isEnabled) context.DropPendingUploads();
            return Task.FromResult(isEnabled);
        }
    }
}
