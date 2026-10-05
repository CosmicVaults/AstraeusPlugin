using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _telescopeSettingsPush;

        private async Task HandleTelescopeUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            ITelescopeSettings telescopeSettings = Profile!.TelescopeSettings;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .String("name", value => telescopeSettings.Name = value)
                .String("mount_name", value => telescopeSettings.MountName = value)
                .Double("focal_length", value => telescopeSettings.FocalLength = value)
                .Double("focal_ratio", value => telescopeSettings.FocalRatio = value)
                .Int("settle_time", value => telescopeSettings.SettleTime = value)
                .Bool("no_sync", value => telescopeSettings.NoSync = value)
                .Bool("time_sync", value => telescopeSettings.TimeSync = value)
                .Bool("primary_reversed", value => telescopeSettings.PrimaryReversed = value)
                .Bool("secondary_reversed", value => telescopeSettings.SecondaryReversed = value)
                .EnumNumber<TelescopeLocationSyncDirection>("location_sync_direction",
                    value => telescopeSettings.TelescopeLocationSyncDirection = value);
            if (!await TryApplyServerUpdateAsync(update, "telescope", PushTelescopeSettingsAsync)) return;

            Log($"Telescope settings updated via WS: name={telescopeSettings.Name}, " +
                $"focalLength={telescopeSettings.FocalLength}, focalRatio={telescopeSettings.FocalRatio}",
                device: "mount");
        }

        internal Task PushTelescopeSettingsAsync() {
            ITelescopeSettings telescopeSettings = Profile!.TelescopeSettings;
            TelescopeSettingsPayload payload = new TelescopeSettingsPayload {
                Name = telescopeSettings.Name,
                MountName = telescopeSettings.MountName,
                FocalLength = telescopeSettings.FocalLength,
                FocalRatio = telescopeSettings.FocalRatio,
                SettleTime = telescopeSettings.SettleTime,
                NoSync = telescopeSettings.NoSync,
                TimeSync = telescopeSettings.TimeSync,
                PrimaryReversed = telescopeSettings.PrimaryReversed,
                SecondaryReversed = telescopeSettings.SecondaryReversed,
                LocationSyncDirection = telescopeSettings.TelescopeLocationSyncDirection
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "telescope"));
        }

        private void OnTelescopeSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _telescopeSettingsPush ??= CreatePush(PushTelescopeSettingsAsync, "telescope");
            _telescopeSettingsPush.Schedule();
        }
    }
}
