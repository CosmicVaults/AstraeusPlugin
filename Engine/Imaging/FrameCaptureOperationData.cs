using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// In-flight capture state for one frame, from its request to the save the caller awaits. Not
    /// thread-safe, since the camera runs one operation at a time and handles the swapping.
    /// </summary>
    internal sealed class FrameCaptureOperationData(
        CaptureRequest request,
        CancellationToken componentToken,
        CancellationToken callerToken)
        : IDisposable {
        private readonly CancellationTokenSource _cancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(componentToken, callerToken);
        private readonly TaskCompletionSource<ImageSavedEventArgs?> _saved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FrameWcs? _pendingWcs;

        public CaptureRequest Request { get; } = request;
        public CancellationToken Token => _cancellationSource.Token;
        public PlateSolveResult? SolveResult { get; private set; }
        public bool WasSolveAttempted { get; private set; }

        /// <summary>
        /// The frame's own headers, so the server gets the settings it was really taken with to match it
        /// to a master.
        /// </summary>
        public ImageMetaData? MetaData { get; private set; }

        /// <summary>The camera uses this to tell its own frames from N.I.N.A.'s.</summary>
        public IImageData? ImageData { get; private set; }

        /// <summary>
        /// When we asked N.I.N.A. for this exposure, used as the shutter time only when the headers have no
        /// DATE-OBS. It can be a little early, but it's far closer than the server's fallback of when the
        /// completion report arrives.
        /// </summary>
        public DateTime? ShutterOpenedAtUtc { get; private set; }

        /// <summary>Completes when this frame's ImageSaved event has been handled.</summary>
        public Task<ImageSavedEventArgs?> Saved => _saved.Task;

        public void RecordImageData(IImageData imageData) {
            ImageData = imageData;
            MetaData = imageData.MetaData;
        }

        public void RecordShutterOpen() => ShutterOpenedAtUtc = DateTime.UtcNow;

        public void RecordSolveAttempt() => WasSolveAttempted = true;

        public void RecordSolve(PlateSolveResult result, FrameWcs wcs) {
            SolveResult = result;
            _pendingWcs = wcs;
        }

        /// <summary>Takes the pending WCS, if any, and clears it so a solve goes into only one file.</summary>
        public FrameWcs? TakePendingWcs() {
            FrameWcs? wcs = _pendingWcs;
            _pendingWcs = null;
            return wcs;
        }

        public void CompleteSave(ImageSavedEventArgs? eventArgs) => _saved.TrySetResult(eventArgs);

        public void Cancel() {
            try { _cancellationSource.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>For a frame that's superseded or torn down.</summary>
        public void Abandon() {
            Cancel();
            _saved.TrySetCanceled();
        }

        /// <summary>Disposal always releases the awaiter, so nothing can wait on a dead operation.</summary>
        public void Dispose() {
            _saved.TrySetCanceled();
            _cancellationSource.Dispose();
        }
    }
}