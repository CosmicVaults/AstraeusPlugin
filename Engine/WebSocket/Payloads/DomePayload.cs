using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {

    public sealed record DomeUpdate : WsMessage {
        public DomeUpdate(DomePayload dome, string? context = null)
            : base(
                type: "update",
                device: "dome",
                context: context,
                payload: ToJsonElement(new DataPayload<DomePayload>(dome))
            ) { }
    }

    public record DomePayload : DevicePayload {
        [JsonPropertyName("shutter_status")]
        public string? ShutterStatus { get; init; }

        [JsonPropertyName("can_driver_follow")]
        public bool? CanDriverFollow { get; init; }

        [JsonPropertyName("can_set_shutter")]
        public bool? CanSetShutter { get; init; }

        [JsonPropertyName("can_set_park")]
        public bool? CanSetPark { get; init; }

        [JsonPropertyName("can_set_azimuth")]
        public bool? CanSetAzimuth { get; init; }

        [JsonPropertyName("can_sync_azimuth")]
        public bool? CanSyncAzimuth { get; init; }

        [JsonPropertyName("can_park")]
        public bool? CanPark { get; init; }

        [JsonPropertyName("can_find_home")]
        public bool? CanFindHome { get; init; }

        [JsonPropertyName("at_home")]
        public bool? IsAtHome { get; init; }

        [JsonPropertyName("driver_following")]
        public bool? IsDriverFollowing { get; init; }
        [JsonPropertyName("slewing")]
        public bool? IsSlewing { get; init; }
        public double? Azimuth { get; init; }
        [JsonPropertyName("parked")]
        public bool? IsParked { get; init; }
    }
}