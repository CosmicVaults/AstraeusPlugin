namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record DeviceConnectionUpdate : WsMessage {
        public DeviceConnectionUpdate(string device, string deviceId, string deviceName, bool isConnected)
            : base(
                type: "update",
                device: device,
                context: "connection",
                payload: ToJsonElement(new { connected = isConnected, device_id = deviceId, device_name = deviceName })
            ) { }
    }
}