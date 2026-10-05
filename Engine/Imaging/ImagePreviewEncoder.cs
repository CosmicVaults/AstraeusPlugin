using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// Renders a captured frame into a small JPEG for the dashboard. Linear sensor data looks nearly
    /// black in 8 bits, so we stretch it with a percentile clip first. The stretch is for display only
    /// and never touches the saved pixels.
    /// </summary>
    internal static class ImagePreviewEncoder {
        private const int MaxPreviewWidth = 800;
        private const int JpegQuality = 80;
        private const double BlackPointPercentile = 0.001;
        private const double WhitePointPercentile = 0.999;
        private const int Gray16LevelCount = ushort.MaxValue + 1;

        public static byte[] EncodeJpeg(BitmapSource image) {
            double scale = Math.Min(1.0, (double)MaxPreviewWidth / image.PixelWidth);
            BitmapSource source = scale < 1.0
                ? new TransformedBitmap(image, new ScaleTransform(scale, scale))
                : image;

            source = StretchTo8Bit(source);
            source.Freeze();

            using MemoryStream stream = new MemoryStream();
            JpegBitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static BitmapSource StretchTo8Bit(BitmapSource source) {
            BitmapSource gray16 = source.Format == PixelFormats.Gray16
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Gray16, null, 0);

            int width = gray16.PixelWidth;
            int height = gray16.PixelHeight;
            ushort[] pixels = new ushort[width * height];
            gray16.CopyPixels(pixels, width * 2, 0);

            // Percentile clip from a histogram, O(n) with no sort
            int[] histogram = new int[Gray16LevelCount];
            foreach (ushort pixel in pixels) histogram[pixel]++;

            ushort blackPoint = Percentile(histogram, pixels.Length, BlackPointPercentile);
            ushort whitePoint = Percentile(histogram, pixels.Length, WhitePointPercentile);
            if (whitePoint <= blackPoint) {
                // A uniform frame. Open the range by one level, downwards if the black point is
                // already at the top (a saturated frame), so the range is never zero.
                if (blackPoint < ushort.MaxValue)
                    whitePoint = (ushort)(blackPoint + 1);
                else
                    blackPoint = ushort.MaxValue - 1;
            }

            double range = whitePoint - blackPoint;
            byte[] output = new byte[width * height];
            for (int i = 0; i < pixels.Length; i++) {
                double normalized = (pixels[i] - blackPoint) / range;
                output[i] = (byte)(Math.Clamp(normalized, 0.0, 1.0) * 255.0);
            }

            return BitmapSource.Create(width, height, source.DpiX, source.DpiY,
                PixelFormats.Gray8, null, output, width);
        }

        private static ushort Percentile(int[] histogram, int total, double fraction) {
            long target = (long)(total * fraction);
            long count = 0;
            for (int i = 0; i < histogram.Length; i++) {
                count += histogram[i];
                if (count >= target) return (ushort)i;
            }
            return ushort.MaxValue;
        }
    }
}