using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record FilterWheelUpdate : WsMessage {
        public FilterWheelUpdate(FilterWheelPayload payload, string? context = null)
            : base(
                type: "update",
                device: "filterwheel",
                context: context,
                payload: ToJsonElement(new DataPayload<FilterWheelPayload>(payload))
            ) { }
    }

    public record FilterWheelPayload : DevicePayload {
        [JsonPropertyName("is_moving")]
        public bool? IsMoving { get; init; }
        [JsonPropertyName("current_position")]
        public int? CurrentPosition { get; init; }
        [JsonPropertyName("selected_filter")]
        public string? SelectedFilter { get; init; }
    }
}