using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record MountUpdate : WsMessage {
        public MountUpdate(MountPayload payload, string? context = null)
            : base(
                type: "update",
                device: "mount",
                context: context,
                payload: ToJsonElement(new DataPayload<MountPayload>(payload))
            ) { }
    }

    public record MountPayload : DevicePayload {
        [JsonPropertyName("sidereal_time")]
        public double? SiderealTime { get; init; }

        public double? Ra { get; init; }

        public double? Dec { get; init; }

        [JsonPropertyName("site_latitude")]
        public double? SiteLatitude { get; init; }

        [JsonPropertyName("site_longitude")]
        public double? SiteLongitude { get; init; }

        [JsonPropertyName("site_elevation")]
        public double? SiteElevation { get; init; }

        [JsonPropertyName("time_to_meridian_flip")]
        public double? TimeToMeridianFlip { get; init; }

        [JsonPropertyName("side_of_pier")]
        public string? SideOfPier { get; init; }

        [JsonPropertyName("is_tracking")]
        public bool? IsTracking { get; init; }

        [JsonPropertyName("is_parked")]
        public bool? IsParked { get; init; }

        [JsonPropertyName("is_home")]
        public bool? IsHome { get; init; }

        public double? Azimuth { get; init; }

        public double? Altitude { get; init; }

        [JsonPropertyName("tracking_mode")]
        public string? TrackingMode { get; init; }

        [JsonPropertyName("custom_right_ascension_rate")]
        public double? CustomRightAscensionRate { get; init; }

        [JsonPropertyName("custom_right_declination_rate")]
        public double? CustomDeclinationRate { get; init; }

        [JsonPropertyName("slewing")]
        public bool? IsSlewing { get; init; }

        [JsonPropertyName("can_find_home")]
        public bool? CanFindHome { get; init; }

        [JsonPropertyName("can_park")]
        public bool? CanPark { get; init; }

        [JsonPropertyName("can_set_park")]
        public bool? CanSetPark { get; init; }

        [JsonPropertyName("can_set_tracking")]
        public bool? CanSetTracking { get; init; }

        [JsonPropertyName("can_set_declination_rate")]
        public bool? CanSetDeclinationRate { get; init; }

        [JsonPropertyName("can_set_right_ascension_rate")]
        public bool? CanSetRightAscensionRate { get; init; }

        public string? Epoch { get; init; }

        [JsonPropertyName("has_unknown_epoch")]
        public bool HasUnknownEpoch { get; init; }

        [JsonPropertyName("alignment_mode")]
        public string? AlignmentMode { get; init; }

        [JsonPropertyName("can_pulse_guide")]
        public bool? CanPulseGuide { get; init; }

        [JsonPropertyName("is_pulse_guiding")]
        public bool? IsPulseGuiding { get; init; }

        [JsonPropertyName("can_set_pier_side")]
        public bool? CanSetPierSide { get; init; }

        [JsonPropertyName("can_slew")]
        public bool? CanSlew { get; init; }

        [JsonPropertyName("can_slew_altaz")]
        public bool? CanSlewAltAz { get; init; }
    }
}