using System.Text.Json;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record WsCommand(
        string Id,
        string Device,
        string Action,
        JsonElement Payload,
        string? Context = null
    );
}