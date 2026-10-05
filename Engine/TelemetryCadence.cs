using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    /// <summary>
    /// Who the server says is watching, and the one wake-up shared by everything that waits on it. The
    /// counts are zero while the socket is closed, so a dropped connection doesn't leave the plugin
    /// capturing for viewers it has lost.
    /// </summary>
    public sealed class TelemetryCadence {
        /// <summary>
        /// Tick while something is moving (a slew, focus move, rotation or filter change) so the screen
        /// keeps up with the hardware. Exposing doesn't count, since the dashboard counts exposures down
        /// itself and the camera exposes for most of the night.
        /// </summary>
        private static readonly TimeSpan BusyTick = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The longest the loop sleeps with nobody watching. Matching the server's 60 s keepalive means a
        /// half-open connection that still reports Open is caught by Observatory's silence check within
        /// about three missed frames.
        /// </summary>
        public static readonly TimeSpan IdleFallback = TimeSpan.FromSeconds(60);

        // Guards against a bad server value wedging or flooding the loop.
        private static readonly TimeSpan MinTick = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan MaxTick = TimeSpan.FromSeconds(60);

        // Written on the socket's receive thread, read on the background loop and the feed loop.
        private int _subscribers;
        private int _feedSubscribers;
        private long _serverTickTicks = DefaultTick.Ticks;

        // The wake-up. Swapped for a fresh one on every Signal(), so every waiter on the old one is
        // released and none can miss a change between capturing it and awaiting it. A SemaphoreSlim
        // release wakes only one waiter, so the feed and the background loop would steal each other's.
        private TaskCompletionSource _changed = NewChangeSource();

        // Presence isn't logged. Who is watching and when is about the user, not the observatory,
        // and logs get pasted into forum posts.
        public TelemetryCadence(IWebSocketBus bus) {
            bus.CommandReceived += OnCommandReceived;
            bus.Connected += OnConnected;
            bus.Disconnected += OnDisconnected;
        }

        public bool IsBusy { get; set; }

        /// <summary>Tabs somebody can see, on any page. Zero while the socket is closed.</summary>
        public int Subscribers => Volatile.Read(ref _subscribers);

        /// <summary>Of those, the tabs showing the webcam card. Zero while the socket is closed.</summary>
        public int FeedSubscribers => Volatile.Read(ref _feedSubscribers);

        public bool HasSubscribers => Subscribers > 0;

        /// <summary>
        /// The feed only captures while this is true, since frames nobody sees cost the uplink and a
        /// camera connection slot for nothing.
        /// </summary>
        public bool HasFeedSubscribers => FeedSubscribers > 0;

        /// <summary>
        /// The gap between frames while somebody is watching. The server's tickRateMs is a ceiling we may go
        /// slower than, and the busy tick is the one agreed exception.
        /// </summary>
        public TimeSpan CurrentInterval {
            get {
                TimeSpan serverTick = TimeSpan.FromTicks(Volatile.Read(ref _serverTickTicks));
                return IsBusy && BusyTick < serverTick ? BusyTick : serverTick;
            }
        }

        /// <summary>
        /// Periodic telemetry only goes to somebody who can see it. Event-driven traffic like
        /// notifications and previews doesn't come through here and carries on regardless.
        /// </summary>
        public bool ShouldPublish => HasSubscribers;

        /// <summary>
        /// Completes on the next Signal. Capture it before reading the state you mean to wait on, so a
        /// change landing in between still ends the wait.
        /// </summary>
        public Task Changed => Volatile.Read(ref _changed).Task;

        public void Signal() => Interlocked.Exchange(ref _changed, NewChangeSource()).TrySetResult();

        /// <summary>
        /// Waits for until to hold, checking it again on every signal, or for the timeout to run out.
        /// Returns what until said last.
        /// </summary>
        public async Task<bool> WaitUntilAsync(Func<bool> until, TimeSpan timeout,
            CancellationToken cancellationToken) {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true) {
                Task changed = Changed;
                if (until()) return true;
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                if (!await WaitForChangeAsync(changed, remaining, cancellationToken)) return until();
            }
        }

        /// <summary>
        /// The delay is worked out after the signal is captured, so its inputs can't change unnoticed in between.
        /// </summary>
        public async Task WaitAsync(Func<TimeSpan> timeout, CancellationToken cancellationToken) {
            Task changed = Changed;
            TimeSpan delay = timeout();
            if (delay <= TimeSpan.Zero) return;
            await WaitForChangeAsync(changed, delay, cancellationToken);
        }

        /// <summary>True when the change came first, false when the delay ran out. Throws on cancellation.</summary>
        private static async Task<bool> WaitForChangeAsync(Task changed, TimeSpan delay,
            CancellationToken cancellationToken) {
            using CancellationTokenSource delayCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delayTask = Task.Delay(delay, delayCancellationSource.Token);
            Task winner = await Task.WhenAny(changed, delayTask);
            if (winner == delayTask) {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            delayCancellationSource.Cancel(); // release the timer
            return true;
        }

        private static TaskCompletionSource NewChangeSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Counts are left alone. The server's connect-time snapshot fills them in within a round
        // trip, and they were already reset on the previous Disconnected.
        private void OnConnected() => Signal();

        private void OnDisconnected() {
            Volatile.Write(ref _subscribers, 0);
            Volatile.Write(ref _feedSubscribers, 0);
            Volatile.Write(ref _serverTickTicks, DefaultTick.Ticks);
            Signal();
        }

        private void OnCommandReceived(WsCommand command) {
            if (!command.Device.Equals("system", StringComparison.OrdinalIgnoreCase)) return;
            if (!command.Action.Equals("presence", StringComparison.OrdinalIgnoreCase)) return;

            int subscribers = ReadCount(command.Payload, "subscribers", Subscribers);
            // An older server sends only one count, so everyone is treated as a webcam viewer.
            int feedSubscribers = ReadCount(command.Payload, "feedSubscribers", subscribers);

            TimeSpan tick = TimeSpan.FromTicks(Volatile.Read(ref _serverTickTicks));
            if (command.Payload.TryGetProperty("tickRateMs", out JsonElement tickRate) &&
                tickRate.ValueKind == JsonValueKind.Number && tickRate.TryGetInt32(out int milliseconds))
                tick = Clamp(TimeSpan.FromMilliseconds(milliseconds));

            Volatile.Write(ref _subscribers, subscribers);
            Volatile.Write(ref _feedSubscribers, feedSubscribers);
            Volatile.Write(ref _serverTickTicks, tick.Ticks);

            // Always signal. A keepalive with the same counts costs one loop pass that publishes nothing.
            Signal();
        }

        private static int ReadCount(JsonElement payload, string name, int fallback) {
            if (payload.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int count))
                return Math.Max(0, count);
            return fallback;
        }

        private static TimeSpan Clamp(TimeSpan value) {
            if (value < MinTick) return MinTick;
            if (value > MaxTick) return MaxTick;
            return value;
        }
    }
}
