namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record NinaConnectionUpdate : WsMessage {
        public NinaConnectionUpdate(bool isConnected)
            : base(
                type: "update",
                device: "nina",
                context: "connection",
                payload: ToJsonElement(new { connected = isConnected, version = AstraeusVersion.Current })
            ) { }
    }
}