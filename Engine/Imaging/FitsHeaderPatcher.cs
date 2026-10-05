using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// Patches WCS keywords into the primary header of a saved FITS file. N.I.N.A.'s PopulateHeaderCards
    /// doesn't write WorldCoordinateSystem to FITS headers, so we edit the file once the save is done.
    /// Never throws. Failures are logged and leave the file untouched.
    /// </summary>
    internal sealed class FitsHeaderPatcher(Action<string> log, Action<string> logWarning) {
        private const int CardSize = 80;
        private const int KeywordLength = 8;
        private const int BlockSize = 2880;
        private const int CardsPerBlock = BlockSize / CardSize;
        private const int MaxHeaderBlocksScanned = 10;

        private const int MaxIoAttempts = 5;
        private static readonly TimeSpan IoRetryDelay = TimeSpan.FromMilliseconds(200);

        private static readonly string[] PatchableExtensions = { ".fits", ".fit", ".fts" };

        // Not a FITS extension, so nothing that scans for frames picks a leftover one up.
        private const string TemporaryFileSuffix = ".wcs-tmp";

        /// <summary>Not XISF, TIFF or compressed .fz.</summary>
        public static bool IsPatchable(string path) =>
            Array.IndexOf(PatchableExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        /// <summary>
        /// Writes the patched file beside the original and swaps it in whole, so a crash part-way leaves the
        /// science frame as it was.
        /// </summary>
        public async Task InjectWcsAsync(string path, FrameWcs wcs, CancellationToken cancellationToken) {
            string temporaryPath = path + TemporaryFileSuffix;
            try {
                if (!IsPatchable(path)) return;

                // ImageSaved can fire before N.I.N.A.'s finalize/history pipeline or an AV scanner lets
                // go of the file, so reads and writes retry on IOException.
                byte[] bytes = await WithIoRetryAsync(() => File.ReadAllBytes(path), cancellationToken);

                int endCardIndex = FindEndCardIndex(bytes);
                if (endCardIndex < 0) {
                    logWarning("WCS injection skipped: FITS END card not found.");
                    return;
                }

                // The data starts at the block after the one holding END.
                int dataOffset = (endCardIndex / CardsPerBlock + 1) * BlockSize;
                if (dataOffset > bytes.Length) {
                    logWarning("WCS injection skipped: the FITS header block is incomplete.");
                    return;
                }

                byte[] header = BuildPatchedHeader(bytes, endCardIndex, BuildWcsCards(wcs));
                await RunWithIoRetryAsync(() => WriteTemporaryFile(temporaryPath, header, bytes, dataOffset),
                    cancellationToken);
                await RunWithIoRetryAsync(() => File.Replace(temporaryPath, path, null), cancellationToken);
                log($"WCS injected into FITS header: {Path.GetFileName(path)}");
            } catch (Exception ex) {
                logWarning($"Failed to inject WCS into FITS file: {ex.Message}");
            } finally {
                DeleteIfPresent(temporaryPath);
            }
        }

        /// <summary>
        /// Drops old WCS cards so patching the same file twice doesn't duplicate them. Kept cards are copied
        /// as raw bytes, never round-tripped through text.
        /// </summary>
        private static byte[] BuildPatchedHeader(byte[] original, int endCardIndex, string[] wcsCards) {
            HashSet<string> wcsKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string card in wcsCards) wcsKeys.Add(card[..KeywordLength].TrimEnd());

            using MemoryStream header = new MemoryStream();
            for (int cardIndex = 0; cardIndex < endCardIndex; cardIndex++) {
                int cardOffset = cardIndex * CardSize;
                string keyword = Encoding.ASCII.GetString(original, cardOffset, KeywordLength).TrimEnd();
                if (wcsKeys.Contains(keyword)) continue;
                header.Write(original, cardOffset, CardSize);
            }
            foreach (string card in wcsCards) header.Write(Encoding.ASCII.GetBytes(card));
            header.Write(Encoding.ASCII.GetBytes("END".PadRight(CardSize)));

            long partialBlockBytes = header.Length % BlockSize;
            if (partialBlockBytes > 0) {
                for (long paddingIndex = partialBlockBytes; paddingIndex < BlockSize; paddingIndex++)
                    header.WriteByte((byte)' ');
            }
            return header.ToArray();
        }

        private static void WriteTemporaryFile(string temporaryPath, byte[] header, byte[] original, int dataOffset) {
            using FileStream output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
            output.Write(header, 0, header.Length);
            output.Write(original, dataOffset, original.Length - dataOffset);
            // Flushed before the swap, or a crash just after it could leave an empty frame behind.
            output.Flush(flushToDisk: true);
        }

        private static void DeleteIfPresent(string path) {
            try {
                if (File.Exists(path)) File.Delete(path);
            } catch (Exception) {
                // A stray temporary file is harmless, and the next patch of this frame overwrites it.
            }
        }

        private static int FindEndCardIndex(byte[] bytes) {
            int searchLimit = Math.Min(bytes.Length / CardSize, CardsPerBlock * MaxHeaderBlocksScanned);
            for (int i = 0; i < searchLimit; i++) {
                int cardOffset = i * CardSize;
                if (bytes[cardOffset] == 'E' && bytes[cardOffset + 1] == 'N' && bytes[cardOffset + 2] == 'D' &&
                    (cardOffset + 3 >= bytes.Length || bytes[cardOffset + 3] == ' ' || bytes[cardOffset + 3] == 0))
                    return i;
            }
            return -1;
        }

        private static string[] BuildWcsCards(FrameWcs wcs) => new[] {
            TextCard  ("CTYPE1", "RA---TAN", "Coordinate projection type"),
            TextCard  ("CTYPE2", "DEC--TAN", "Coordinate projection type"),
            NumberCard("CRVAL1", wcs.CrVal1, "[deg] RA at reference pixel"),
            NumberCard("CRVAL2", wcs.CrVal2, "[deg] Dec at reference pixel"),
            NumberCard("CRPIX1", wcs.CrPix1, "[px] Reference pixel X"),
            NumberCard("CRPIX2", wcs.CrPix2, "[px] Reference pixel Y"),
            NumberCard("CD1_1",  wcs.Cd11,   "[deg/px] WCS transform matrix"),
            NumberCard("CD1_2",  wcs.Cd12,   "[deg/px] WCS transform matrix"),
            NumberCard("CD2_1",  wcs.Cd21,   "[deg/px] WCS transform matrix"),
            NumberCard("CD2_2",  wcs.Cd22,   "[deg/px] WCS transform matrix"),
        };

        private static string TextCard(string key, string value, string comment)
            => ToCard($"{key,-8}= '{value}'{string.Empty,-10} / {comment}");

        private static string NumberCard(string key, double value, string comment)
            => ToCard($"{key,-8}= {value.ToString("G15", CultureInfo.InvariantCulture),20} / {comment}");

        private static string ToCard(string line)
            => line.Length > CardSize ? line[..CardSize] : line.PadRight(CardSize);

        private static async Task<T> WithIoRetryAsync<T>(Func<T> operation, CancellationToken cancellationToken) {
            for (int attempt = 1; ; attempt++) {
                try {
                    return operation();
                } catch (IOException) when (attempt < MaxIoAttempts) {
                    await Task.Delay(IoRetryDelay, cancellationToken);
                }
            }
        }

        private static Task RunWithIoRetryAsync(Action operation, CancellationToken cancellationToken)
            => WithIoRetryAsync<object?>(() => {
                operation();
                return null;
            }, cancellationToken);
    }
}