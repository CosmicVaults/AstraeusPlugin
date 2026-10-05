using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _domeSettingsPush;

        private async Task HandleDomeUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            IDomeSettings domeSettings = Profile!.DomeSettings;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .Double("scope_position_east_west_mm", value => domeSettings.ScopePositionEastWest_mm = value)
                .Double("scope_position_north_south_mm", value => domeSettings.ScopePositionNorthSouth_mm = value)
                .Double("scope_position_up_down_mm", value => domeSettings.ScopePositionUpDown_mm = value)
                .Double("dome_radius_mm", value => domeSettings.DomeRadius_mm = value)
                .Double("gem_axis_mm", value => domeSettings.GemAxis_mm = value)
                .Double("lateral_axis_mm", value => domeSettings.LateralAxis_mm = value)
                .Double("azimuth_tolerance_degrees", value => domeSettings.AzimuthTolerance_degrees = value)
                .Bool("find_home_before_park", value => domeSettings.FindHomeBeforePark = value)
                .Int("dome_sync_timeout_seconds", value => domeSettings.DomeSyncTimeoutSeconds = value)
                .Bool("synchronize_during_mount_slew", value => domeSettings.SynchronizeDuringMountSlew = value)
                .Bool("sync_slew_dome_when_mount_slews", value => domeSettings.SyncSlewDomeWhenMountSlews = value)
                .Double("rotate_degrees", value => domeSettings.RotateDegrees = value)
                .Bool("close_on_unsafe", value => domeSettings.CloseOnUnsafe = value)
                .Bool("park_mount_before_shutter_move", value => domeSettings.ParkMountBeforeShutterMove = value)
                .Bool("refuse_unsafe_shutter_move", value => domeSettings.RefuseUnsafeShutterMove = value)
                .Bool("refuse_unsafe_shutter_open_sans_safety_device",
                    value => domeSettings.RefuseUnsafeShutterOpenSansSafetyDevice = value)
                .Bool("refuse_unpark_without_shutter_open",
                    value => domeSettings.RefuseUnparkWithoutShutterOpen = value)
                .Bool("park_dome_before_shutter_move", value => domeSettings.ParkDomeBeforeShutterMove = value)
                .EnumNumber<MountTypeEnum>("mount_type", value => domeSettings.MountType = value)
                .Double("dec_offset_horizontal_mm", value => domeSettings.DecOffsetHorizontal_mm = value)
                .Int("settle_time_seconds", value => domeSettings.SettleTimeSeconds = value);
            if (!await TryApplyServerUpdateAsync(update, "dome", PushDomeSettingsAsync)) return;

            Log($"Dome settings updated via WS: radius={domeSettings.DomeRadius_mm}, " +
                $"azimuthTolerance={domeSettings.AzimuthTolerance_degrees}", device: "dome");
        }

        internal Task PushDomeSettingsAsync() {
            IDomeSettings domeSettings = Profile!.DomeSettings;
            DomeSettingsPayload payload = new DomeSettingsPayload {
                ScopePositionEastWest_mm = domeSettings.ScopePositionEastWest_mm,
                ScopePositionNorthSouth_mm = domeSettings.ScopePositionNorthSouth_mm,
                ScopePositionUpDown_mm = domeSettings.ScopePositionUpDown_mm,
                DomeRadius_mm = domeSettings.DomeRadius_mm,
                GemAxis_mm = domeSettings.GemAxis_mm,
                LateralAxis_mm = domeSettings.LateralAxis_mm,
                AzimuthTolerance_degrees = domeSettings.AzimuthTolerance_degrees,
                FindHomeBeforePark = domeSettings.FindHomeBeforePark,
                DomeSyncTimeoutSeconds = domeSettings.DomeSyncTimeoutSeconds,
                SynchronizeDuringMountSlew = domeSettings.SynchronizeDuringMountSlew,
                SyncSlewDomeWhenMountSlews = domeSettings.SyncSlewDomeWhenMountSlews,
                RotateDegrees = domeSettings.RotateDegrees,
                CloseOnUnsafe = domeSettings.CloseOnUnsafe,
                ParkMountBeforeShutterMove = domeSettings.ParkMountBeforeShutterMove,
                RefuseUnsafeShutterMove = domeSettings.RefuseUnsafeShutterMove,
                RefuseUnsafeShutterOpenSansSafetyDevice = domeSettings.RefuseUnsafeShutterOpenSansSafetyDevice,
                RefuseUnparkWithoutShutterOpen = domeSettings.RefuseUnparkWithoutShutterOpen,
                ParkDomeBeforeShutterMove = domeSettings.ParkDomeBeforeShutterMove,
                MountType = domeSettings.MountType,
                DecOffsetHorizontal_mm = domeSettings.DecOffsetHorizontal_mm,
                SettleTimeSeconds = domeSettings.SettleTimeSeconds
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "dome"));
        }

        private void OnDomeSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _domeSettingsPush ??= CreatePush(PushDomeSettingsAsync, "dome");
            _domeSettingsPush.Schedule();
        }
    }
}
