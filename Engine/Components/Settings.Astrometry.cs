using NINA.Core.Model;
using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _astrometrySettingsPush;

        private async Task HandleAstrometryUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            IProfileService profileService = Observatory.Settings.ProfileService;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .Double("latitude", value => profileService.ChangeLatitude(value))
                .Double("longitude", value => profileService.ChangeLongitude(value))
                .Double("elevation", value => profileService.ChangeElevation(value));
            if (!await TryApplyServerUpdateAsync(update, "astrometry", PushAstrometrySettingsAsync)) return;

            IAstrometrySettings astrometrySettings = Profile!.AstrometrySettings;
            Log($"Astrometry settings updated via WS: latitude={astrometrySettings.Latitude}, " +
                $"longitude={astrometrySettings.Longitude}, elevation={astrometrySettings.Elevation}",
                device: "system");
        }

        internal Task PushAstrometrySettingsAsync() {
            IAstrometrySettings astrometrySettings = Profile!.AstrometrySettings;
            CustomHorizon? horizon = astrometrySettings.Horizon;
            AstrometryPayload payload = new AstrometryPayload {
                Latitude = astrometrySettings.Latitude,
                Longitude = astrometrySettings.Longitude,
                Elevation = astrometrySettings.Elevation,
                HorizonAltitudes = SampleHorizon(horizon == null ? null : horizon.GetAltitude),
                HorizonFileName = Path.GetFileName(astrometrySettings.HorizonFilePath ?? "")
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "astrometry"));
        }

        // The custom horizon at each integer azimuth, so the server applies the same floor the mount does.
        private static double[] SampleHorizon(Func<double, double>? altitudeAtAzimuth) {
            if (altitudeAtAzimuth == null) return Array.Empty<double>();
            double[] altitudeByAzimuth = new double[360];
            for (int azimuthDegrees = 0; azimuthDegrees < 360; azimuthDegrees++)
                altitudeByAzimuth[azimuthDegrees] = altitudeAtAzimuth(azimuthDegrees);
            return altitudeByAzimuth;
        }

        private void OnAstrometrySettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _astrometrySettingsPush ??= CreatePush(PushAstrometrySettingsAsync, "astrometry");
            _astrometrySettingsPush.Schedule();
        }
    }
}
