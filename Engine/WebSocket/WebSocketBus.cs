using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket {
    /// <summary>
    /// Transport reports go only to N.I.N.A.'s log file, through LocalLog. Anything written through
    /// Observatory.Log goes down the socket, so the server's ack would cost a log row per connect and a
    /// rate-limit reply would trigger a notification that's itself rate-limited.
    /// </summary>
    public sealed class WebSocketBus : IWebSocketBus {
        public event Action<WsCommand>? CommandReceived;
        public event Action? Connected;
        public event Action? Disconnected;

        private readonly WebSocketClient _client;

        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // Held while the socket is down and sent in order once it's back. Capped in count and age,
        // since a reconnect gap is seconds and a minute-old device event is noise by then.
        private const int PendingCapacity = 200;
        private static readonly TimeSpan PendingMaxAge = TimeSpan.FromSeconds(60);
        private readonly Queue<(DateTime QueuedAtUtc, string Json)> _pending = new();
        private readonly object _pendingLock = new();
        private DateTime? _pendingSinceUtc;

        private static readonly JsonSerializerOptions JsonOptions = new() {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        // Shared because System.Text.Json caches type metadata on the options instance, and the
        // receive path runs once per frame.
        private static readonly JsonSerializerOptions InboundJsonOptions = new() {
            PropertyNameCaseInsensitive = true
        };

        public WebSocketBus(WebSocketClient client) {
            _client = client;
            _client.OnMessageReceived += HandleMessage;
            _client.Disconnected += _ => Disconnected?.Invoke();
        }

        /// <summary>
        /// With the socket down, one-off messages are held for FlushPendingAsync and telemetry is dropped,
        /// since the next tick replaces it.
        /// </summary>
        public async Task SendAsync(WsMessage wsMessage) {
            string messageJson = JsonSerializer.Serialize(wsMessage, JsonOptions);
            if (_client.State != WebSocketState.Open) {
                HoldForReplay(wsMessage, messageJson);
                return;
            }
            bool wasSent;
            await _sendLock.WaitAsync();
            try {
                wasSent = await _client.SendDataAsync(messageJson);
            } finally {
                _sendLock.Release();
            }
            // The socket can drop between the state check and the send. A message worth replaying is
            // then held as if it had been down all along.
            if (!wasSent) HoldForReplay(wsMessage, messageJson);
        }

        // One-off messages are worth sending late, like notifications, command replies, device events
        // and connections. Telemetry ticks, feed frames and previews get re-sent or replaced anyway.
        private static bool IsWorthReplaying(WsMessage wsMessage) {
            if (wsMessage is FeedFrameMessage or ImagePreviewMessage or DashboardUpdate or NinaConnectionUpdate)
                return false;
            return wsMessage.Type != "update" || wsMessage.Context is not (null or "dashboard");
        }

        private void HoldForReplay(WsMessage wsMessage, string messageJson) {
            if (!IsWorthReplaying(wsMessage)) return;
            lock (_pendingLock) {
                _pendingSinceUtc ??= DateTime.UtcNow;
                while (_pending.Count >= PendingCapacity) _pending.Dequeue();
                _pending.Enqueue((DateTime.UtcNow, messageJson));
            }
        }

        /// <summary>
        /// Sends what was held while the socket was down, oldest first. Called once the server has been told
        /// the socket is back, so the replay lands on a connection it already counts as live.
        /// </summary>
        public async Task FlushPendingAsync() {
            List<(DateTime QueuedAtUtc, string Json)> replay;
            DateTime gapStartUtc;
            TimeSpan gap;
            lock (_pendingLock) {
                if (_pending.Count == 0) return;
                DateTime now = DateTime.UtcNow;
                gapStartUtc = _pendingSinceUtc ?? now;
                gap = now - gapStartUtc;
                replay = _pending.Where(entry => now - entry.QueuedAtUtc <= PendingMaxAge).ToList();
                _pending.Clear();
                _pendingSinceUtc = null;
            }
            int sentCount = 0;
            await _sendLock.WaitAsync();
            try {
                foreach ((DateTime QueuedAtUtc, string Json) entry in replay) {
                    if (!await _client.SendDataAsync(entry.Json)) break;
                    sentCount++;
                }
            } finally {
                _sendLock.Release();
            }
            if (sentCount < replay.Count) {
                HoldAheadOfPending(replay.GetRange(sentCount, replay.Count - sentCount), gapStartUtc);
                LocalLog.Info($"Replayed {sentCount} of {replay.Count} held message(s) before the websocket " +
                              "dropped again; the rest stay held.");
                return;
            }
            LocalLog.Info($"Replayed {replay.Count} message(s) held during a {gap.TotalSeconds:F0} s websocket gap.");
        }

        /// <summary>
        /// Puts messages a replay couldn't send back at the front of the queue, in order. Anything
        /// held since the replay began is newer, so it stays behind them.
        /// </summary>
        private void HoldAheadOfPending(List<(DateTime QueuedAtUtc, string Json)> unsent, DateTime gapStartUtc) {
            lock (_pendingLock) {
                List<(DateTime QueuedAtUtc, string Json)> newer = _pending.ToList();
                _pending.Clear();
                foreach ((DateTime QueuedAtUtc, string Json) entry in unsent) _pending.Enqueue(entry);
                foreach ((DateTime QueuedAtUtc, string Json) entry in newer) _pending.Enqueue(entry);
                while (_pending.Count > PendingCapacity) _pending.Dequeue();
                _pendingSinceUtc = gapStartUtc;
            }
        }

        public void NotifyConnected() => Connected?.Invoke();

        public async Task NotifyConnectionAsync(string device, string deviceId, bool isConnected, string deviceName) {
            await SendAsync(new DeviceConnectionUpdate(device, deviceId, deviceName, isConnected));
            await SendAsync(new WsNotification(
                device:   device,
                category: "connection",
                message:  isConnected ? $"{deviceName} is Connected." : $"{deviceName} has Disconnected",
                isToast:  true
            ));
        }

        private void HandleMessage(string json) {
            InboundMessage? message;
            try {
                message = JsonSerializer.Deserialize<InboundMessage>(json, InboundJsonOptions);
            } catch (JsonException) {
                LocalLog.Warning("Websocket: invalid JSON received.");
                return;
            }
            if (message == null) return;

            // The server's own frames, none of which is a command.
            if (message.Type == "error" || message.Error != null) {
                LocalLog.Warning($"Websocket: server reported an error: {message.Message ?? message.Error}");
                return;
            }
            if (string.IsNullOrWhiteSpace(message.Type) && message.Role != null) {
                LocalLog.Debug($"Websocket: {message.Message}");
                return;
            }
            if (message.Type == "notification") {
                // Sent before the server closes a displaced connection, so the reason is on record.
                LocalLog.Warning($"Websocket: server notice ({message.Context}): {DescribeReason(message.Payload)}");
                return;
            }

            if (string.IsNullOrWhiteSpace(message.Type) || string.IsNullOrWhiteSpace(message.Device)) {
                LocalLog.Warning("Websocket: message missing required fields.");
                return;
            }

            if (message.Type != "dispatch_nina_command") {
                LocalLog.Warning($"Websocket: unexpected inbound message type '{message.Type}'.");
                return;
            }

            // A dashboard connection check gets its reply here and never reaches CommandReceived.
            if (message.Device == "connection" && message.Context == "status") {
                var payload = new { connected = true };
                WsResponse response = new WsResponse("connection", message.Id, "received", payload);
                SendAsync(response).ObserveFaults(exception =>
                    LocalLog.Warning($"Websocket: connection status reply failed: {exception.Message}"));
                return;
            }

            if (message.Payload is null
                || JsonFields.ReadString(message.Payload.Value, "action", out string action) != JsonFieldState.Valid
                || action.Length == 0) {
                LocalLog.Warning("Websocket: command missing action.");
                return;
            }

            WsCommand command = new WsCommand(
                Id: message.Id,
                Device: message.Device,
                Action: action,
                Payload: message.Payload.Value,
                Context: message.Context
            );
            RaiseCommandReceived(command);
        }

        /// <summary>Hands a command to each subscriber in turn, so one that throws can't starve the rest.</summary>
        private void RaiseCommandReceived(WsCommand command) {
            Action<WsCommand>? handlers = CommandReceived;
            if (handlers == null) return;
            foreach (Delegate subscriber in handlers.GetInvocationList()) {
                try {
                    ((Action<WsCommand>)subscriber)(command);
                } catch (Exception ex) {
                    LocalLog.Warning($"Websocket: a handler for '{command.Device}' / '{command.Action}' " +
                                     $"threw: {ex.Message}");
                }
            }
        }

        private static string DescribeReason(JsonElement? payload) {
            if (payload is not { } element) return string.Empty;
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("reason", out JsonElement reason) &&
                reason.ValueKind == JsonValueKind.String)
                return reason.GetString() ?? string.Empty;
            return element.ToString();
        }
    }
}
