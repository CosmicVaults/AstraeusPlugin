using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record RotatorUpdate : WsMessage {
        public RotatorUpdate(RotatorPayload payload, string? context = null)
            : base(
                type: "update",
                device: "rotator",
                context: context,
                payload: ToJsonElement(new DataPayload<RotatorPayload>(payload))
            ) { }
    }

    public record RotatorPayload : DevicePayload {
        [JsonPropertyName("can_reverse")]
        public bool? CanReverse { get; init; }
        [JsonPropertyName("reverse")]
        public bool? IsReversed { get; init; }

        [JsonPropertyName("mechanical_position")]
        public double? MechanicalPosition { get; init; }
        public double? Position { get; init; }

        [JsonPropertyName("is_moving")]
        public bool? IsMoving { get; init; }

        [JsonPropertyName("step_size")]
        public double? StepSize { get; init; }

        [JsonPropertyName("synced")]
        public bool? IsSynced { get; init; }
    }
}