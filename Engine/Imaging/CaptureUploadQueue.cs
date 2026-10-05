using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// Uploads captured frames one at a time on a background worker, so the mount doesn't wait on target
    /// with the roof open while a frame goes up. It's bounded because a slow uplink would grow a backlog
    /// that never catches up. Refused frames stay on this PC and the server re-offers the exposure.
    /// </summary>
    internal sealed class CaptureUploadQueue(Observatory observatory, IWebSocketBus webSocketBus)
        : ServiceComponent(observatory, webSocketBus) {

        private const int Capacity = 8;
        private const int MaxAttempts = 3;
        private static readonly TimeSpan RetryBackoffStep = TimeSpan.FromSeconds(5);

        private readonly Queue<QueuedUpload> _queue = new();
        private readonly object _gate = new();

        // Wakes the worker. Released once per frame accepted and waited once per frame taken, so its
        // count is the queue depth. There's only one waiter, which is what a semaphore suits. Never
        // disposed, because WaitAsync doesn't allocate a wait handle and the field outlives every
        // Start/Stop pair.
        private readonly SemaphoreSlim _work = new(0);

        private CancellationTokenSource? _loopCancellationSource;
        private Task? _loop;

        // Whether the queue is refusing frames, so only the changes get logged. Read and written
        // under _gate.
        private bool _isBackedUp;

        public override string DeviceType => "uploads";
        public override LogCategory DefaultLogCategory => LogCategory.Network;

        // Always false. Observatory.LateUpdate ORs IsBusy across components to pick the tick rate, and
        // a background upload is no reason to speed up the dashboard.
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

        /// <summary>
        /// Runs during the plugin's Teardown, so a transfer in flight is cancelled on the spot. N.I.N.A. is
        /// waiting, and the frame is still on disk anyway.
        /// </summary>
        public async Task StopAsync() {
            _loopCancellationSource?.Cancel();
            if (_loop != null) {
                try { await _loop; } catch (OperationCanceledException) { /* expected on shutdown */ }
            }
            _loop = null;
            _loopCancellationSource?.Dispose();
            _loopCancellationSource = null;
        }

        /// <summary>
        /// False means the queue is full and the frame stays on this PC, which the caller needn't log. The
        /// caller builds the key because PollForTarget may have cleared the target name by this frame's turn.
        /// Never throws.
        /// </summary>
        public bool TryEnqueue(PendingUpload upload, string r2Key, string logDevice, LogCategory logCategory) {
            bool isAccepted;
            bool isFirstRefusal = false;
            lock (_gate) {
                isAccepted = _queue.Count < Capacity;
                if (isAccepted) {
                    _queue.Enqueue(new QueuedUpload(upload, r2Key, logDevice, logCategory));
                } else {
                    isFirstRefusal = !_isBackedUp;
                    _isBackedUp = true;
                }
            }

            if (isAccepted) {
                _work.Release();
                return true;
            }

            if (isFirstRefusal)
                LogWarning($"Cloud upload is {Capacity} frames behind; new frames stay on this machine until " +
                           "it catches up.", logDevice, logCategory);
            return false;
        }

        private async Task RunAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                try {
                    await _work.WaitAsync(cancellationToken);
                } catch (OperationCanceledException) {
                    return;
                }

                QueuedUpload item;
                lock (_gate) {
                    // The semaphore counts the queue, so there's always something here. The guard is
                    // just for the shutdown race.
                    if (_queue.Count == 0) continue;
                    item = _queue.Dequeue();
                }

                try {
                    await UploadWithRetriesAsync(item, cancellationToken);
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    // CaptureUploader only throws on cancellation, so this is unexpected. Log it and
                    // move on, since one bad frame mustn't take the worker down and strand the
                    // frames queued behind it.
                    LogWarning($"Upload of {Path.GetFileName(item.Upload.FilePath)} failed unexpectedly: {ex.Message}",
                        item.LogDevice, item.LogCategory);
                }

                ReportCaughtUp(item);
            }
        }

        /// <summary>
        /// Gives a failed frame a couple more tries, so a network blip doesn't cost telescope time. It retries
        /// in place, since requeueing would break the semaphore count and frame order. Once the bytes are in
        /// R2 only the confirm is retried.
        /// </summary>
        private async Task UploadWithRetriesAsync(QueuedUpload item, CancellationToken cancellationToken) {
            CaptureUploader uploader = new CaptureUploader(Observatory, item.LogDevice, item.LogCategory);
            string frameName = Path.GetFileName(item.Upload.FilePath);
            UploadOutcome outcome = UploadOutcome.Failed;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++) {
                if (outcome == UploadOutcome.ConfirmFailed)
                    outcome = await uploader.RetryConfirmAsync(cancellationToken);
                else
                    outcome = await uploader.UploadAsync(item.Upload, item.R2Key, cancellationToken);
                if (outcome == UploadOutcome.Confirmed) return;
                // A full account gives the same answer however many times it is asked, and
                // CaptureUploader has already said so in the log.
                if (outcome == UploadOutcome.StorageFull) return;

                if (attempt == MaxAttempts) break;
                string failedStep = outcome == UploadOutcome.ConfirmFailed
                    ? "is uploaded but not yet in your image library"
                    : "did not upload";
                LogWarning($"{frameName} {failedStep} (attempt {attempt} of {MaxAttempts}); retrying.",
                    item.LogDevice, item.LogCategory);
                await Task.Delay(RetryBackoffStep * attempt, cancellationToken);
            }

            if (outcome == UploadOutcome.ConfirmFailed) {
                LogError($"{frameName} is in cloud storage, but the server never confirmed it: the server " +
                         "will fail this capture once its upload timeout passes.",
                    item.LogDevice, item.LogCategory);
                return;
            }
            LogWarning($"{frameName} did not reach your image library after {MaxAttempts} attempts; the frame " +
                       "stays on this machine.", item.LogDevice, item.LogCategory);
        }

        private void ReportCaughtUp(QueuedUpload item) {
            bool isRecovered;
            lock (_gate) {
                isRecovered = _isBackedUp && _queue.Count == 0;
                if (isRecovered) _isBackedUp = false;
            }
            if (isRecovered)
                Log("Cloud upload has caught up; frames are going up as they are taken again.",
                    item.LogDevice, item.LogCategory);
        }

        public override WsMessage? GetUpdateMessage() => null;
        protected override Task HandleCommandAsync(WsCommand command) => Task.CompletedTask;

        /// <summary>
        /// Carries the caller's log device and category, so a warning about an Autopilot frame still shows
        /// as the Autopilot's.
        /// </summary>
        private sealed record QueuedUpload(
            PendingUpload Upload, string R2Key, string LogDevice, LogCategory LogCategory);
    }
}
