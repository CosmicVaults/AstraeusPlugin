using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket {

    /// <param name="IsDeliberate">True when StopAsync ended it, false for a close frame, a transport fault
    /// or an abort after a failed send.</param>
    public sealed record DisconnectInfo(
        bool IsDeliberate,
        WebSocketCloseStatus? CloseStatus,
        string? CloseDescription,
        TimeSpan ConnectionAge);

    /// <summary>
    /// Raw JSON transport that never reconnects. Observatory decides when to try again, through
    /// Disconnected. Logging here goes to N.I.N.A.'s log file only, since a line about the socket must
    /// never go down it.
    /// </summary>
    public class WebSocketClient {
        public event Action<string>? OnMessageReceived;

        /// <summary>
        /// Raised once per connection, whatever ended it, since presence counts and other far-end state reset
        /// on it. DisconnectInfo carries the why for the reconnect policy.
        /// </summary>
        public event Action<DisconnectInfo>? Disconnected;

        public WebSocketState State => _connection?.Socket.State ?? WebSocketState.None;

        /// <summary>
        /// The only way to spot a half-open connection here, since KeepAliveTimeout needs .NET 9 and a dead
        /// link leaves State at Open. The server sends a presence frame at least every 60 s so the silence
        /// can be timed.
        /// </summary>
        public DateTime LastInboundUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>Raised when the server refuses the handshake with 426, so this plugin version is no longer supported.</summary>
        public event Action? UpdateRequired;

        public const int CloseCodeUnauthenticated = 4001;
        public const int CloseCodeDisplaced = 4004;
        public const int CloseCodeUpdateRequired = 4026;

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LoopExitTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

        private const int ReceiveBufferBytes = 4096;

        // Server commands are small JSON documents. Anything this big isn't one, so we drop the
        // connection instead of buffering it without limit.
        private const int MaxInboundMessageBytes = 4 * 1024 * 1024;

        private readonly Authenticator _authenticator;
        private readonly string _baseUri;

        // Open and stop are serialised so they can never interleave on the same connection.
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private Connection? _connection;

        // Inbound messages queue here for the dispatch loop, so a slow handler never holds the receive
        // loop out of ReceiveAsync, which is where the pong to the server's ping goes. Bounded with
        // backpressure, so a stuck consumer stalls the loop instead of losing commands.
        private const int InboundCapacity = 512;
        private static readonly BoundedChannelOptions InboundOptions = new(InboundCapacity) {
            SingleReader = true,
            SingleWriter = true,
        };

        /// <summary>
        /// Everything that belongs to one connection, kept together so a receive loop only touches its
        /// own socket and token. A late loop from an old connection can't interfere with the current one.
        /// </summary>
        private sealed class Connection {
            public readonly ClientWebSocket Socket = new();
            public readonly CancellationTokenSource CancellationSource = new CancellationTokenSource();
            public readonly Channel<string> Inbound = Channel.CreateBounded<string>(InboundOptions);
            public Task ReceiveLoop = Task.CompletedTask;
            public Task DispatchLoop = Task.CompletedTask;
            public DateTime ConnectedAtUtc;
            public string? AccessToken;
            public WebSocketCloseStatus? CloseStatus;
            public string? CloseDescription;
            // Interlocked flag so the teardown, and with it Disconnected, runs once per connection.
            public int TornDown;
        }

        public WebSocketClient(Authenticator authenticator) {
            _authenticator = authenticator;
            _baseUri = ServerEndpoints.WebSocketBaseUri;
        }

        public async Task<bool> OpenConnectionAsync() {
            if (!_authenticator.IsAuthenticated) {
                LocalLog.Warning("Tried to open websocket connection while unauthenticated.");
                return false;
            }

            await _lifecycle.WaitAsync();
            try {
                if (_connection is { Socket.State: WebSocketState.Open }) return true;

                // Tear down what's left of the previous connection first and wait for its receive loop.
                // Presence counts reset on Disconnected, so a late loop would wipe the new connection's snapshot.
                if (_connection is { } previous) {
                    await TeardownAsync(previous, isDeliberate: true, shouldAwaitLoop: true);
                }

                string? accessToken = await _authenticator.GetAccessTokenAsync();
                if (accessToken == null) {
                    LocalLog.Warning("No valid access token; websocket connection skipped.");
                    return false;
                }

                Connection connection = new Connection { AccessToken = accessToken };
                ClientWebSocketOptions options = connection.Socket.Options;

                // Keeps NAT mappings alive over a quiet night, since the plugin can go minutes without
                // sending. It doesn't abort a half-open connection. LastInboundUtc handles that.
                options.KeepAliveInterval = KeepAliveInterval;

                // The server reads the bearer from the handshake headers and assigns the role from it.
                // Browsers can't set WebSocket headers, which is why the role isn't a query param. The
                // URI is logged, so it mustn't carry a credential, as N.I.N.A. logs get pasted into forums.
                options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
                options.SetRequestHeader("User-Agent", AstraeusVersion.UserAgent);
                // Keeps the status of a refused handshake, so a 426 can be told apart from a network fault.
                options.CollectHttpResponseDetails = true;

                LocalLog.Info($"Connecting to WS at {_baseUri}...");
                try {
                    // Time-limited, so a hung handshake doesn't hang the background loop with it.
                    using CancellationTokenSource connectTimeoutSource =
                        CancellationTokenSource.CreateLinkedTokenSource(connection.CancellationSource.Token);
                    connectTimeoutSource.CancelAfter(ConnectTimeout);
                    await connection.Socket.ConnectAsync(new Uri(_baseUri), connectTimeoutSource.Token);
                } catch (Exception ex) {
                    LocalLog.Warning($"WebSocket connect failed: {ex.Message}");
                    bool isUpdateRequired = connection.Socket.HttpStatusCode == System.Net.HttpStatusCode.UpgradeRequired;
                    connection.Socket.Dispose();
                    connection.CancellationSource.Dispose();
                    if (isUpdateRequired) UpdateRequired?.Invoke();
                    return false;
                }

                // Start the silence clock from the connection, or a slow first frame would look like a
                // stale socket.
                connection.ConnectedAtUtc = DateTime.UtcNow;
                LastInboundUtc = connection.ConnectedAtUtc;
                _connection = connection;
                connection.ReceiveLoop = Task.Run(() => ReceiveLoopAsync(connection));
                connection.DispatchLoop = Task.Run(() => DispatchLoopAsync(connection));
                return true;
            } finally {
                _lifecycle.Release();
            }
        }

        /// <summary>
        /// Receives messages for one connection until a close frame, a fault or cancellation, then tears
        /// it down. This is the only path by which anything but a deliberate stop raises Disconnected.
        /// </summary>
        private async Task ReceiveLoopAsync(Connection connection) {
            ClientWebSocket socket = connection.Socket;
            CancellationToken token = connection.CancellationSource.Token;
            byte[] buffer = new byte[ReceiveBufferBytes];
            using MemoryStream stream = new MemoryStream();
            try {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, token);

                    // Every frame counts, not just whole messages, since this is only a liveness signal.
                    LastInboundUtc = DateTime.UtcNow;

                    if (result.MessageType == WebSocketMessageType.Close) {
                        RecordCloseFrame(connection, result);
                        break;
                    }

                    stream.Write(buffer, 0, result.Count);
                    if (stream.Length > MaxInboundMessageBytes) {
                        int limitMegabytes = MaxInboundMessageBytes / (1024 * 1024);
                        LocalLog.Warning($"Server sent a websocket message over {limitMegabytes} MB; " +
                                         "dropping the connection.");
                        break;
                    }

                    if (result.EndOfMessage) {
                        string json = Encoding.UTF8.GetString(stream.ToArray());
                        stream.SetLength(0);
                        // Handed to the dispatch loop instead of handled here. See InboundCapacity.
                        await connection.Inbound.Writer.WriteAsync(json, token);
                    }
                }
            } catch (OperationCanceledException) {
                // expected on stop
            } catch (ObjectDisposedException) {
                // expected when the socket is disposed during shutdown
            } catch (Exception ex) {
                LocalLog.Warning($"WebSocket receive loop ended: {ex.Message}");
            } finally {
                // After a deliberate stop the teardown has already run and this returns at once. A close
                // frame, a fault or an abort after a failed send is torn down from here.
                await TeardownAsync(connection, isDeliberate: false, shouldAwaitLoop: false);
            }
        }

        private void RecordCloseFrame(Connection connection, WebSocketReceiveResult result) {
            connection.CloseStatus = result.CloseStatus;
            connection.CloseDescription = result.CloseStatusDescription;
            if ((int?)result.CloseStatus == CloseCodeUnauthenticated) {
                // The server rejected this socket's token. Forget it so the next attempt refreshes
                // instead of sending the same one again.
                _authenticator.InvalidateAccessToken(connection.AccessToken);
            }
            LocalLog.Warning($"Server closed the websocket ({(int?)result.CloseStatus}): " +
                             $"{result.CloseStatusDescription}");
        }

        /// <summary>
        /// Hands received messages to the handlers in arrival order. Ends when teardown completes the writer,
        /// after anything that arrived before the close is handled.
        /// </summary>
        private async Task DispatchLoopAsync(Connection connection) {
            ChannelReader<string> reader = connection.Inbound.Reader;
            try {
                while (await reader.WaitToReadAsync()) {
                    while (reader.TryRead(out string? json)) {
                        DispatchMessage(json);
                    }
                }
            } catch (Exception ex) {
                LocalLog.Warning($"WebSocket dispatch loop ended: {ex.Message}");
            }
        }

        private void DispatchMessage(string json) {
            try {
                OnMessageReceived?.Invoke(json);
            } catch (Exception ex) {
                // A throwing handler is a handler bug, not a reason to drop the connection.
                LocalLog.Error($"Unhandled exception handling a websocket message: {ex.Message}");
            }
        }

        /// <summary>
        /// False if the message didn't go, and the caller decides whether to hold it. A send that fails on
        /// a dead link aborts the socket, so Disconnected comes at once without waiting for the silence check.
        /// </summary>
        public async Task<bool> SendDataAsync(string json) {
            Connection? connection = _connection;
            if (connection == null || connection.Socket.State != WebSocketState.Open) {
                return false;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            try {
                await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                    endOfMessage: true, connection.CancellationSource.Token);
                return true;
            } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) {
                // Connection is stopping or stopped, so the message didn't go.
                return false;
            } catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException) {
                LocalLog.Warning($"WebSocket send failed: {ex.Message}");
                try {
                    connection.Socket.Abort();
                } catch (Exception) {
                    // Already gone.
                }
                return false;
            }
        }

        /// <summary>
        /// Idempotent. Raises Disconnected as deliberate if the connection hadn't already gone on its own.
        /// </summary>
        public async Task StopAsync() {
            await _lifecycle.WaitAsync();
            try {
                if (_connection is { } connection) {
                    await TeardownAsync(connection, isDeliberate: true, shouldAwaitLoop: true);
                }
            } finally {
                _lifecycle.Release();
            }
        }

        /// <summary>
        /// The only way a connection ends. Whoever gets here first, a deliberate stop or the receive
        /// loop, does the work and raises the event. The other just waits for the loop if asked to.
        /// </summary>
        private async Task TeardownAsync(Connection connection, bool isDeliberate, bool shouldAwaitLoop) {
            bool isFirst = Interlocked.Exchange(ref connection.TornDown, 1) == 0;

            if (isFirst && isDeliberate && connection.Socket.State == WebSocketState.Open) {
                // Close properly so the server runs its disconnect now, not after a TCP timeout.
                // CloseAsync would wait for the server's close frame, which never comes on a half-open
                // link. The receive loop picks up any reply on its own.
                try {
                    using CancellationTokenSource closeTimeout = new CancellationTokenSource(CloseTimeout);
                    await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stopping",
                        closeTimeout.Token);
                } catch (Exception) {
                    // Best-effort close. The socket is torn down either way.
                }
            }

            if (isFirst) {
                try {
                    connection.CancellationSource.Cancel();
                } catch (ObjectDisposedException) {
                }
                // The dispatch loop drains what arrived before the close, then exits on its own.
                connection.Inbound.Writer.TryComplete();
            }

            if (shouldAwaitLoop) {
                // Time-limited. The cancel above aborts a loop blocked in ReceiveAsync, but we don't
                // want to hang here if that's slow.
                try {
                    await Task.WhenAny(connection.ReceiveLoop, Task.Delay(LoopExitTimeout));
                } catch (Exception) {
                }
            }

            if (!isFirst) return;

            // Atomic, because the receive loop gets here outside the lifecycle lock, maybe after an
            // open has already installed the next connection.
            Interlocked.CompareExchange(ref _connection, null, connection);
            connection.Socket.Dispose();
            connection.CancellationSource.Dispose();

            TimeSpan age = DateTime.UtcNow - connection.ConnectedAtUtc;
            Disconnected?.Invoke(new DisconnectInfo(isDeliberate, connection.CloseStatus,
                connection.CloseDescription, age));
        }
    }
}
