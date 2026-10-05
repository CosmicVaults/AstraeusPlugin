using System.Collections.Generic;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record DashboardUpdate : WsMessage {
        public DashboardUpdate(IReadOnlyDictionary<string, object> deviceData) : base(
            type: "update",
            device: "*",
            context: "dashboard",
            payload: ToJsonElement(deviceData)
        ) { }
    };
}