using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// Every feed frame is sized and encoded here, so max width and quality changes apply from the next
    /// frame without a reconnect. Kept apart from ImagePreviewEncoder, which forces Gray8 and would turn
    /// a colour webcam grey.
    /// </summary>
    internal static class FeedFrameEncoder {
        /// <summary>
        /// Frames bigger than this are rejected before decoding. A misconfigured 4K camera can send tens
        /// of MB a frame, and the server caps frame size anyway.
        /// </summary>
        public const int MaxSourceBytes = 16 * 1024 * 1024;

        /// <summary>True if the bytes start with a JPEG SOI marker.</summary>
        public static bool LooksLikeJpeg(byte[] data) =>
            data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

        /// <summary>
        /// Returns the frame at or below maxWidth, re-encoding only if it has to. Throws only on input
        /// that can't be decoded, which the caller treats as a failed capture.
        /// </summary>
        public static FeedFrame Prepare(SourceFrame source, int maxWidth, int quality) {
            if (source.Jpeg is { } jpeg) return PrepareJpeg(jpeg, maxWidth, quality, source.CapturedUtc);
            if (source.Image is { } image) return PrepareImage(image, maxWidth, quality, source.CapturedUtc);
            throw new InvalidOperationException("the source produced neither pixels nor an encoded frame");
        }

        private static FeedFrame PrepareJpeg(byte[] jpeg, int maxWidth, int quality, DateTime capturedUtc) {
            (int sourceWidth, int sourceHeight) = ReadSize(jpeg);

            // Small enough, so send the camera's own bytes. Re-encoding costs CPU and a generation of
            // quality, and the result is often no smaller.
            if (sourceWidth <= maxWidth)
                return new FeedFrame(jpeg, sourceWidth, sourceHeight, capturedUtc);

            BitmapSource scaled = DecodeScaled(jpeg, maxWidth);
            return new FeedFrame(EncodeJpeg(scaled, quality), scaled.PixelWidth, scaled.PixelHeight, capturedUtc);
        }

        /// <summary>
        /// The RTSP decoder already scales to the configured width, so this resize only matters between a
        /// width change and the reconnect that applies it.
        /// </summary>
        private static FeedFrame PrepareImage(BitmapSource image, int maxWidth, int quality, DateTime capturedUtc) {
            BitmapSource sized = image.PixelWidth > maxWidth ? Resize(image, maxWidth) : image;
            return new FeedFrame(EncodeJpeg(sized, quality), sized.PixelWidth, sized.PixelHeight, capturedUtc);
        }

        public static byte[] EncodeJpeg(BitmapSource image, int quality) {
            using MemoryStream output = new MemoryStream();
            int clampedQuality = Math.Clamp(quality,
                AstraeusSettings.MinFeedJpegQuality, AstraeusSettings.MaxFeedJpegQuality);
            JpegBitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = clampedQuality };
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(output);
            return output.ToArray();
        }

        private static BitmapSource Resize(BitmapSource image, int width) {
            double scale = width / (double)image.PixelWidth;
            TransformedBitmap scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            scaled.Freeze();
            return scaled;
        }

        /// <summary>
        /// Reads the size from the header only, via DelayCreation with no caching. This runs every few seconds
        /// on the imaging PC, and a full 8 MP decode just to learn the width would be most of the cost.
        /// </summary>
        private static (int Width, int Height) ReadSize(byte[] jpeg) {
            using MemoryStream stream = new MemoryStream(jpeg, writable: false);
            JpegBitmapDecoder decoder = new JpegBitmapDecoder(
                stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }

        /// <summary>
        /// Decodes at the target width. DecodePixelWidth scales during the DCT instead of resampling a
        /// full-size decode, which is much cheaper for a 4K source.
        /// </summary>
        private static BitmapSource DecodeScaled(byte[] jpeg, int width) {
            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(jpeg, writable: false);
            image.DecodePixelWidth = width;
            // OnLoad so the pixels are read before the stream goes out of scope.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
