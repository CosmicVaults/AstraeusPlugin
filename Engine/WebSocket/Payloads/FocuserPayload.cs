using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {

    public sealed record FocuserUpdate : WsMessage {
        public FocuserUpdate(FocuserPayload payload, string? context = null)
            : base(
                type: "update",
                device: "focuser",
                context: context,
                payload: ToJsonElement(new DataPayload<FocuserPayload>(payload))
            ) { }
    }

    public record FocuserPayload : DevicePayload {
        public double? Position { get; init; }
        [JsonPropertyName("step_size")]
        public double? StepSize { get; init; }
        public double? Temperature { get; init; }
        [JsonPropertyName("is_moving")]
        public bool? IsMoving { get; init; }
        [JsonPropertyName("is_settling")]
        public bool? IsSettling { get; init; }
        [JsonPropertyName("temperature_compensation")]
        public bool? IsTemperatureCompensationOn { get; init; }
        [JsonPropertyName("is_temp_comp_available")]
        public bool? IsTempCompAvailable { get; init; }
    }
}