namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
        
    /// <summary>Tells the server a command has been received.</summary>
    public sealed record WsResponse : WsMessage {
        public WsResponse(
            string device,
            string? correlationId,
            string context,
            object? payload = null
        ) : base(
            type: "response",
            device: device,
            context: context,
            payload: payload is null ? null : ToJsonElement(payload),
            id: correlationId
        ) { }
    }
}