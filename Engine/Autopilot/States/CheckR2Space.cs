using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Imaging;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Asks the server whether this account may upload. Only reached with Upload to Cloud on. No keeps the
    /// frame on this machine and goes on imaging, and Failed ends the night. Every frame asks, so an account
    /// that frees space starts uploading again at the next frame.
    /// </summary>
    internal sealed class CheckR2Space(string name, Context context) : StateBase(name) {
        // The mount is still tracking and the roof open during the round trip, and the limit may not be
        // set in the mount's own driver.
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;
        // Daylight isn't watched, like the rest of the post-capture tail, so dawn can't hold the night's
        // last frame back from the upload queue.
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            StorageVerdict verdict =
                await StorageGate.CheckAsync(context.Observatory, "autopilot", LogCategory.Autopilot);

            // The web client takes no token, so a watcher that tripped during the round trip is only
            // seen here. It comes before the verdict because a mount past its limit outranks storage.
            cancellationToken.ThrowIfCancellationRequested();

            if (verdict == StorageVerdict.Upload) return AutopilotStateResult.Completed;

            // Nothing goes up, and queued frames don't wait for a later one either. A full account
            // keeps every frame on this machine.
            context.DropPendingUploads();
            return verdict == StorageVerdict.Pause ? AutopilotStateResult.Failed : AutopilotStateResult.No;
        }
    }
}
