using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// One frame as a source produced it, before any sizing or encoding. An HTTP camera gives JPEG
    /// bytes that are often ready to send, and the RTSP decoder gives pixels. Carrying both lets
    /// FeedFrameEncoder.Prepare be the only place that scales or encodes, so max width and quality
    /// are read fresh on every tick.
    /// </summary>
    internal readonly record struct SourceFrame(byte[]? Jpeg, BitmapSource? Image, DateTime CapturedUtc) {
        public static SourceFrame FromJpeg(byte[] jpeg) => new(jpeg, null, DateTime.UtcNow);
        public static SourceFrame FromImage(BitmapSource image) => new(null, image, DateTime.UtcNow);
    }

    internal readonly record struct FeedFrame(byte[] Jpeg, int Width, int Height, DateTime CapturedUtc);

    /// <summary>
    /// CaptureAsync throws on a failed capture, like an unreachable camera, a refused login or a
    /// response that isn't an image. WebcamFeed catches it, reports the reason to the dashboard and
    /// backs off. Null means the capture worked but there was no frame.
    ///
    /// A source should recover from a failed capture on its own. The caller only disposes it after
    /// repeated failures, so a source holding an expensive connection should reconnect internally.
    /// Kept narrow so an out-of-process RTSP decoder would just be one more implementation.
    /// </summary>
    internal interface IFeedSource : IAsyncDisposable {
        Task<SourceFrame?> CaptureAsync(CancellationToken cancellationToken);
    }
}
