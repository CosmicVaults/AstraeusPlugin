using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record SafetyMonitorUpdate : WsMessage {
        public SafetyMonitorUpdate(SafetyMonitorPayload payload, string? context = null)
            : base(
                type: "update",
                device: "safetymonitor",
                context: context,
                payload: ToJsonElement(new DataPayload<SafetyMonitorPayload>(payload))
            ) { }
    }

    public record SafetyMonitorPayload : DevicePayload {
        [JsonPropertyName("is_safe")]
        public bool? IsSafe { get; init; }
    }
}