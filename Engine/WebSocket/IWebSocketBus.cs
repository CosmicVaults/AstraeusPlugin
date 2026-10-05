using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket {
    /// <summary>
    /// The WebSocket message bus. It handles serialization, signing, timestamps and dispatch, so
    /// callers only deal in typed messages.
    /// </summary>
    public interface IWebSocketBus {
        public event Action<WsCommand>? CommandReceived;

        /// <summary>The socket is open and the server has been told so.</summary>
        public event Action? Connected;

        /// <summary>
        /// The socket is gone, for any reason. Raised once per connection. Anything holding state about
        /// the far end, like who is watching, resets on this.
        /// </summary>
        public event Action? Disconnected;

        Task SendAsync(WsMessage wsMessage);

        /// <summary>Sends what was held while the socket was down.</summary>
        Task FlushPendingAsync();

        /// <summary>Sends a device connection update and the matching user notification.</summary>
        Task NotifyConnectionAsync(string device, string deviceId, bool isConnected, string deviceName);
    }
}
