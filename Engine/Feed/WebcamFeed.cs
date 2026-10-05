using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// Sends webcam frames to the dashboard.
    ///
    /// Runs its own loop instead of using the component Update() tick. Observatory.Update awaits each
    /// component in turn, and a capture can take 15-20 s, so an unreachable camera would freeze
    /// mount, focuser and weather updates on every attempt.
    ///
    /// Only captures while someone has the webcam card open on a dashboard tab
    /// (TelemetryCadence.FeedSubscribers). The control hub and settings page have no card. The server
    /// reports the exact count, so capture stops as soon as the last card is hidden, and a closed
    /// socket counts as everyone leaving.
    ///
    /// The camera connection stays open for SourceLinger after the last viewer leaves. Rebuilding an
    /// RTSP player costs a handshake and a keyframe wait, which an alt-tab or socket blip isn't worth.
    /// After the linger the source is torn down, freeing the camera's connection slot and decode thread.
    /// </summary>
    public sealed class WebcamFeed(Observatory observatory, IWebSocketBus webSocketBus)
        : ServiceComponent(observatory, webSocketBus) {

        private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SourceLinger = TimeSpan.FromSeconds(45);
        private const int MaxConsecutiveFailures = 5;

        /// <summary>
        /// Longest wait before the gate is re-checked without a signal. Everything that changes the gate
        /// signals the cadence, so this only covers a missed signal.
        /// </summary>
        private static readonly TimeSpan IdleFallback = TimeSpan.FromSeconds(30);

        private const string NoPasswordFingerprint = "nopw";

        private CancellationTokenSource? _loopCancellationSource;
        private Task? _loop;
        private IFeedSource? _source;

        // The settings the live source was built from. A change to any of them has to rebuild it,
        // and comparing is cheaper and more reliable than trying to catch every setter.
        private string? _sourceSignature;

        private TimeSpan _backoff = InitialBackoff;
        private string? _lastError;
        private int _consecutiveFailures;

        // Why there's no source, when there isn't one. _lastError is for a capture that was tried and
        // failed.
        private string? _unavailableReason;

        // When the last viewer left, for the linger. Null while somebody is watching.
        private DateTime? _idleSince;

        // Whether the loop is capturing, so a new viewer gets a fresh backoff and error state.
        private bool _isCapturing;

        // 1 while a snapshotNow is outstanding. An int so the loop can consume it with one
        // Interlocked.Exchange. It's written on the bus thread and read on the loop thread, and a
        // separate test-then-clear would drop a request that landed in between.
        private int _snapshotRequested;

        public override string DeviceType => "feed";
        public override LogCategory DefaultLogCategory => LogCategory.Equipment;

        // Always false. Observatory.LateUpdate ORs IsBusy across components to pick the global tick
        // rate, and a webcam shouldn't drag the whole dashboard to the fast cadence.
        public override bool IsBusy => false;

        public override async Task Start() {
            await base.Start();
            _loopCancellationSource = new CancellationTokenSource();
            CancellationToken token = _loopCancellationSource.Token;
            _loop = Task.Run(() => RunAsync(token), token);
        }

        public override async Task Destroy() {
            await StopAsync();
            await base.Destroy();
        }

        public async Task StopAsync() {
            _loopCancellationSource?.Cancel();
            if (_loop != null) {
                try { await _loop; } catch (OperationCanceledException) { /* expected on shutdown */ }
            }
            _loop = null;
            _loopCancellationSource?.Dispose();
            _loopCancellationSource = null;
            await DisposeSourceAsync();
        }

        public override WsMessage? GetUpdateMessage() => null;

        /// <summary>
        /// The gate. The feed is on, the socket is open and someone is watching the card. Each of these
        /// signals the cadence when it changes, so the loop can sleep on it instead of polling.
        /// </summary>
        private bool CanCapture =>
            Observatory.Settings.IsFeedEnabled() &&
            Observatory.IsSocketOpen &&
            Observatory.Cadence.HasFeedSubscribers;

        protected override async Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "snapshotNow":
                    // The dashboard asks on tab focus and on connect, so this stays cheap. We set a flag
                    // for the loop and reply. Capturing here would let a burst open several
                    // connections to the camera at once.
                    if (!Observatory.Settings.IsFeedEnabled()) {
                        await BroadcastMessageReceivedAsync(command.Id, false, "The feed is not enabled.");
                        break;
                    }
                    if (!Observatory.Cadence.HasFeedSubscribers) {
                        await BroadcastMessageReceivedAsync(command.Id, false, "Nobody is watching the webcam.");
                        break;
                    }
                    Interlocked.Exchange(ref _snapshotRequested, 1);
                    // The loop may be part-way through an interval, and this cuts it short.
                    Observatory.Cadence.Signal();
                    await BroadcastMessageReceivedAsync(command.Id, true);
                    break;
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                try {
                    await TickAsync(cancellationToken);
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    // The loop survives any single failure. An unreachable camera must never stop the
                    // feed for good or disturb an imaging session.
                    LogError($"Feed loop error: {ex.Message}");
                    await Task.Delay(MaxBackoff, cancellationToken);
                }
            }
        }

        private async Task TickAsync(CancellationToken cancellationToken) {
            if (!CanCapture) {
                // Drop any outstanding request, or a viewer arriving later would be served a snapshot
                // asked for long ago.
                Interlocked.Exchange(ref _snapshotRequested, 0);
                _idleSince ??= DateTime.UtcNow;
                _isCapturing = false;

                // A disabled feed releases the camera at once. Otherwise the source is kept until the
                // linger runs out. Capture has stopped either way.
                TimeSpan lingerLeft = _idleSince.Value + SourceLinger - DateTime.UtcNow;
                if (_source != null && (!Observatory.Settings.IsFeedEnabled() || lingerLeft <= TimeSpan.Zero))
                    await DisposeSourceAsync();

                TimeSpan wait = _source != null && lingerLeft > TimeSpan.Zero && lingerLeft < IdleFallback
                    ? lingerLeft
                    : IdleFallback;
                await Observatory.Cadence.WaitUntilAsync(() => CanCapture, wait, cancellationToken);
                return;
            }

            if (!_isCapturing) {
                // A viewer has arrived. Earlier failures don't carry over, so the first frame is tried
                // at once without an inherited backoff.
                _isCapturing = true;
                _idleSince = null;
                _backoff = InitialBackoff;
                _lastError = null;
                _consecutiveFailures = 0;
            }

            // Cleared whether or not one was pending, since this pass serves it.
            Interlocked.Exchange(ref _snapshotRequested, 0);

            AstraeusSettings settings = Observatory.Settings;
            FeedFrame? frame = await CaptureAsync(settings, cancellationToken);
            if (frame is { } captured) {
                await WebSocketBus.SendAsync(new FeedFrameMessage(
                    FeedFramePayload.FromFrame(captured.Jpeg, captured.Width, captured.Height, captured.CapturedUtc)));
                _backoff = InitialBackoff;
                await WaitWhileWatchedAsync(TimeSpan.FromSeconds(settings.GetFeedIntervalSeconds()), cancellationToken);
                return;
            }

            // Tell the dashboard why the panel is empty so it doesn't quietly go stale. Then back off
            // so a camera that's off for the night isn't hammered every interval.
            await WebSocketBus.SendAsync(new FeedFrameMessage(
                FeedFramePayload.Failure(_lastError ?? "Could not read a frame")));
            await WaitWhileWatchedAsync(_backoff, cancellationToken);
            _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        private Task WaitWhileWatchedAsync(TimeSpan total, CancellationToken cancellationToken) =>
            Observatory.Cadence.WaitUntilAsync(
                () => !CanCapture || Volatile.Read(ref _snapshotRequested) == 1, total, cancellationToken);

        private async Task<FeedFrame?> CaptureAsync(AstraeusSettings settings, CancellationToken cancellationToken) {
            IFeedSource? source = await EnsureSourceAsync(settings);
            if (source == null) {
                _lastError = _unavailableReason ?? "The feed is not configured.";
                return null;
            }

            try {
                SourceFrame? raw = await source.CaptureAsync(cancellationToken);
                if (raw is not { } frame) {
                    _lastError = "The camera returned no frame.";
                    await CountFailureAsync();
                    return null;
                }

                // Encoding is CPU work on the imaging PC, so keep it off the loop's thread. Settings
                // are read per frame so max width and quality changes apply on the next capture.
                FeedFrame prepared = await Task.Run(() => FeedFrameEncoder.Prepare(
                    frame, settings.GetFeedMaxWidth(), settings.GetFeedJpegQuality()), cancellationToken);
                _lastError = null;
                _consecutiveFailures = 0;
                return prepared;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                // Just the reason, with the host scrubbed out. It reaches the dashboard, and there's
                // only one feed, so naming the camera would only add its address.
                _lastError = FeedUrl.RedactCameraDetails(ex.Message, settings.GetFeedUrl());
                LogWarning($"Feed capture failed: {_lastError}");
                await CountFailureAsync();
                return null;
            }
        }

        /// <summary>
        /// Rebuilds the source only after several failures in a row. An RTSP source rebuilds its own player
        /// when the stream stops, so tearing it down on one timeout would only force a slow reconnect.
        /// </summary>
        private async Task CountFailureAsync() {
            if (++_consecutiveFailures < MaxConsecutiveFailures) return;
            LogWarning($"Feed has failed {_consecutiveFailures} times in a row, so rebuilding the source.");
            _consecutiveFailures = 0;
            await DisposeSourceAsync();
        }

        private async Task<IFeedSource?> EnsureSourceAsync(AstraeusSettings settings) {
            string signature = BuildSourceSignature(settings);
            if (_source != null && signature == _sourceSignature) return _source;

            await DisposeSourceAsync();
            (_source, _unavailableReason) = FeedSourceFactory.Create(settings);
            _sourceSignature = signature;
            return _source;
        }

        /// <summary>
        /// Holds a hash of the password, never the password, so a changed password rebuilds the source
        /// without keeping another copy of the secret. Max width is included because the RTSP decoder
        /// fixes the frame size when it connects. Quality is left out since Prepare reads it every frame.
        /// </summary>
        private static string BuildSourceSignature(AstraeusSettings settings) =>
            string.Join("|",
                settings.GetFeedSourceKind().ToString(),
                settings.GetFeedUrl(),
                settings.GetFeedUsername(),
                PasswordFingerprint(settings.GetFeedPassword()),
                settings.GetFeedMaxWidth().ToString());

        private static string PasswordFingerprint(string? password) {
            if (string.IsNullOrEmpty(password)) return NoPasswordFingerprint;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(hash);
        }

        private async Task DisposeSourceAsync() {
            if (_source == null) return;
            try {
                await _source.DisposeAsync();
            } catch (Exception ex) {
                LogWarning($"Feed source did not shut down cleanly: {ex.Message}");
            }
            _source = null;
            _sourceSignature = null;
        }
    }
}
