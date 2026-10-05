using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using System.Collections.Generic;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record FilterListUpdate : WsMessage {
        public FilterListUpdate(IReadOnlyList<FilterListItem> filters)
            : base(type: "update", device: "filterwheel", context: "fetch", payload: ToJsonElement(new { filters })) { }
    }
}
