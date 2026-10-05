using CosmicVaults.NINA.Astraeus.Engine.Auth;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Feed;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Globalization;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class AstraeusSettings {

        // Keys in the N.I.N.A. profile. Changing one of these strings orphans the value users already have.
        private const string RefreshTokenKey = "Astraeus.RefreshToken";
        private const string EmailKey = "Astraeus.Email";
        private const string IsPluginEnabledKey = "Astraeus.IsPluginEnabled";
        private const string IsNinaThemeEnabledKey = "Astraeus.IsNinaThemeEnabled";
        private const string IsAutopilotEnabledKey = "Astraeus.IsAutopilotEnabled";
        private const string DefaultDeviceIdKeyPrefix = "Astraeus.DefaultDeviceId.";
        private const string AutopilotRoofModeKey = "Astraeus.AutopilotRoofMode";
        private const string ShouldAutoConnectEquipmentKey = "Astraeus.AutopilotAutoConnectEquipment";
        private const string AutopilotStartupSequencePathKey = "Astraeus.AutopilotStartupSequencePath";
        private const string AutopilotShutdownSequencePathKey = "Astraeus.AutopilotShutdownSequencePath";
        private const string IsCameraCoolingEnabledKey = "Astraeus.IsCameraCoolingEnabled";
        private const string CameraCoolingTemperatureKey = "Astraeus.CameraCoolingTemperature";
        private const string AutopilotOperatingWindowKey = "Astraeus.AutopilotOperatingWindow";
        private const string IsMountLimitEnabledKey = "Astraeus.IsMountLimitEnabled";
        private const string IsAltitudeLimitEnabledKey = "Astraeus.IsAltitudeLimitEnabled";
        private const string MountMinAltitudeDegreesKey = "Astraeus.MountMinAltitudeDegrees";
        private const string IsCustomHorizonLimitEnabledKey = "Astraeus.IsCustomHorizonLimitEnabled";
        private const string IsMeridianFlipEnabledKey = "Astraeus.IsMeridianFlipEnabled";
        private const string RecenterToleranceArcminKey = "Astraeus.RecenterToleranceArcmin";
        private const string SafeSettleSecondsKey = "Astraeus.SafeSettleSeconds";
        private const string AutopilotRotatorFallbackModeKey = "Astraeus.AutopilotRotatorFallbackMode";
        private const string AutopilotRotatorFallbackAngleKey = "Astraeus.AutopilotRotatorFallbackAngle";
        private const string ShouldParkMountToOpenRoofKey = "Astraeus.ParkMountToOpenRoof";
        private const string AutopilotFlatPanelModeKey = "Astraeus.AutopilotFlatPanelMode";
        private const string ShouldOperateFlatPanelWithRoofClosedKey = "Astraeus.FlatPanelOperatesWithRoofClosed";
        private const string IsAutofocusEnabledKey = "Astraeus.IsAutofocusEnabled";
        private const string ShouldAutofocusOnTemperatureChangeKey = "Astraeus.AutofocusOnTemperatureChange";
        private const string AutofocusTemperatureThresholdKey = "Astraeus.AutofocusTemperatureThreshold";
        private const string ShouldAutofocusOnFilterChangeKey = "Astraeus.AutofocusOnFilterChange";
        private const string ShouldAutofocusOnTimeIntervalKey = "Astraeus.AutofocusOnTimeInterval";
        private const string AutofocusIntervalMinutesKey = "Astraeus.AutofocusIntervalMinutes";
        private const string IsSmartAutofocusEnabledKey = "Astraeus.IsSmartAutofocusEnabled";
        private const string IsCloudUploadEnabledKey = "Astraeus.IsCloudUploadEnabled";
        private const string CalibrationScanFolderKey = "Astraeus.Calibration.ScanFolder";
        private const string CalibrationMastersOutputFolderKey = "Astraeus.Calibration.MastersOutputFolder";
        private const string IsFeedEnabledKey = "Astraeus.Feed.Enabled";
        private const string FeedSourceKindKey = "Astraeus.Feed.SourceKind";
        private const string FeedUrlKey = "Astraeus.Feed.Url";
        private const string FeedUsernameKey = "Astraeus.Feed.Username";
        private const string FeedPasswordKey = "Astraeus.Feed.Password";
        private const string FeedMaxWidthKey = "Astraeus.Feed.MaxWidth";
        private const string FeedJpegQualityKey = "Astraeus.Feed.JpegQuality";
        private const string FeedIntervalSecondsKey = "Astraeus.Feed.IntervalSeconds";

        private const double DefaultCameraCoolingTemperature = -10;
        private const double DefaultMountMinAltitudeDegrees = 10.0;
        private const double DefaultRecenterToleranceArcmin = 2.0;
        private const int DefaultSafeSettleSeconds = 300;
        private const double DefaultRotatorFallbackAngle = 0.0;
        private const double DefaultAutofocusTemperatureThreshold = 2.0;
        private const int DefaultAutofocusIntervalMinutes = 60;

        public const int MinFeedMaxWidth = 160;
        public const int MaxFeedMaxWidth = 1920;
        private const int DefaultFeedMaxWidth = 640;
        public const int MinFeedJpegQuality = 30;
        public const int MaxFeedJpegQuality = 95;
        private const int DefaultFeedJpegQuality = 78;
        public const int MinFeedIntervalSeconds = 2;
        public const int MaxFeedIntervalSeconds = 300;
        private const int DefaultFeedIntervalSeconds = 60;

        public IProfileService ProfileService { get; private set; }
        public IOptionsVM OptionsViewModel { get; private set; }
        private readonly Guid _pluginGuid;

        public AstraeusSettings(IProfileService profileService, IOptionsVM optionsViewModel, Guid pluginGuid) {
            ProfileService = profileService;
            OptionsViewModel = optionsViewModel;
            _pluginGuid = pluginGuid;
            SetImagePatterns();
        }

        public void SetImagePatterns() {
            OptionsViewModel.AddImagePattern(ImagePatternDefinitions.CreateProjectNamePattern());
            OptionsViewModel.AddImagePattern(ImagePatternDefinitions.CreateTargetNamePattern());
        }

        ///////////// Safe profile-settings helpers /////////////
        // NINA's PluginSettings.TryGetValue throws NullReferenceException until the plugin GUID is
        // registered in the active profile, e.g. on the first load after install. These catch it.

        private bool TryGetSetting(string key, out string? value) {
            try {
                if (ProfileService.ActiveProfile?.PluginSettings == null) { value = null; return false; }
                return ProfileService.ActiveProfile.PluginSettings.TryGetValue(_pluginGuid, key, out value);
            } catch (Exception ex) {
                Logger.Warning($"[Astraeus] Failed to read setting '{key}': {ex.Message}");
                value = null;
                return false;
            }
        }

        private void SetSetting(string key, string? value) {
            try {
                ProfileService.ActiveProfile?.PluginSettings?.SetValue(_pluginGuid, key, value);
            } catch (Exception ex) {
                Logger.Warning($"[Astraeus] Failed to write setting '{key}': {ex.Message}");
            }
        }

        // Enum.TryParse also accepts a number like "7" that names no member. That reads as unset.
        private TEnum GetEnumSetting<TEnum>(string key, TEnum defaultValue) where TEnum : struct, Enum {
            if (!TryGetSetting(key, out string? storedValue)) return defaultValue;
            if (!Enum.TryParse(storedValue, out TEnum parsedValue)) return defaultValue;
            if (!Enum.IsDefined(typeof(TEnum), parsedValue)) return defaultValue;
            return parsedValue;
        }

        private bool GetBoolSetting(string key, bool defaultValue) {
            if (!TryGetSetting(key, out string? storedValue)) return defaultValue;
            if (storedValue == "true") return true;
            if (storedValue == "false") return false;
            return defaultValue;
        }

        private void SetBoolSetting(string key, bool value) {
            SetSetting(key, value ? "true" : "false");
        }

        private double GetDoubleSetting(string key, double defaultValue) {
            if (!TryGetSetting(key, out string? storedValue)) return defaultValue;
            bool isParsed = double.TryParse(storedValue, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double parsedValue);
            if (!isParsed) return defaultValue;
            if (!double.IsFinite(parsedValue)) return defaultValue;
            return parsedValue;
        }

        private void SetDoubleSetting(string key, double value) {
            SetSetting(key, value.ToString(CultureInfo.InvariantCulture));
        }

        private int GetIntSetting(string key, int defaultValue) {
            if (!TryGetSetting(key, out string? storedValue)) return defaultValue;
            bool isParsed = int.TryParse(storedValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsedValue);
            if (!isParsed) return defaultValue;
            return parsedValue;
        }

        private void SetIntSetting(string key, int value) {
            SetSetting(key, value.ToString(CultureInfo.InvariantCulture));
        }

        // An empty string is how a cleared text setting is stored, so it reads back as null.
        private string? GetTextSetting(string key) {
            if (!TryGetSetting(key, out string? storedValue)) return null;
            if (string.IsNullOrEmpty(storedValue)) return null;
            return storedValue;
        }

        private void SetTextSetting(string key, string? value) {
            SetSetting(key, value ?? string.Empty);
        }

        /// <summary>Access tokens are short-lived and only ever kept in memory.</summary>
        public string? GetRefreshToken() {
            if (!TryGetSetting(RefreshTokenKey, out string? protectedToken)) return null;
            return TokenStore.Unprotect(protectedToken);
        }

        /// <summary>Stores the refresh token, DPAPI-encrypted.</summary>
        public void SetRefreshToken(string? token) {
            SetSetting(RefreshTokenKey, TokenStore.Protect(token));
        }

        public void DeleteRefreshToken() {
            SetSetting(RefreshTokenKey, null);
        }

        /// <summary>Only used to show who is signed in. Login doesn't need it.</summary>
        public string? GetUserEmail() {
            return GetTextSetting(EmailKey);
        }

        public void SetUserEmail(string? email) {
            SetTextSetting(EmailKey, email);
        }

        public bool IsPluginEnabled() {
            return GetBoolSetting(IsPluginEnabledKey, defaultValue: true);
        }

        public void SetIsPluginEnabled(bool value) {
            SetBoolSetting(IsPluginEnabledKey, value);
        }

        /// <summary>
        /// Whether the options page uses N.I.N.A.'s own theme, from the active profile's colour schema,
        /// instead of the Astraeus look. Defaults to false so existing installs keep their look.
        /// </summary>
        public bool IsNinaThemeEnabled() {
            return GetBoolSetting(IsNinaThemeEnabledKey, defaultValue: false);
        }

        public void SetIsNinaThemeEnabled(bool value) {
            SetBoolSetting(IsNinaThemeEnabledKey, value);
        }

        public bool IsAutopilotEnabled() {
            return GetBoolSetting(IsAutopilotEnabledKey, defaultValue: false);
        }

        public void SetIsAutopilotEnabled(bool value) {
            SetBoolSetting(IsAutopilotEnabledKey, value);
        }

        public string? GetDefaultDeviceId(string deviceType) {
            return GetTextSetting(DefaultDeviceIdKeyPrefix + deviceType);
        }

        public void SetDefaultDeviceId(string deviceType, string? deviceId) {
            SetTextSetting(DefaultDeviceIdKeyPrefix + deviceType, deviceId);
        }

        public Autopilot.RoofMode GetAutopilotRoofMode() {
            return GetEnumSetting(AutopilotRoofModeKey, Autopilot.RoofMode.WaitForOpen);
        }

        public void SetAutopilotRoofMode(Autopilot.RoofMode value) {
            SetSetting(AutopilotRoofModeKey, value.ToString());
        }

        public bool ShouldAutoConnectEquipment() {
            return GetBoolSetting(ShouldAutoConnectEquipmentKey, defaultValue: false);
        }

        public void SetShouldAutoConnectEquipment(bool value) {
            SetBoolSetting(ShouldAutoConnectEquipmentKey, value);
        }

        /// <summary>
        /// Path to a NINA advanced sequence (.json) run once when the autopilot starts, before the
        /// imaging loop. Machine-local, so it's not part of the web contract.
        /// </summary>
        public string? GetAutopilotStartupSequencePath() {
            return GetTextSetting(AutopilotStartupSequencePathKey);
        }

        public void SetAutopilotStartupSequencePath(string? path) {
            SetTextSetting(AutopilotStartupSequencePathKey, path);
        }

        /// <summary>
        /// Path to a NINA advanced sequence (.json) run once when the autopilot stops, unsafe and user
        /// stops included, after the imaging loop. Machine-local, not in the web contract.
        /// </summary>
        public string? GetAutopilotShutdownSequencePath() {
            return GetTextSetting(AutopilotShutdownSequencePathKey);
        }

        public void SetAutopilotShutdownSequencePath(string? path) {
            SetTextSetting(AutopilotShutdownSequencePathKey, path);
        }

        /// <summary>
        /// Cools the camera at dusk and warms it at dawn. The cool and warm durations are still N.I.N.A.'s
        /// camera settings, the same ones its cooler button and the dashboard use.
        /// </summary>
        public bool IsCameraCoolingEnabled() {
            return GetBoolSetting(IsCameraCoolingEnabledKey, defaultValue: false);
        }

        public void SetIsCameraCoolingEnabled(bool value) {
            SetBoolSetting(IsCameraCoolingEnabledKey, value);
        }

        /// <summary>In °C.</summary>
        public const double MinCameraCoolingTemperature = -50;

        /// <summary>In °C.</summary>
        public const double MaxCameraCoolingTemperature = 20;

        /// <summary>
        /// In °C. Our own setting because N.I.N.A.'s camera temperature sits on an options page few observers
        /// find. Clamped on read too, so a hand-edited profile can't ask the cooler for something absurd.
        /// </summary>
        public double GetCameraCoolingTemperature() {
            double temperature = GetDoubleSetting(CameraCoolingTemperatureKey, DefaultCameraCoolingTemperature);
            return Math.Clamp(temperature, MinCameraCoolingTemperature, MaxCameraCoolingTemperature);
        }

        public void SetCameraCoolingTemperature(double value) {
            if (double.IsNaN(value)) return;
            double clamped = Math.Clamp(value, MinCameraCoolingTemperature, MaxCameraCoolingTemperature);
            SetDoubleSetting(CameraCoolingTemperatureKey, clamped);
        }

        public OperatingWindow GetAutopilotOperatingWindow() {
            return GetEnumSetting(AutopilotOperatingWindowKey, OperatingWindow.Nautical);
        }

        public void SetAutopilotOperatingWindow(OperatingWindow window) {
            SetSetting(AutopilotOperatingWindowKey, window.ToString());
        }

        ///////////// Mount pointing / tracking limits /////////////
        // All opt-in behind IsMountLimitEnabled. When it's off every limit check is a no-op.

        public bool IsMountLimitEnabled() {
            return GetBoolSetting(IsMountLimitEnabledKey, defaultValue: false);
        }

        public void SetIsMountLimitEnabled(bool value) {
            SetBoolSetting(IsMountLimitEnabledKey, value);
        }

        public bool IsAltitudeLimitEnabled() {
            return GetBoolSetting(IsAltitudeLimitEnabledKey, defaultValue: false);
        }

        public void SetIsAltitudeLimitEnabled(bool value) {
            SetBoolSetting(IsAltitudeLimitEnabledKey, value);
        }

        /// <summary>
        /// The lowest altitude the mount may point at or track down to. Only enforced when IsAltitudeLimitEnabled.
        /// </summary>
        public double GetMountMinAltitudeDegrees() {
            return GetDoubleSetting(MountMinAltitudeDegreesKey, DefaultMountMinAltitudeDegrees);
        }

        public void SetMountMinAltitudeDegrees(double value) {
            SetDoubleSetting(MountMinAltitudeDegreesKey, value);
        }

        /// <summary>
        /// Whether the profile's custom horizon (AstrometrySettings.Horizon) is enforced as an altitude
        /// floor, combined with the fixed one as max(floor, horizon(az)). No-op without a horizon file.
        /// </summary>
        public bool IsCustomHorizonLimitEnabled() {
            return GetBoolSetting(IsCustomHorizonLimitEnabledKey, defaultValue: false);
        }

        public void SetIsCustomHorizonLimitEnabled(bool value) {
            SetBoolSetting(IsCustomHorizonLimitEnabledKey, value);
        }

        /// <summary>
        /// Whether a German equatorial mount may flip at the meridian limit instead of being refused or
        /// stopped. The limit itself comes from N.I.N.A.'s Meridian Flip settings.
        /// </summary>
        public bool IsMeridianFlipEnabled() {
            return GetBoolSetting(IsMeridianFlipEnabledKey, defaultValue: false);
        }

        public void SetIsMeridianFlipEnabled(bool value) {
            SetBoolSetting(IsMeridianFlipEnabledKey, value);
        }

        /// <summary>
        /// How far off target (arcmin) the mount or a plate solve may be before the autopilot slews and
        /// solves again. The 2' default is twice N.I.N.A.'s default pointing tolerance, so a frame its
        /// centring passed never reads as off target.
        /// </summary>
        public double GetRecenterToleranceArcmin() {
            return GetDoubleSetting(RecenterToleranceArcminKey, DefaultRecenterToleranceArcmin);
        }

        public void SetRecenterToleranceArcmin(double value) {
            SetDoubleSetting(RecenterToleranceArcminKey, value);
        }

        /// <summary>The same cap the server enforces.</summary>
        public const int MaxSafeSettleSeconds = 600;

        /// <summary>
        /// Seconds the safety monitor must read Safe without a break before the autopilot reopens the roof
        /// and unparks. Clamped on read, since an older profile may hold an hour and put the slider off scale.
        /// </summary>
        public int GetSafeSettleSeconds() {
            int seconds = GetIntSetting(SafeSettleSecondsKey, DefaultSafeSettleSeconds);
            return Math.Clamp(seconds, 0, MaxSafeSettleSeconds);
        }

        public void SetSafeSettleSeconds(int value) {
            SetIntSetting(SafeSettleSecondsKey, Math.Clamp(value, 0, MaxSafeSettleSeconds));
        }

        ///////////// Rotator /////////////

        /// <summary>What the autopilot does with a connected rotator when the target has no position angle.</summary>
        public Autopilot.RotatorFallbackMode GetAutopilotRotatorFallbackMode() {
            return GetEnumSetting(AutopilotRotatorFallbackModeKey, Autopilot.RotatorFallbackMode.None);
        }

        public void SetAutopilotRotatorFallbackMode(Autopilot.RotatorFallbackMode value) {
            SetSetting(AutopilotRotatorFallbackModeKey, value.ToString());
        }

        /// <summary>
        /// The angle (degrees) the rotator fallback modes other than None use. A sky position angle or
        /// a mechanical angle, depending on the mode.
        /// </summary>
        public double GetAutopilotRotatorFallbackAngle() {
            return GetDoubleSetting(AutopilotRotatorFallbackAngleKey, DefaultRotatorFallbackAngle);
        }

        public void SetAutopilotRotatorFallbackAngle(double value) {
            SetDoubleSetting(AutopilotRotatorFallbackAngleKey, value);
        }

        /// <summary>
        /// For when N.I.N.A. refuses to move the shutter with the mount unparked. Parking slews the mount
        /// with the roof still closed, so a park position not reachable in that state means a collision.
        /// </summary>
        public bool ShouldParkMountToOpenRoof() {
            return GetBoolSetting(ShouldParkMountToOpenRoofKey, defaultValue: false);
        }

        public void SetShouldParkMountToOpenRoof(bool value) {
            SetBoolSetting(ShouldParkMountToOpenRoofKey, value);
        }

        public Autopilot.FlatPanelMode GetAutopilotFlatPanelMode() {
            return GetEnumSetting(AutopilotFlatPanelModeKey, Autopilot.FlatPanelMode.Ignore);
        }

        public void SetAutopilotFlatPanelMode(Autopilot.FlatPanelMode value) {
            SetSetting(AutopilotFlatPanelModeKey, value.ToString());
        }

        /// <summary>
        /// When off the cover only moves under an open roof, so an emergency close can strand it open.
        /// When on the roof always closes first and the cover after it.
        /// </summary>
        public bool ShouldOperateFlatPanelWithRoofClosed() {
            return GetBoolSetting(ShouldOperateFlatPanelWithRoofClosedKey, defaultValue: false);
        }

        public void SetShouldOperateFlatPanelWithRoofClosed(bool value) {
            SetBoolSetting(ShouldOperateFlatPanelWithRoofClosedKey, value);
        }

        ///////////// Autofocus /////////////
        // A master switch plus three triggers that each switch on separately. With the master off every
        // autofocus check is a no-op. With it on there's always an autofocus at the start of the night
        // (see Context.LastAutofocusTimeUtc).

        public bool IsAutofocusEnabled() {
            return GetBoolSetting(IsAutofocusEnabledKey, defaultValue: false);
        }

        public void SetIsAutofocusEnabled(bool value) {
            SetBoolSetting(IsAutofocusEnabledKey, value);
        }

        public bool ShouldAutofocusOnTemperatureChange() {
            return GetBoolSetting(ShouldAutofocusOnTemperatureChangeKey, defaultValue: false);
        }

        public void SetShouldAutofocusOnTemperatureChange(bool value) {
            SetBoolSetting(ShouldAutofocusOnTemperatureChangeKey, value);
        }

        /// <summary>Temperature change (°C, absolute) since the last autofocus that triggers a refocus.</summary>
        public double GetAutofocusTemperatureThreshold() {
            return GetDoubleSetting(AutofocusTemperatureThresholdKey, DefaultAutofocusTemperatureThreshold);
        }

        public void SetAutofocusTemperatureThreshold(double value) {
            SetDoubleSetting(AutofocusTemperatureThresholdKey, value);
        }

        public bool ShouldAutofocusOnFilterChange() {
            return GetBoolSetting(ShouldAutofocusOnFilterChangeKey, defaultValue: false);
        }

        public void SetShouldAutofocusOnFilterChange(bool value) {
            SetBoolSetting(ShouldAutofocusOnFilterChangeKey, value);
        }

        public bool ShouldAutofocusOnTimeInterval() {
            return GetBoolSetting(ShouldAutofocusOnTimeIntervalKey, defaultValue: false);
        }

        public void SetShouldAutofocusOnTimeInterval(bool value) {
            SetBoolSetting(ShouldAutofocusOnTimeIntervalKey, value);
        }

        public int GetAutofocusIntervalMinutes() {
            int minutes = GetIntSetting(AutofocusIntervalMinutesKey, DefaultAutofocusIntervalMinutes);
            if (minutes <= 0) return DefaultAutofocusIntervalMinutes;
            return minutes;
        }

        public void SetAutofocusIntervalMinutes(int value) {
            SetIntSetting(AutofocusIntervalMinutesKey, value);
        }

        /// <summary>
        /// When on, the server decides when to refocus instead of the local triggers. Between focuses the
        /// focuser goes to the server's recommended position for the filter and temperature.
        /// </summary>
        public bool IsSmartAutofocusEnabled() {
            return GetBoolSetting(IsSmartAutofocusEnabledKey, defaultValue: false);
        }

        /// <summary>Runs EnforceSmartAutofocusProfile when on.</summary>
        public void SetIsSmartAutofocusEnabled(bool value) {
            SetBoolSetting(IsSmartAutofocusEnabledKey, value);
            if (value) EnforceSmartAutofocusProfile();
        }

        /// <summary>
        /// Switches off N.I.N.A.'s filter wheel offsets, or every filter change would have N.I.N.A. move the
        /// focuser and the server move it back. Turning them on again later is left to the user.
        /// </summary>
        public void EnforceSmartAutofocusProfile() {
            try {
                IFocuserSettings? focuserSettings = ProfileService.ActiveProfile?.FocuserSettings;
                if (focuserSettings == null || !focuserSettings.UseFilterWheelOffsets) return;
                focuserSettings.UseFilterWheelOffsets = false;
                Logger.Info("[Astraeus] Smart Autofocus is on, so N.I.N.A.'s 'use filter wheel offsets' was turned off.");
            } catch (Exception ex) {
                Logger.Warning($"[Astraeus] Could not turn off N.I.N.A.'s filter wheel offsets: {ex.Message}");
            }
        }

        ///////////// Cloud upload /////////////

        /// <summary>
        /// Covers manual frames as well as autopilot ones. When off the live preview still reaches the
        /// dashboard over the WebSocket, but nothing is stored.
        /// </summary>
        public bool IsCloudUploadEnabled() {
            return GetBoolSetting(IsCloudUploadEnabledKey, defaultValue: true);
        }

        public void SetIsCloudUploadEnabled(bool value) {
            SetBoolSetting(IsCloudUploadEnabledKey, value);
        }

        ///////////// Calibration master generation /////////////
        // The plugin stacks raw darks, biases and flats from one folder into masters, saves them and
        // uploads them. The raw frames live on this machine, so these settings aren't in the web contract.

        /// <summary>
        /// Folder scanned recursively for raw calibration frames, classified by each file's FITS/XISF
        /// IMAGETYP header. Null means none is set and generation does nothing.
        /// </summary>
        public string? GetCalibrationScanFolder() {
            return GetTextSetting(CalibrationScanFolderKey);
        }

        public void SetCalibrationScanFolder(string? path) {
            SetTextSetting(CalibrationScanFolderKey, path);
        }

        /// <summary>Null means the scan folder.</summary>
        public string? GetCalibrationMastersOutputFolder() {
            return GetTextSetting(CalibrationMastersOutputFolderKey);
        }

        public void SetCalibrationMastersOutputFolder(string? path) {
            SetTextSetting(CalibrationMastersOutputFolderKey, path);
        }

        ///////////// Feed (webcam) /////////////
        // An all-sky or observatory webcam shown on the dashboard. A feed URL is often
        // rtsp://user:pass@host, so these settings stay on this machine and are never sent to or set
        // from the server. Only the frames go to the dashboard.

        public bool IsFeedEnabled() {
            return GetBoolSetting(IsFeedEnabledKey, defaultValue: false);
        }

        public void SetIsFeedEnabled(bool value) {
            SetBoolSetting(IsFeedEnabledKey, value);
        }

        /// <summary>Defaults to HttpSnapshot, the kind that needs no decoding.</summary>
        public FeedSourceKind GetFeedSourceKind() {
            return GetEnumSetting(FeedSourceKindKey, FeedSourceKind.HttpSnapshot);
        }

        public void SetFeedSourceKind(FeedSourceKind value) {
            SetSetting(FeedSourceKindKey, value.ToString());
        }

        /// <summary>Snapshot/MJPEG/RTSP URL.</summary>
        public string? GetFeedUrl() {
            return GetTextSetting(FeedUrlKey);
        }

        public void SetFeedUrl(string? value) {
            SetTextSetting(FeedUrlKey, value);
        }

        public string? GetFeedUsername() {
            return GetTextSetting(FeedUsernameKey);
        }

        public void SetFeedUsername(string? value) {
            SetTextSetting(FeedUsernameKey, value);
        }

        /// <summary>
        /// The camera password, DPAPI-protected like the refresh token because NINA's PluginSettings is
        /// plaintext profile XML. Null when unset or when it can't be decrypted, e.g. in a profile copied
        /// to another machine. That shows up as an auth failure the user can fix.
        /// </summary>
        public string? GetFeedPassword() {
            if (!TryGetSetting(FeedPasswordKey, out string? protectedPassword)) return null;
            return TokenStore.Unprotect(protectedPassword);
        }

        public void SetFeedPassword(string? value) {
            SetSetting(FeedPasswordKey, TokenStore.Protect(value));
        }

        /// <summary>
        /// Longest edge of an uploaded frame. With quality it sets most of the bandwidth. 640 px at
        /// quality 78 is about 35 KB, enough to see if the roof is open or it's clouding over. Clamped
        /// on read so a bad stored value can't flood.
        /// </summary>
        public int GetFeedMaxWidth() {
            int width = GetIntSetting(FeedMaxWidthKey, DefaultFeedMaxWidth);
            return Math.Clamp(width, MinFeedMaxWidth, MaxFeedMaxWidth);
        }

        public void SetFeedMaxWidth(int value) {
            SetIntSetting(FeedMaxWidthKey, Math.Clamp(value, MinFeedMaxWidth, MaxFeedMaxWidth));
        }

        public int GetFeedJpegQuality() {
            int quality = GetIntSetting(FeedJpegQualityKey, DefaultFeedJpegQuality);
            return Math.Clamp(quality, MinFeedJpegQuality, MaxFeedJpegQuality);
        }

        public void SetFeedJpegQuality(int value) {
            SetIntSetting(FeedJpegQualityKey, Math.Clamp(value, MinFeedJpegQuality, MaxFeedJpegQuality));
        }

        /// <summary>
        /// Seconds between frames while somebody is watching. The floor is 2 s because each frame goes to
        /// every watching browser, so this multiplies the server's biggest egress cost. Clamped on read too.
        /// </summary>
        public int GetFeedIntervalSeconds() {
            int seconds = GetIntSetting(FeedIntervalSecondsKey, DefaultFeedIntervalSeconds);
            return Math.Clamp(seconds, MinFeedIntervalSeconds, MaxFeedIntervalSeconds);
        }

        public void SetFeedIntervalSeconds(int value) {
            SetIntSetting(FeedIntervalSecondsKey, Math.Clamp(value, MinFeedIntervalSeconds, MaxFeedIntervalSeconds));
        }
    }
}
