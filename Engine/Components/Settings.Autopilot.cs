using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Utility;
using CosmicVaults.NINA.Astraeus.Engine.Components;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {

        ///////////// Change-driven push /////////////
        // Autopilot settings and default devices write straight to the profile and raise a Changed
        // event. Without this wiring the server would only see the values from connect time, and a
        // change on the options page would leave the two sides out of step until the next reconnect.

        // Set while a settingsChanged from the server is applied, so we don't echo its values straight
        // back. It's per thread, so a change made meanwhile on another thread (say the autopilot
        // switching itself off) is still pushed.
        [ThreadStatic]
        private static bool _isApplyingAutopilotUpdate;

        private DebouncedPush? _autopilotPush;

        // Keys waiting for the next debounced push. The autopilot block also carries scheduler fields
        // only the web sets, so we send just the changed fields. The whole block would overwrite them.
        private readonly HashSet<string> _pendingAutopilotKeys = new();
        private readonly object _pendingAutopilotKeysLock = new();

        private void SubscribeAutopilotSettings() {
            _autopilotPush = new DebouncedPush(
                PushChangedAutopilotSettingsAsync,
                message => LogError($"Failed to push autopilot settings: {message}", device: "autopilot"));
            if (Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot))
                autopilot.SettingsChanged += OnAutopilotSettingChanged;
            foreach (IDeviceComponent device in Observatory.GetComponents<IDeviceComponent>())
                device.DefaultDeviceChanged += OnAutopilotDefaultDeviceChanged;
        }

        private void UnsubscribeAutopilotSettings() {
            if (Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot))
                autopilot.SettingsChanged -= OnAutopilotSettingChanged;
            foreach (IDeviceComponent device in Observatory.GetComponents<IDeviceComponent>())
                device.DefaultDeviceChanged -= OnAutopilotDefaultDeviceChanged;
        }

        private void OnAutopilotSettingChanged(object? sender, Autopilot.SettingChangedEventArgs eventArgs) {
            if (_isApplyingAutopilotUpdate) return;
            // No key means a machine-local setting the server doesn't carry.
            if (eventArgs.Key is null) return;
            QueueAutopilotKeys(eventArgs.Key);
        }

        /// <summary>
        /// A default-device change sends the whole device map plus the two flat safety and roof fields
        /// kept for compatibility. The plugin owns all three, so sending them together keeps them consistent.
        /// </summary>
        private void OnAutopilotDefaultDeviceChanged(object? sender, EventArgs eventArgs) {
            if (_isApplyingAutopilotUpdate) return;
            QueueAutopilotKeys("default_devices", "safety_device_id", "roof_device_id");
        }

        private void QueueAutopilotKeys(params string[] keys) {
            lock (_pendingAutopilotKeysLock)
                foreach (string key in keys)
                    _pendingAutopilotKeys.Add(key);
            _autopilotPush?.Schedule();
        }

        private async Task HandleAutopilotUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;
            if (!Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot)) return;

            SettingsUpdateBatch update = BuildAutopilotUpdate(autopilot, data);
            if (!TryApplyWithoutEcho(update)) {
                LogWarning("Ignored an autopilot settings update from the server: wrong type for " +
                           $"{update.InvalidFieldNames}.", device: "autopilot");
                await PushAutopilotSettingsAsync();
                return;
            }
            LogAutopilotSettings();
        }

        private static bool TryApplyWithoutEcho(SettingsUpdateBatch update) {
            _isApplyingAutopilotUpdate = true;
            try {
                return update.TryApply();
            } finally {
                _isApplyingAutopilotUpdate = false;
            }
        }

        // The flat panel mode and auto-connect decide what the next roof opening does, so a mid-night
        // change to either has to show in the log.
        private void LogAutopilotSettings() {
            AstraeusSettings settings = Observatory.Settings;
            string devices = $"safetyDeviceId={settings.GetDefaultDeviceId("safetymonitor")}, " +
                             $"roofDeviceId={settings.GetDefaultDeviceId("dome")}";
            string roof = $"roofMode={settings.GetAutopilotRoofMode()}, " +
                          $"flatPanelMode={settings.GetAutopilotFlatPanelMode()}";
            string cooling = $"cooling={settings.IsCameraCoolingEnabled()} at " +
                             $"{settings.GetCameraCoolingTemperature():F1}°C";
            Log($"Autopilot settings updated via WS: isEnabled={settings.IsAutopilotEnabled()}, {devices}, {roof}, " +
                $"cloudUpload={settings.IsCloudUploadEnabled()}, " +
                $"autoConnect={settings.ShouldAutoConnectEquipment()}, {cooling}", device: "autopilot");
        }

        private SettingsUpdateBatch BuildAutopilotUpdate(Autopilot.Autopilot autopilot, JsonElement data) {
            bool hasDefaultDevices = data.TryGetProperty("default_devices", out JsonElement defaultDevices)
                                     && defaultDevices.ValueKind != JsonValueKind.Null;
            Dictionary<string, string>? defaultDeviceIds =
                hasDefaultDevices ? ReadDefaultDeviceIds(defaultDevices) : null;

            return new SettingsUpdateBatch(data)
                .Bool("is_autopilot_enabled", value => autopilot.IsEnabled = value)
                .String("safety_device_id", value => SetDefaultDevice("safetymonitor", value))
                .EnumName<Autopilot.RoofMode>("roof_mode", value => autopilot.RoofMode = value)
                .String("roof_device_id", value => SetDefaultDevice("dome", value))
                .Bool("auto_connect_equipment", value => autopilot.ShouldAutoConnectEquipment = value)
                .Bool("is_mount_limit_enabled", value => autopilot.IsMountLimitEnabled = value)
                .Bool("is_altitude_limit_enabled", value => autopilot.IsAltitudeLimitEnabled = value)
                .Double("min_altitude_degrees", value => autopilot.MinAltitudeDegrees = value)
                .Bool("is_custom_horizon_limit_enabled", value => autopilot.IsCustomHorizonLimitEnabled = value)
                .Bool("is_meridian_flip_enabled", value => autopilot.IsMeridianFlipEnabled = value)
                .Double("recenter_tolerance_arcmin", value => autopilot.RecenterToleranceArcmin = value)
                .RoundedInt("safe_settle_seconds", value => autopilot.SafeSettleSeconds = value)
                .EnumName<Autopilot.RotatorFallbackMode>("rotator_fallback_mode",
                    value => autopilot.RotatorFallbackMode = value)
                .Double("rotator_fallback_angle", value => autopilot.RotatorFallbackAngle = value)
                .Bool("park_mount_to_open_roof", value => autopilot.ShouldParkMountToOpenRoof = value)
                .EnumName<Autopilot.FlatPanelMode>("flat_panel_mode", value => autopilot.FlatPanelMode = value)
                .Bool("flat_panel_operates_with_roof_closed",
                    value => autopilot.ShouldOperateFlatPanelWithRoofClosed = value)
                .EnumName<Autopilot.OperatingWindow>("operating_window", value => autopilot.OperatingWindow = value)
                .Bool("is_camera_cooling_enabled", value => autopilot.IsCameraCoolingEnabled = value)
                .Double("camera_cooling_temperature_celsius", value => autopilot.CameraCoolingTemperature = value)
                .Bool("is_autofocus_enabled", value => autopilot.IsAutofocusEnabled = value)
                .Bool("autofocus_on_temperature_change", value => autopilot.ShouldAutofocusOnTemperatureChange = value)
                .Double("autofocus_temperature_threshold", value => autopilot.AutofocusTemperatureThreshold = value)
                .Bool("autofocus_on_filter_change", value => autopilot.ShouldAutofocusOnFilterChange = value)
                .Bool("autofocus_on_time_interval", value => autopilot.ShouldAutofocusOnTimeInterval = value)
                .Int("autofocus_interval_minutes", value => autopilot.AutofocusIntervalMinutes = value)
                .Bool("is_smart_autofocus_enabled", value => autopilot.IsSmartAutofocusEnabled = value)
                // Absent fields are never applied, so an older server block without this one leaves the
                // local default (on) alone.
                .Bool("is_cloud_upload_enabled", value => autopilot.IsCloudUploadEnabled = value)
                // After the flat safety and roof fields above, so if a server sends both, the map wins.
                .Custom("default_devices", hasDefaultDevices, defaultDeviceIds != null,
                    () => ApplyDefaultDeviceIds(defaultDeviceIds!));
        }

        /// <summary>
        /// The device map as device type -> device id. Null when it has the wrong shape, which rejects
        /// the whole update. Null entries are left out.
        /// </summary>
        private static Dictionary<string, string>? ReadDefaultDeviceIds(JsonElement defaultDevices) {
            if (defaultDevices.ValueKind != JsonValueKind.Object) return null;

            Dictionary<string, string> defaultDeviceIds = new Dictionary<string, string>();
            foreach (JsonProperty entry in defaultDevices.EnumerateObject()) {
                if (entry.Value.ValueKind == JsonValueKind.Null) continue;
                if (entry.Value.ValueKind != JsonValueKind.String) return null;
                defaultDeviceIds[entry.Name] = entry.Value.GetString() ?? BaseComponent.NoDevice;
            }
            return defaultDeviceIds;
        }

        // Unknown device types are ignored, so nothing gets written to a dead setting key.
        private void ApplyDefaultDeviceIds(Dictionary<string, string> defaultDeviceIds) {
            foreach ((string deviceType, string deviceId) in defaultDeviceIds) {
                SetDefaultDevice(deviceType, deviceId);
            }
        }

        private void SetDefaultDevice(string deviceType, string deviceId) {
            IDeviceComponent? component = Observatory.GetComponents<IDeviceComponent>()
                .FirstOrDefault(candidate => candidate.DeviceType == deviceType);
            component?.SetDefaultDevice(deviceId, "web settings");
        }

        /// <summary>Sends nothing if the queue is empty, since a full push may already have drained it.</summary>
        private Task PushChangedAutopilotSettingsAsync() {
            string[] changedKeys;
            lock (_pendingAutopilotKeysLock) {
                if (_pendingAutopilotKeys.Count == 0) return Task.CompletedTask;
                changedKeys = _pendingAutopilotKeys.ToArray();
                _pendingAutopilotKeys.Clear();
            }
            AutopilotSettingsPayload payload = BuildAutopilotPayload();
            return WebSocketBus.SendAsync(SettingsUpdate.CreatePartial(payload, "autopilot", changedKeys));
        }

        /// <summary>
        /// Sends the whole block, used on connect where NINA's values win for the settings it owns.
        /// Fields it doesn't own are left out, so a merging server keeps them. Replaces any queued partial push.
        /// </summary>
        internal Task PushAutopilotSettingsAsync() {
            lock (_pendingAutopilotKeysLock) _pendingAutopilotKeys.Clear();
            return WebSocketBus.SendAsync(SettingsUpdate.Create(BuildAutopilotPayload(), "autopilot"));
        }

        private AutopilotSettingsPayload BuildAutopilotPayload() {
            return new AutopilotSettingsPayload {
                IsAutopilotEnabled = Observatory.Settings.IsAutopilotEnabled(),
                SafetyDeviceId = Observatory.Settings.GetDefaultDeviceId("safetymonitor"),
                RoofMode = Observatory.Settings.GetAutopilotRoofMode().ToString(),
                RoofDeviceId = Observatory.Settings.GetDefaultDeviceId("dome"),
                DefaultDevices = Observatory.GetComponents<IDeviceComponent>()
                    .ToDictionary(component => component.DeviceType, component => component.DefaultDevice),
                ShouldAutoConnectEquipment = Observatory.Settings.ShouldAutoConnectEquipment(),
                IsCameraCoolingEnabled = Observatory.Settings.IsCameraCoolingEnabled(),
                CameraCoolingTemperatureCelsius = Observatory.Settings.GetCameraCoolingTemperature(),
                OperatingWindow = Observatory.Settings.GetAutopilotOperatingWindow().ToString(),
                IsMountLimitEnabled = Observatory.Settings.IsMountLimitEnabled(),
                IsAltitudeLimitEnabled = Observatory.Settings.IsAltitudeLimitEnabled(),
                MinAltitudeDegrees = Observatory.Settings.GetMountMinAltitudeDegrees(),
                IsCustomHorizonLimitEnabled = Observatory.Settings.IsCustomHorizonLimitEnabled(),
                IsMeridianFlipEnabled = Observatory.Settings.IsMeridianFlipEnabled(),
                RecenterToleranceArcmin = Observatory.Settings.GetRecenterToleranceArcmin(),
                SafeSettleSeconds = Observatory.Settings.GetSafeSettleSeconds(),
                RotatorFallbackMode = Observatory.Settings.GetAutopilotRotatorFallbackMode().ToString(),
                RotatorFallbackAngle = Observatory.Settings.GetAutopilotRotatorFallbackAngle(),
                ShouldParkMountToOpenRoof = Observatory.Settings.ShouldParkMountToOpenRoof(),
                FlatPanelMode = Observatory.Settings.GetAutopilotFlatPanelMode().ToString(),
                ShouldOperateFlatPanelWithRoofClosed = Observatory.Settings.ShouldOperateFlatPanelWithRoofClosed(),
                IsAutofocusEnabled = Observatory.Settings.IsAutofocusEnabled(),
                ShouldAutofocusOnTemperatureChange = Observatory.Settings.ShouldAutofocusOnTemperatureChange(),
                AutofocusTemperatureThreshold = Observatory.Settings.GetAutofocusTemperatureThreshold(),
                ShouldAutofocusOnFilterChange = Observatory.Settings.ShouldAutofocusOnFilterChange(),
                ShouldAutofocusOnTimeInterval = Observatory.Settings.ShouldAutofocusOnTimeInterval(),
                AutofocusIntervalMinutes = Observatory.Settings.GetAutofocusIntervalMinutes(),
                IsSmartAutofocusEnabled = Observatory.Settings.IsSmartAutofocusEnabled(),
                IsCloudUploadEnabled = Observatory.Settings.IsCloudUploadEnabled()
            };
        }
    }
}