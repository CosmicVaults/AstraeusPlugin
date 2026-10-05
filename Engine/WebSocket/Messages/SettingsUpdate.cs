using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record SettingsUpdate : WsMessage {
        private SettingsUpdate(string device, JsonElement payload)
            : base(type: "update", device: device, context: "settings", payload: payload) { }

        public static SettingsUpdate Create<TPayload>(TPayload payload, string device) {
            return new SettingsUpdate(device, ToJsonElement(new DataPayload<TPayload>(payload)));
        }

        /// <summary>
        /// Pushes only the autopilot settings in changedKeys, since the whole block could overwrite the
        /// scheduler fields the web sets, which N.I.N.A. doesn't own.
        /// </summary>
        public static SettingsUpdate CreatePartial(AutopilotSettingsPayload payload, string device,
            IReadOnlyCollection<string> changedKeys) {
            return new SettingsUpdate(device, ToFilteredDataPayload(payload, changedKeys));
        }
    }

    public record AstrometryPayload {
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public double? Elevation { get; init; }

        // One altitude per integer azimuth. Empty means no horizon is loaded, since a null would be
        // dropped by WhenWritingNull and never clear the server's copy.
        [JsonPropertyName("horizon_altitudes")]
        public double[] HorizonAltitudes { get; init; } = System.Array.Empty<double>();

        [JsonPropertyName("horizon_file_name")]
        public string HorizonFileName { get; init; } = "";
    }

    public record TelescopeSettingsPayload {
        public string? Name { get; init; }

        [JsonPropertyName("mount_name")]
        public string? MountName { get; init; }

        [JsonPropertyName("focal_length")]
        public double? FocalLength { get; init; }

        [JsonPropertyName("focal_ratio")]
        public double? FocalRatio { get; init; }

        [JsonPropertyName("settle_time")]
        public int? SettleTime { get; init; }

        [JsonPropertyName("no_sync")]
        public bool NoSync { get; init; }

        [JsonPropertyName("time_sync")]
        public bool TimeSync { get; init; }

        [JsonPropertyName("primary_reversed")]
        public bool PrimaryReversed { get; init; }

        [JsonPropertyName("secondary_reversed")]
        public bool SecondaryReversed { get; init; }

        [JsonPropertyName("location_sync_direction")]
        public TelescopeLocationSyncDirection LocationSyncDirection { get; init; }
    }

    /// <summary>N.I.N.A.'s Meridian Flip settings (IMeridianFlipSettings), in minutes and seconds.</summary>
    public record MeridianFlipSettingsPayload {
        [JsonPropertyName("minutes_after_meridian")]
        public double MinutesAfterMeridian { get; init; }

        [JsonPropertyName("max_minutes_after_meridian")]
        public double MaxMinutesAfterMeridian { get; init; }

        [JsonPropertyName("pause_time_before_meridian")]
        public double PauseTimeBeforeMeridian { get; init; }

        [JsonPropertyName("recenter")]
        public bool Recenter { get; init; }

        [JsonPropertyName("settle_time")]
        public int SettleTime { get; init; }

        [JsonPropertyName("use_side_of_pier")]
        public bool UseSideOfPier { get; init; }

        [JsonPropertyName("auto_focus_after_flip")]
        public bool AutoFocusAfterFlip { get; init; }

        [JsonPropertyName("rotate_image_after_flip")]
        public bool RotateImageAfterFlip { get; init; }
    }

    public record FilterWheelSettingsPayload {
        [JsonPropertyName("disable_guiding_on_filter_change")]
        public bool DisableGuidingOnFilterChange { get; init; }

        public List<FilterPayload> Filters { get; init; } = new();
    }

    public record FilterPayload {
        public int Position { get; init; }
        public string? Name { get; init; }

        // N.I.N.A.'s per-filter focus offset and autofocus filter, so the Smart Autofocus card can show
        // them beside the offsets the server works out.
        [JsonPropertyName("focus_offset")]
        public int FocusOffset { get; init; }

        [JsonPropertyName("is_autofocus_filter")]
        public bool IsAutofocusFilter { get; init; }
    }

    public record CameraSettingsPayload {
        [JsonPropertyName("pixel_size")]
        public double? PixelSize { get; init; }

        [JsonPropertyName("bit_depth")]
        public double? BitDepth { get; init; }

        [JsonPropertyName("bayer_pattern")]
        public BayerPatternEnum? BayerPattern { get; init; }
    }

    public record FocuserSettingsPayload {
        [JsonPropertyName("autofocus_exposure_time")]
        public double? AutoFocusExposureTime { get; init; }

        [JsonPropertyName("autofocus_initial_offset_steps")]
        public int AutoFocusInitialOffsetSteps { get; init; }

        [JsonPropertyName("autofocus_step_size")]
        public int AutoFocusStepSize { get; init; }

        [JsonPropertyName("use_filter_wheel_offsets")]
        public bool UseFilterWheelOffsets { get; init; }

        [JsonPropertyName("autofocus_disable_guiding")]
        public bool AutoFocusDisableGuiding { get; init; }

        [JsonPropertyName("focuser_settle_time")]
        public int FocuserSettleTime { get; init; }

        [JsonPropertyName("autofocus_total_number_of_attempts")]
        public int AutoFocusTotalNumberOfAttempts { get; init; }

        [JsonPropertyName("autofocus_number_of_frames_per_point")]
        public int AutoFocusNumberOfFramesPerPoint { get; init; }

        [JsonPropertyName("autofocus_inner_crop_ratio")]
        public double? AutoFocusInnerCropRatio { get; init; }

        [JsonPropertyName("autofocus_outer_crop_ratio")]
        public double? AutoFocusOuterCropRatio { get; init; }

        [JsonPropertyName("autofocus_use_brightest_stars")]
        public int AutoFocusUseBrightestStars { get; init; }

        [JsonPropertyName("backlash_in")]
        public int BacklashIn { get; init; }

        [JsonPropertyName("backlash_out")]
        public int BacklashOut { get; init; }

        [JsonPropertyName("autofocus_binning")]
        public short AutoFocusBinning { get; init; }

        [JsonPropertyName("autofocus_curve_fitting")]
        public AFCurveFittingEnum AutoFocusCurveFitting { get; init; }

        [JsonPropertyName("autofocus_method")]
        public AFMethodEnum AutoFocusMethod { get; init; }

        [JsonPropertyName("contrast_detection_method")]
        public ContrastDetectionMethodEnum ContrastDetectionMethod { get; init; }

        [JsonPropertyName("backlash_compensation_model")]
        public BacklashCompensationModel BacklashCompensationModel { get; init; }

        [JsonPropertyName("autofocus_timeout_seconds")]
        public int AutoFocusTimeoutSeconds { get; init; }

        [JsonPropertyName("r_squared_threshold")]
        public double? RSquaredThreshold { get; init; }
    }

    public record AlpacaSettingsPayload {
        [JsonPropertyName("number_of_polls")]
        public int NumberOfPolls { get; init; }

        [JsonPropertyName("poll_interval")]
        public int PollInterval { get; init; }

        [JsonPropertyName("discovery_port")]
        public int DiscoveryPort { get; init; }

        [JsonPropertyName("discovery_duration")]
        public double? DiscoveryDuration { get; init; }

        [JsonPropertyName("resolve_dns_name")]
        public bool ResolveDnsName { get; init; }

        [JsonPropertyName("use_ipv4")]
        public bool UseIPv4 { get; init; }

        [JsonPropertyName("use_ipv6")]
        public bool UseIPv6 { get; init; }

        [JsonPropertyName("use_https")]
        public bool UseHttps { get; init; }
    }

    public record DomeSettingsPayload {
        [JsonPropertyName("scope_position_east_west_mm")]
        public double? ScopePositionEastWest_mm { get; init; }

        [JsonPropertyName("scope_position_north_south_mm")]
        public double? ScopePositionNorthSouth_mm { get; init; }

        [JsonPropertyName("scope_position_up_down_mm")]
        public double? ScopePositionUpDown_mm { get; init; }

        [JsonPropertyName("dome_radius_mm")]
        public double? DomeRadius_mm { get; init; }

        [JsonPropertyName("gem_axis_mm")]
        public double? GemAxis_mm { get; init; }

        [JsonPropertyName("lateral_axis_mm")]
        public double? LateralAxis_mm { get; init; }

        [JsonPropertyName("azimuth_tolerance_degrees")]
        public double? AzimuthTolerance_degrees { get; init; }

        [JsonPropertyName("find_home_before_park")]
        public bool FindHomeBeforePark { get; init; }

        [JsonPropertyName("dome_sync_timeout_seconds")]
        public int DomeSyncTimeoutSeconds { get; init; }

        [JsonPropertyName("synchronize_during_mount_slew")]
        public bool SynchronizeDuringMountSlew { get; init; }

        [JsonPropertyName("sync_slew_dome_when_mount_slews")]
        public bool SyncSlewDomeWhenMountSlews { get; init; }

        [JsonPropertyName("rotate_degrees")]
        public double? RotateDegrees { get; init; }

        [JsonPropertyName("close_on_unsafe")]
        public bool CloseOnUnsafe { get; init; }

        [JsonPropertyName("park_mount_before_shutter_move")]
        public bool ParkMountBeforeShutterMove { get; init; }

        [JsonPropertyName("refuse_unsafe_shutter_move")]
        public bool RefuseUnsafeShutterMove { get; init; }

        [JsonPropertyName("refuse_unsafe_shutter_open_sans_safety_device")]
        public bool RefuseUnsafeShutterOpenSansSafetyDevice { get; init; }

        [JsonPropertyName("refuse_unpark_without_shutter_open")]
        public bool RefuseUnparkWithoutShutterOpen { get; init; }

        [JsonPropertyName("park_dome_before_shutter_move")]
        public bool ParkDomeBeforeShutterMove { get; init; }

        [JsonPropertyName("mount_type")]
        public MountTypeEnum MountType { get; init; }

        [JsonPropertyName("dec_offset_horizontal_mm")]
        public double? DecOffsetHorizontal_mm { get; init; }

        [JsonPropertyName("settle_time_seconds")]
        public int SettleTimeSeconds { get; init; }
    }

    public record AutopilotSettingsPayload {
        [JsonPropertyName("is_autopilot_enabled")]
        public bool IsAutopilotEnabled { get; init; }

        [JsonPropertyName("safety_device_id")]
        public string? SafetyDeviceId { get; init; }

        [JsonPropertyName("roof_mode")]
        public string? RoofMode { get; init; }

        [JsonPropertyName("roof_device_id")]
        public string? RoofDeviceId { get; init; }

        /// <summary>
        /// Each component's default device keyed by DeviceType, with "None" for no device. A map, so adding a
        /// component doesn't widen the contract. SafetyDeviceId and RoofDeviceId stay for compatibility.
        /// </summary>
        [JsonPropertyName("default_devices")]
        public Dictionary<string, string?> DefaultDevices { get; init; } = new();

        [JsonPropertyName("auto_connect_equipment")]
        public bool ShouldAutoConnectEquipment { get; init; }

        /// <summary>
        /// Whether the autopilot cools the camera at dusk and warms it at dawn, to
        /// camera_cooling_temperature_celsius.
        /// </summary>
        [JsonPropertyName("is_camera_cooling_enabled")]
        public bool IsCameraCoolingEnabled { get; init; }

        [JsonPropertyName("camera_cooling_temperature_celsius")]
        public double? CameraCoolingTemperatureCelsius { get; init; }

        /// <summary>Sunset, Nautical or Astronomical, serialised by name like roof_mode.</summary>
        [JsonPropertyName("operating_window")]
        public string? OperatingWindow { get; init; }

        [JsonPropertyName("is_mount_limit_enabled")]
        public bool IsMountLimitEnabled { get; init; }

        [JsonPropertyName("is_altitude_limit_enabled")]
        public bool IsAltitudeLimitEnabled { get; init; }

        [JsonPropertyName("min_altitude_degrees")]
        public double? MinAltitudeDegrees { get; init; }

        [JsonPropertyName("is_custom_horizon_limit_enabled")]
        public bool IsCustomHorizonLimitEnabled { get; init; }

        /// <summary>
        /// Whether the autopilot flips the mount to carry a target past the meridian limit. With this
        /// off, the plugin refuses those targets whatever the server's per-project "allow meridian flip" says.
        /// </summary>
        [JsonPropertyName("is_meridian_flip_enabled")]
        public bool IsMeridianFlipEnabled { get; init; }

        [JsonPropertyName("recenter_tolerance_arcmin")]
        public double? RecenterToleranceArcmin { get; init; }

        [JsonPropertyName("safe_settle_seconds")]
        public int? SafeSettleSeconds { get; init; }
        
        [JsonPropertyName("rotator_fallback_mode")]
        public string? RotatorFallbackMode { get; init; }

        [JsonPropertyName("rotator_fallback_angle")]
        public double? RotatorFallbackAngle { get; init; }

        [JsonPropertyName("park_mount_to_open_roof")]
        public bool ShouldParkMountToOpenRoof { get; init; }

        /// <summary>Ignore, EnsureOpen or OpenAndClose, serialised by name like roof_mode.</summary>
        [JsonPropertyName("flat_panel_mode")]
        public string? FlatPanelMode { get; init; }

        [JsonPropertyName("flat_panel_operates_with_roof_closed")]
        public bool ShouldOperateFlatPanelWithRoofClosed { get; init; }

        ///////////// Autofocus /////////////
        // The master switch gates the three cadence triggers. Smart autofocus overrides them all and
        // lets the server pick when to focus and the focus position.

        [JsonPropertyName("is_autofocus_enabled")]
        public bool IsAutofocusEnabled { get; init; }

        [JsonPropertyName("autofocus_on_temperature_change")]
        public bool ShouldAutofocusOnTemperatureChange { get; init; }

        [JsonPropertyName("autofocus_temperature_threshold")]
        public double? AutofocusTemperatureThreshold { get; init; }

        [JsonPropertyName("autofocus_on_filter_change")]
        public bool ShouldAutofocusOnFilterChange { get; init; }

        [JsonPropertyName("autofocus_on_time_interval")]
        public bool ShouldAutofocusOnTimeInterval { get; init; }

        [JsonPropertyName("autofocus_interval_minutes")]
        public int AutofocusIntervalMinutes { get; init; }

        [JsonPropertyName("is_smart_autofocus_enabled")]
        public bool IsSmartAutofocusEnabled { get; init; }

        [JsonPropertyName("is_cloud_upload_enabled")]
        public bool IsCloudUploadEnabled { get; init; }
    }

    public record ImageFileSettingsPayload {
        [JsonPropertyName("file_pattern")]
        public string? FilePattern { get; init; }

        [JsonPropertyName("file_pattern_dark")]
        public string? FilePatternDARK { get; init; }

        [JsonPropertyName("file_pattern_bias")]
        public string? FilePatternBIAS { get; init; }

        [JsonPropertyName("file_pattern_flat")]
        public string? FilePatternFLAT { get; init; }

        [JsonPropertyName("file_type")]
        public FileTypeEnum FileType { get; init; }

        [JsonPropertyName("tiff_compression_type")]
        public TIFFCompressionTypeEnum TIFFCompressionType { get; init; }

        [JsonPropertyName("xisf_compression_type")]
        public XISFCompressionTypeEnum XISFCompressionType { get; init; }

        [JsonPropertyName("xisf_checksum_type")]
        public XISFChecksumTypeEnum XISFChecksumType { get; init; }

        [JsonPropertyName("xisf_byte_shuffling")]
        public bool XISFByteShuffling { get; init; }

        [JsonPropertyName("fits_compression_type")]
        public FITSCompressionTypeEnum FITSCompressionType { get; init; }

        [JsonPropertyName("fits_add_fz_extension")]
        public bool FITSAddFzExtension { get; init; }

        [JsonPropertyName("fits_use_legacy_writer")]
        public bool FITSUseLegacyWriter { get; init; }
    }
}