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
                .Double("autofocus_exposure_time", value => focuserSettings.AutoFocusExposureTime = value)
                .Int("autofocus_initial_offset_steps", value => focuserSettings.AutoFocusInitialOffsetSteps = value)
                .Int("autofocus_step_size", value => focuserSettings.AutoFocusStepSize = value)
                .Bool("use_filter_wheel_offsets", value => focuserSettings.UseFilterWheelOffsets = value)
                .Bool("autofocus_disable_guiding", value => focuserSettings.AutoFocusDisableGuiding = value)
                .Int("focuser_settle_time", value => focuserSettings.FocuserSettleTime = value)
                .Int("autofocus_total_number_of_attempts",
                    value => focuserSettings.AutoFocusTotalNumberOfAttempts = value)
                .Int("autofocus_number_of_frames_per_point",
                    value => focuserSettings.AutoFocusNumberOfFramesPerPoint = value)
                .Double("autofocus_inner_crop_ratio", value => focuserSettings.AutoFocusInnerCropRatio = value)
                .Double("autofocus_outer_crop_ratio", value => focuserSettings.AutoFocusOuterCropRatio = value)
                .Int("autofocus_use_brightest_stars", value => focuserSettings.AutoFocusUseBrightestStars = value)
                .Int("backlash_in", value => focuserSettings.BacklashIn = value)
                .Int("backlash_out", value => focuserSettings.BacklashOut = value)
                .Short("autofocus_binning", value => focuserSettings.AutoFocusBinning = value)
                .EnumNumber<AFCurveFittingEnum>("autofocus_curve_fitting",
                    value => focuserSettings.AutoFocusCurveFitting = value)
                .EnumNumber<AFMethodEnum>("autofocus_method", value => focuserSettings.AutoFocusMethod = value)
                .EnumNumber<ContrastDetectionMethodEnum>("contrast_detection_method",
                    value => focuserSettings.ContrastDetectionMethod = value)
                .EnumNumber<BacklashCompensationModel>("backlash_compensation_model",
                    value => focuserSettings.BacklashCompensationModel = value)
                .Int("autofocus_timeout_seconds", value => focuserSettings.AutoFocusTimeoutSeconds = value)
                .Double("r_squared_threshold", value => focuserSettings.RSquaredThreshold = value);
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
