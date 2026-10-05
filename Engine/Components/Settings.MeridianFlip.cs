using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using NINA.Profile.Interfaces;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _meridianFlipPush;

        private void SubscribeMeridianFlipSettings() {
            Profile!.MeridianFlipSettings.PropertyChanged += OnMeridianFlipSettingsChanged;
        }

        private void UnsubscribeMeridianFlipSettings() {
            if (Profile != null) Profile.MeridianFlipSettings.PropertyChanged -= OnMeridianFlipSettingsChanged;
        }

        private async Task HandleMeridianFlipUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            IMeridianFlipSettings meridianFlipSettings = Profile!.MeridianFlipSettings;
            double? minimumMinutesAfter = null;
            double? maximumMinutesAfter = null;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .Double("minutes_after_meridian", value => minimumMinutesAfter = value)
                .Double("max_minutes_after_meridian", value => maximumMinutesAfter = value)
                .Double("pause_time_before_meridian", value => meridianFlipSettings.PauseTimeBeforeMeridian = value)
                .Bool("recenter", value => meridianFlipSettings.Recenter = value)
                .RoundedInt("settle_time", value => meridianFlipSettings.SettleTime = value)
                .Bool("use_side_of_pier", value => meridianFlipSettings.UseSideOfPier = value)
                .Bool("auto_focus_after_flip", value => meridianFlipSettings.AutoFocusAfterFlip = value)
                .Bool("rotate_image_after_flip", value => meridianFlipSettings.RotateImageAfterFlip = value);
            if (!await TryApplyServerUpdateAsync(update, "meridian flip", PushMeridianFlipSettingsAsync)) return;
            ApplyMinutesAfterMeridian(meridianFlipSettings, minimumMinutesAfter, maximumMinutesAfter);

            Log($"Meridian flip settings updated via WS: minutesAfter={meridianFlipSettings.MinutesAfterMeridian}, " +
                $"maxMinutesAfter={meridianFlipSettings.MaxMinutesAfterMeridian}, " +
                $"pauseBefore={meridianFlipSettings.PauseTimeBeforeMeridian}, " +
                $"recenter={meridianFlipSettings.Recenter}, " +
                $"autofocusAfterFlip={meridianFlipSettings.AutoFocusAfterFlip}", device: "mount");
        }

        // N.I.N.A.'s setters keep minimum <= maximum, so a minimum above the current maximum goes in
        // after the new maximum.
        private static void ApplyMinutesAfterMeridian(IMeridianFlipSettings meridianFlipSettings,
            double? minimum, double? maximum) {
            bool isMaximumRaisedFirst = minimum > meridianFlipSettings.MaxMinutesAfterMeridian && maximum is double;
            if (isMaximumRaisedFirst) meridianFlipSettings.MaxMinutesAfterMeridian = maximum!.Value;
            if (minimum is double newMinimum) meridianFlipSettings.MinutesAfterMeridian = newMinimum;
            if (maximum is double newMaximum) meridianFlipSettings.MaxMinutesAfterMeridian = newMaximum;
        }

        internal Task PushMeridianFlipSettingsAsync() {
            IMeridianFlipSettings meridianFlipSettings = Profile!.MeridianFlipSettings;
            MeridianFlipSettingsPayload payload = new MeridianFlipSettingsPayload {
                MinutesAfterMeridian = meridianFlipSettings.MinutesAfterMeridian,
                MaxMinutesAfterMeridian = meridianFlipSettings.MaxMinutesAfterMeridian,
                PauseTimeBeforeMeridian = meridianFlipSettings.PauseTimeBeforeMeridian,
                Recenter = meridianFlipSettings.Recenter,
                SettleTime = meridianFlipSettings.SettleTime,
                UseSideOfPier = meridianFlipSettings.UseSideOfPier,
                AutoFocusAfterFlip = meridianFlipSettings.AutoFocusAfterFlip,
                RotateImageAfterFlip = meridianFlipSettings.RotateImageAfterFlip
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "meridianflip"));
        }

        private void OnMeridianFlipSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _meridianFlipPush ??= CreatePush(PushMeridianFlipSettingsAsync, "meridian flip");
            _meridianFlipPush.Schedule();
        }
    }
}
