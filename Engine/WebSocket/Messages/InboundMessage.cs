using System.Text.Json;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record InboundMessage {
        public string Id { get; init; } = default!;
        public string Type { get; init; } = default!;
        public string Device { get; init; } = default!;
        public string? Context { get; init; }
        public JsonElement? Payload { get; init; }

        /// <summary>Set on the server's connect acknowledgement, which carries no type.</summary>
        public string? Role { get; init; }

        /// <summary>Human-readable text on the acknowledgement and on typed error frames.</summary>
        public string? Message { get; init; }

        /// <summary>Older servers report an error as a bare {"error": "..."} frame.</summary>
        public string? Error { get; init; }
    }
}
