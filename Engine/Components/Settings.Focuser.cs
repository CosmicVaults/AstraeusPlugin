using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _focuserSettingsPush;

        private async Task HandleFocuserUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            IFocuserSettings focuserSettings = Profile!.FocuserSettings;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .Double("autofocus_exposure_time", value => focuserSettings.AutoFocusExposureTime = value,
                    min: 0.01, max: 600)
                .Int("autofocus_initial_offset_steps", value => focuserSettings.AutoFocusInitialOffsetSteps = value,
                    min: 1, max: 100)
                .Int("autofocus_step_size", value => focuserSettings.AutoFocusStepSize = value, min: 1, max: 100000)
                .Bool("use_filter_wheel_offsets", value => focuserSettings.UseFilterWheelOffsets = value)
                .Bool("autofocus_disable_guiding", value => focuserSettings.AutoFocusDisableGuiding = value)
                .Int("focuser_settle_time", value => focuserSettings.FocuserSettleTime = value, min: 0, max: 600)
                .Int("autofocus_total_number_of_attempts",
                    value => focuserSettings.AutoFocusTotalNumberOfAttempts = value, min: 1, max: 10)
                .Int("autofocus_number_of_frames_per_point",
                    value => focuserSettings.AutoFocusNumberOfFramesPerPoint = value, min: 1, max: 10)
                .Double("autofocus_inner_crop_ratio", value => focuserSettings.AutoFocusInnerCropRatio = value,
                    min: 0, max: 1)
                .Double("autofocus_outer_crop_ratio", value => focuserSettings.AutoFocusOuterCropRatio = value,
                    min: 0, max: 1)
                .Int("autofocus_use_brightest_stars", value => focuserSettings.AutoFocusUseBrightestStars = value,
                    min: 0, max: 1000)
                .Int("backlash_in", value => focuserSettings.BacklashIn = value, min: 0, max: 100000)
                .Int("backlash_out", value => focuserSettings.BacklashOut = value, min: 0, max: 100000)
                .Short("autofocus_binning", value => focuserSettings.AutoFocusBinning = value, min: 1, max: 4)
                .EnumNumber<AFCurveFittingEnum>("autofocus_curve_fitting",
                    value => focuserSettings.AutoFocusCurveFitting = value)
                .EnumNumber<AFMethodEnum>("autofocus_method", value => focuserSettings.AutoFocusMethod = value)
                .EnumNumber<ContrastDetectionMethodEnum>("contrast_detection_method",
                    value => focuserSettings.ContrastDetectionMethod = value)
                .EnumNumber<BacklashCompensationModel>("backlash_compensation_model",
                    value => focuserSettings.BacklashCompensationModel = value)
                .Int("autofocus_timeout_seconds", value => focuserSettings.AutoFocusTimeoutSeconds = value,
                    min: 1, max: 3600)
                .Double("r_squared_threshold", value => focuserSettings.RSquaredThreshold = value, min: 0, max: 1);
            if (!await TryApplyServerUpdateAsync(update, "focuser", PushFocuserSettingsAsync)) return;

            Log($"Focuser settings updated via WS: stepSize={focuserSettings.AutoFocusStepSize}, " +
                $"method={focuserSettings.AutoFocusMethod}", device: "focuser");
        }

        internal Task PushFocuserSettingsAsync() {
            IFocuserSettings focuserSettings = Profile!.FocuserSettings;
            FocuserSettingsPayload payload = new FocuserSettingsPayload {
                AutoFocusExposureTime = focuserSettings.AutoFocusExposureTime,
                AutoFocusInitialOffsetSteps = focuserSettings.AutoFocusInitialOffsetSteps,
                AutoFocusStepSize = focuserSettings.AutoFocusStepSize,
                UseFilterWheelOffsets = focuserSettings.UseFilterWheelOffsets,
                AutoFocusDisableGuiding = focuserSettings.AutoFocusDisableGuiding,
                FocuserSettleTime = focuserSettings.FocuserSettleTime,
                AutoFocusTotalNumberOfAttempts = focuserSettings.AutoFocusTotalNumberOfAttempts,
                AutoFocusNumberOfFramesPerPoint = focuserSettings.AutoFocusNumberOfFramesPerPoint,
                AutoFocusInnerCropRatio = focuserSettings.AutoFocusInnerCropRatio,
                AutoFocusOuterCropRatio = focuserSettings.AutoFocusOuterCropRatio,
                AutoFocusUseBrightestStars = focuserSettings.AutoFocusUseBrightestStars,
                BacklashIn = focuserSettings.BacklashIn,
                BacklashOut = focuserSettings.BacklashOut,
                AutoFocusBinning = focuserSettings.AutoFocusBinning,
                AutoFocusCurveFitting = focuserSettings.AutoFocusCurveFitting,
                AutoFocusMethod = focuserSettings.AutoFocusMethod,
                ContrastDetectionMethod = focuserSettings.ContrastDetectionMethod,
                BacklashCompensationModel = focuserSettings.BacklashCompensationModel,
                AutoFocusTimeoutSeconds = focuserSettings.AutoFocusTimeoutSeconds,
                RSquaredThreshold = focuserSettings.RSquaredThreshold
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "focuser"));
        }

        private void OnFocuserSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _focuserSettingsPush ??= CreatePush(PushFocuserSettingsAsync, "focuser");
            _focuserSettingsPush.Schedule();
        }
    }
}
