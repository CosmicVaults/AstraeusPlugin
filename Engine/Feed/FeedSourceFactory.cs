using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// Builds the IFeedSource for the current settings. The feed and the options page's Test
    /// connection button both use it, so the test runs the same source the real feed will.
    /// </summary>
    internal static class FeedSourceFactory {
        /// <summary>
        /// Long enough for a slow camera on a busy LAN to answer, but a dead one won't hold a capture
        /// slot for most of an interval.
        /// </summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        /// <summary>The source for these settings, or the reason there isn't one for the caller to show.</summary>
        public static (IFeedSource? Source, string? Reason) Create(AstraeusSettings settings) {
            string? url = settings.GetFeedUrl();
            if (string.IsNullOrWhiteSpace(url))
                return (null, "No feed URL is set.");

            FeedSourceKind kind = settings.GetFeedSourceKind();
            string? username = settings.GetFeedUsername();
            string? password = settings.GetFeedPassword();

            switch (kind) {
                case FeedSourceKind.HttpSnapshot:
                    return (new HttpSnapshotSource(url, username, password, isMjpeg: false, RequestTimeout), null);

                case FeedSourceKind.Mjpeg:
                    return (new HttpSnapshotSource(url, username, password, isMjpeg: true, RequestTimeout), null);

                case FeedSourceKind.Rtsp:
                    // Checked up front so a missing native library gives one clear message instead of
                    // a native load failure mid-capture.
                    if (!LibVlcRtspSource.TryInitialize(out string? error))
                        return (null, error);
                    // Max width is needed at connect time because it fixes the decoder's buffer
                    // format. Quality isn't, since FeedFrameEncoder applies it per frame.
                    return (new LibVlcRtspSource(url, username, password, settings.GetFeedMaxWidth()), null);

                default:
                    return (null, $"Unknown feed source type {kind}.");
            }
        }
    }
}
