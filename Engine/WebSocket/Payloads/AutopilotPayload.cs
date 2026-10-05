using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record AutopilotUpdate : WsMessage {
        public AutopilotUpdate(AutopilotPayload payload, string? context = null)
            : base(
                type: "update",
                device: "autopilot",
                context: context,
                payload: ToJsonElement(new DataPayload<AutopilotPayload>(payload))
            ) { }
    }

    public record AutopilotPayload {
        [JsonPropertyName("state")]
        public string State { get; init; } = "idle";

        [JsonPropertyName("is_enabled")]
        public bool IsEnabled { get; init; }

        [JsonPropertyName("is_running")]
        public bool IsRunning { get; init; }

        [JsonPropertyName("image_id")]
        public int? ImageId { get; init; }

        [JsonPropertyName("target_name")]
        public string? TargetName { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        /// <summary>
        /// ISO 8601 UTC time the safety monitor has read Safe since, while WaitForSafe is settling on it.
        /// Null otherwise. The browser counts the settle up from this.
        /// </summary>
        [JsonPropertyName("safe_since")]
        public string? SafeSince { get; init; }

        /// <summary>The settle that count runs to, in seconds. Null unless a settle is running.</summary>
        [JsonPropertyName("safe_settle_seconds")]
        public int? SafeSettleSeconds { get; init; }
    }
}