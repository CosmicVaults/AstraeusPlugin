using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {

    public sealed record FlatUpdate : WsMessage {
        public FlatUpdate(FlatPayload payload, string? context = null)
            : base(
                type: "update",
                device: "flat",
                context: context,
                payload: ToJsonElement(new DataPayload<FlatPayload>(payload))
            ) { }
    }

    public record FlatPayload : DevicePayload {
        [JsonPropertyName("light_on")]
        public bool? IsLightOn { get; init; }

        public int? Brightness { get; init; }

        [JsonPropertyName("cover_state")]
        public string? CoverState { get; init; }

        [JsonPropertyName("min_brightness")]
        public int? MinBrightness { get; init; }

        [JsonPropertyName("max_brightness")]
        public int? MaxBrightness { get; init; }

        [JsonPropertyName("supports_on_off")]
        public bool? SupportsOnOff { get; init; }
    }
}