using System.Collections.Generic;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record DeviceListItem(
        string id,
        string displayName
    );
    
    public sealed record DeviceListUpdate : WsMessage {
        public DeviceListUpdate(
            string device,
            IReadOnlyList<DeviceListItem> devices,
            string? selectedDeviceId
        ) : base(
            type: "update",
            device: device,
            context: "deviceList",
            payload: ToJsonElement(new {
                devices,
                selectedDevice = selectedDeviceId
            })
        ) { }
    }
}