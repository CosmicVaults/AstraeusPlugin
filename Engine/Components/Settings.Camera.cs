using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _cameraSettingsPush;

        private async Task HandleCameraUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            ICameraSettings cameraSettings = Profile!.CameraSettings;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .Double("pixel_size", value => cameraSettings.PixelSize = value)
                .Int("bit_depth", value => cameraSettings.BitDepth = value)
                .EnumNumber<BayerPatternEnum>("bayer_pattern", value => cameraSettings.BayerPattern = value);
            if (!await TryApplyServerUpdateAsync(update, "camera", PushCameraSettingsAsync)) return;

            Log($"Camera settings updated via WS: pixelSize={cameraSettings.PixelSize}, " +
                $"bitDepth={cameraSettings.BitDepth}, bayerPattern={cameraSettings.BayerPattern}", device: "camera");
        }

        internal Task PushCameraSettingsAsync() {
            ICameraSettings cameraSettings = Profile!.CameraSettings;
            CameraSettingsPayload payload = new CameraSettingsPayload {
                PixelSize = cameraSettings.PixelSize,
                BitDepth = cameraSettings.BitDepth,
                BayerPattern = cameraSettings.BayerPattern
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "camera"));
        }

        private void OnCameraSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _cameraSettingsPush ??= CreatePush(PushCameraSettingsAsync, "camera");
            _cameraSettingsPush.Schedule();
        }
    }
}
