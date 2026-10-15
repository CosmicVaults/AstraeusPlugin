using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>
    /// The bus with no socket behind it. Everything sent is kept for the test to read, and the test raises
    /// inbound commands and connection events itself.
    /// </summary>
    internal sealed class FakeWebSocketBus : IWebSocketBus {
        public event Action<WsCommand>? CommandReceived;
        public event Action? Connected;
        public event Action? Disconnected;

        public List<WsMessage> Sent { get; } = new List<WsMessage>();
        public List<(string Device, string DeviceId, bool IsConnected, string DeviceName)> ConnectionNotices { get; } = new();
        public int FlushCount { get; private set; }

        public Task SendAsync(WsMessage wsMessage) {
            Sent.Add(wsMessage);
            return Task.CompletedTask;
        }

        public Task FlushPendingAsync() {
            FlushCount++;
            return Task.CompletedTask;
        }

        public Task NotifyConnectionAsync(string device, string deviceId, bool isConnected, string deviceName) {
            ConnectionNotices.Add((device, deviceId, isConnected, deviceName));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Hands a command to every component listening, as the socket would. Components handle commands on
        /// an async void path, so a test waits for the effect rather than assuming the handler has finished.
        /// </summary>
        public void Receive(WsCommand command) => CommandReceived?.Invoke(command);

        public void RaiseConnected() => Connected?.Invoke();

        public void RaiseDisconnected() => Disconnected?.Invoke();

        /// <summary>
        /// The most recent message of that type, or null when none was sent.
        /// </summary>
        public T? LastSent<T>() where T : WsMessage => Sent.OfType<T>().LastOrDefault();
    }
}