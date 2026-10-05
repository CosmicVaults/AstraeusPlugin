using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record CameraUpdate : WsMessage {
        public CameraUpdate(CameraPayload camera, string? context = null)
            : base(
                type: "update",
                device: "camera",
                context: context,
                payload: ToJsonElement(new DataPayload<CameraPayload>(camera))
            ) { }
    }

    public record CameraPayload : DevicePayload {
        [JsonPropertyName("can_set_temperature")]
        public bool? CanSetTemperature { get; init; }

        [JsonPropertyName("has_shutter")]
        public bool? HasShutter { get; init; }

        public double? Temperature { get; init; }

        public int? Gain { get; init; }

        [JsonPropertyName("default_gain")]
        public int? DefaultGain { get; init; }

        [JsonPropertyName("electrons_per_adu")]
        public double? ElectronsPerADU { get; init; }

        [JsonPropertyName("bin_x")]
        public short? BinX { get; init; }

        [JsonPropertyName("bin_y")]
        public short? BinY { get; init; }

        [JsonPropertyName("bit_depth")]
        public int? BitDepth { get; init; }

        [JsonPropertyName("can_set_offset")]
        public bool? CanSetOffset { get; init; }

        [JsonPropertyName("can_get_gain")]
        public bool? CanGetGain { get; init; }

        [JsonPropertyName("offset_min")]
        public int? OffsetMin { get; init; }

        [JsonPropertyName("offset_max")]
        public int? OffsetMax { get; init; }

        public int? Offset { get; init; }

        [JsonPropertyName("default_offset")]
        public int? DefaultOffset { get; init; }

        [JsonPropertyName("usb_limit")]
        public int? USBLimit { get; init; }

        [JsonPropertyName("can_set_usb_limit")]
        public bool? CanSetUSBLimit { get; init; }

        [JsonPropertyName("usb_limit_min")]
        public int? USBLimitMin { get; init; }

        [JsonPropertyName("usb_limit_max")]
        public int? USBLimitMax { get; init; }

        [JsonPropertyName("is_sub_sample_enabled")]
        public bool? IsSubSampleEnabled { get; init; }

        [JsonPropertyName("camera_state")]
        public string? CameraState { get; init; }

        [JsonPropertyName("x_size")]
        public int? XSize { get; init; }

        [JsonPropertyName("y_size")]
        public int? YSize { get; init; }

        [JsonPropertyName("pixel_size")]
        public double? PixelSize { get; init; }

        [JsonPropertyName("has_battery")]
        public bool? HasBattery { get; init; }

        public int? Battery { get; init; }

        [JsonPropertyName("gain_min")]
        public int? GainMin { get; init; }

        [JsonPropertyName("gain_max")]
        public int? GainMax { get; init; }

        [JsonPropertyName("can_set_gain")]
        public bool? CanSetGain { get; init; }

        [JsonPropertyName("cooler_on")]
        public bool? IsCoolerOn { get; init; }

        [JsonPropertyName("cooler_power")]
        public double? CoolerPower { get; init; }

        [JsonPropertyName("has_dew_heater")]
        public bool? HasDewHeater { get; init; }

        [JsonPropertyName("dew_heater_on")]
        public bool? IsDewHeaterOn { get; init; }

        [JsonPropertyName("can_sub_sample")]
        public bool? CanSubSample { get; init; }

        [JsonPropertyName("sub_sample_x")]
        public int? SubSampleX { get; init; }

        [JsonPropertyName("sub_sample_y")]
        public int? SubSampleY { get; init; }

        [JsonPropertyName("sub_sample_width")]
        public int? SubSampleWidth { get; init; }

        [JsonPropertyName("sub_sample_height")]
        public int? SubSampleHeight { get; init; }

        [JsonPropertyName("temperature_set_point")]
        public double? TemperatureSetPoint { get; init; }

        [JsonPropertyName("readout_mode")]
        public short? ReadoutMode { get; init; }

        [JsonPropertyName("readout_mode_for_snap_images")]
        public short? ReadoutModeForSnapImages { get; init; }

        [JsonPropertyName("readout_mode_for_normal_images")]
        public short? ReadoutModeForNormalImages { get; init; }

        /// <summary>The readout mode names in index order. The hub's capture command sends one back by name.</summary>
        [JsonPropertyName("readout_modes")]
        public IReadOnlyList<string>? ReadoutModes { get; init; }

        [JsonPropertyName("is_exposing")]
        public bool? IsExposing { get; init; }

        [JsonPropertyName("sensor_type")]
        public string? SensorType { get; init; }

        [JsonPropertyName("bayer_offset_x")]
        public short? BayerOffsetX { get; init; }

        [JsonPropertyName("bayer_offset_y")]
        public short? BayerOffsetY { get; init; }

        [JsonPropertyName("exposure_max")]
        public double? ExposureMax { get; init; }

        [JsonPropertyName("exposure_min")]
        public double? ExposureMin { get; init; }

        [JsonPropertyName("live_view_enabled")]
        public bool? IsLiveViewEnabled { get; init; }

        [JsonPropertyName("can_show_live_view")]
        public bool? CanShowLiveView { get; init; }

        [JsonPropertyName("exposure_remaining")]
        public double? ExposureRemaining { get; init; }
    }
}