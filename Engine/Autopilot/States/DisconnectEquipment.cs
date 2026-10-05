using CosmicVaults.NINA.Astraeus.Engine.Components;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// At the end of the night, with auto-connect on, disconnects what the autopilot will reconnect at
    /// dusk. The safety monitor and weather station are used all day, the dome's roof watchdog needs it and
    /// the switch hub powers the rest, so those stay connected.
    /// </summary>
    internal sealed class DisconnectEquipment(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!context.ShouldAutoConnectEquipment) return AutopilotStateResult.Completed;

            context.AutopilotStatus = AutopilotStatus.RunningShutdown;
            // The guider first, since it may still hold the guide camera and the mount.
            await DisconnectAsync(context.Guider);
            if (context.Camera is { IsCoolerOn: true }) {
                string advice = context.IsCameraCoolingEnabled
                    ? "The dawn warm-up did not finish. Warm the camera before disconnecting it."
                    : "Turn on camera cooling in the autopilot settings to have it warmed at dawn, " +
                      "or warm it in the shutdown sequence.";
                context.LogWarning($"Camera left connected: its cooler is still on. {advice}");
            } else {
                await DisconnectAsync(context.Camera);
            }
            await DisconnectAsync(context.FilterWheel);
            await DisconnectAsync(context.Focuser);
            await DisconnectAsync(context.Rotator);
            await DisconnectAsync(context.FlatPanel);
            if (context.Mount is { IsConnected: true, IsParked: false }) {
                context.LogWarning("Mount left connected: it is not parked.");
            } else {
                await DisconnectAsync(context.Mount);
            }
            return AutopilotStateResult.Completed;
        }

        private async Task DisconnectAsync(IDeviceComponent? component) {
            if (component is not { IsConnected: true }) return;
            if (!component.HasDefaultDevice) {
                context.Log($"{component.DisplayName} left connected: it has no default device, so the " +
                            "autopilot could not reconnect it at dusk.");
                return;
            }
            if (await component.TryDisconnectAsync()) {
                context.Log($"Disconnected {component.DisplayName}.");
            } else {
                context.LogWarning($"{component.DisplayName} did not disconnect, so leaving it connected.");
            }
        }
    }
}
