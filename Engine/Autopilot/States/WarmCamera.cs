using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Warms the camera on the dawn path only, once secured. Runs before the shutdown sequence so one that
    /// cuts switch power never cuts it to a cold camera.
    /// </summary>
    internal sealed class WarmCamera(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!context.IsCameraCoolingEnabled) return AutopilotStateResult.Completed;
            // Not connected (start-up never got that far) or already warm, so nothing to do.
            if (context.Camera is not { IsCoolerOn: true } camera) return AutopilotStateResult.Completed;

            context.AutopilotStatus = AutopilotStatus.RunningShutdown;
            if (!await camera.WarmCameraAsync(cancellationToken)) {
                context.LogWarning("Camera warm-up did not finish. Its cooler may still be on.");
            }
            return AutopilotStateResult.Completed;
        }
    }
}
