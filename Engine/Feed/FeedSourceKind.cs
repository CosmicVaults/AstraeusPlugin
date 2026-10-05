
namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// How the plugin pulls frames from the camera. The HTTP kinds are managed code only, since the
    /// camera already returns JPEG. Rtsp is the fallback for cameras that support nothing else, and
    /// the only kind that loads native decoders.
    /// </summary>
    public enum FeedSourceKind {
        HttpSnapshot = 0,

        /// <summary>We take the first full part and disconnect.</summary>
        Mjpeg = 1,

        Rtsp = 2
    }
}
