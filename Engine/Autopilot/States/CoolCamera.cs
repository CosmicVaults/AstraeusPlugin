using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Runs before anything waits on the weather, so an unsafe dusk is spent cooling. Always returns
    /// Completed, so a camera that can't reach its target doesn't hold up the night.
    /// </summary>
    internal sealed class CoolCamera(string name, Context context) : StateBase(name) {

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!context.IsCameraCoolingEnabled) return AutopilotStateResult.Completed;
            if (context.Camera is not { IsConnected: true } camera) {
                context.LogWarning("Camera cooling is on, but the camera is not connected, so not cooling it.");
                return AutopilotStateResult.Completed;
            }

            context.AutopilotStatus = AutopilotStatus.RunningStartup;
            // The camera logs why when it returns false (no temperature control, or a target the cooler
            // can't reach on a warm night).
            if (!await context.CoolCameraForNightAsync(camera, cancellationToken)) {
                context.Log("Camera is not at its target temperature. Carrying on with the night regardless.");
            }
            return AutopilotStateResult.Completed;
        }
    }
}
