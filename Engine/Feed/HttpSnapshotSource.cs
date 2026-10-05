using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// Pulls frames over HTTP, either from a URL that returns one JPEG per request or from an MJPEG
    /// stream we take one frame from. Nothing is decoded here because the camera already sends JPEG.
    /// That's why this is the default kind. It covers most ONVIF and consumer cameras, loads no
    /// native libraries and can't take N.I.N.A. down with it.
    /// </summary>
    internal sealed class HttpSnapshotSource : IFeedSource {
        private const int ReadChunkBytes = 64 * 1024;
        private const int NoPreviousByte = -1;
        private const byte JpegMarkerPrefix = 0xFF;
        private const byte StartOfImageMarker = 0xD8;
        private const byte EndOfImageMarker = 0xD9;

        // Its own client rather than AstraeusWebClient, so nothing meant for the Astraeus server (the
        // bearer, the version header) can reach a third-party device on the user's LAN.
        private readonly HttpClient _client;
        private readonly string _url;
        private readonly bool _isMjpeg;
        private readonly TimeSpan _timeout;

        public HttpSnapshotSource(string url, string? username, string? password, bool isMjpeg,
            TimeSpan timeout) {
            _url = url;
            _isMjpeg = isMjpeg;
            _timeout = timeout;

            HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = true };
            (string? resolvedUsername, string? resolvedPassword) =
                FeedUrl.ResolveCredentials(url, username, password);
            if (!string.IsNullOrEmpty(resolvedUsername)) {
                // A CredentialCache lets the handler answer a Digest challenge, which many cameras
                // use. A preset Basic header only works on cameras that accept Basic.
                CredentialCache cache = new CredentialCache();
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) {
                    NetworkCredential credential =
                        new NetworkCredential(resolvedUsername, resolvedPassword ?? string.Empty);
                    cache.Add(uri, "Digest", credential);
                    cache.Add(uri, "Basic", credential);
                }
                handler.Credentials = cache;
                handler.PreAuthenticate = true;
            }

            _client = new HttpClient(handler) {
                Timeout = timeout,
                // A still is buffered whole, so a URL that turns out to be a stream gets cut off at the
                // frame limit instead of filling memory until the timeout.
                MaxResponseContentBufferSize = FeedFrameEncoder.MaxSourceBytes
            };
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Astraeus", "1.0"));
        }

        /// <summary>
        /// The client buffers a still itself, since only then do the buffer limit and timeout cover the
        /// body. An MJPEG stream never ends, so we read just its first part.
        /// </summary>
        public async Task<SourceFrame?> CaptureAsync(CancellationToken cancellationToken) {
            HttpCompletionOption completionOption = _isMjpeg
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead;
            using HttpResponseMessage response = await _client.GetAsync(_url, completionOption, cancellationToken);
            response.EnsureSuccessStatusCode();

            byte[] jpeg = _isMjpeg
                ? await ReadFirstMjpegPartWithinTimeoutAsync(response, cancellationToken)
                : await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (jpeg.Length > FeedFrameEncoder.MaxSourceBytes) {
                int frameMegabytes = jpeg.Length / 1024 / 1024;
                int limitMegabytes = FeedFrameEncoder.MaxSourceBytes / 1024 / 1024;
                throw new InvalidOperationException(
                    $"frame is {frameMegabytes} MB, over the {limitMegabytes} MB limit");
            }
            if (!FeedFrameEncoder.LooksLikeJpeg(jpeg))
                throw new InvalidOperationException(
                    "response was not a JPEG (check the URL points at a snapshot, not a viewer page)");

            return SourceFrame.FromJpeg(jpeg);
        }

        /// <summary>
        /// The client's own timeout ends once the headers arrive, so a stream that stalls part-way would
        /// hang the feed.
        /// </summary>
        private async Task<byte[]> ReadFirstMjpegPartWithinTimeoutAsync(HttpResponseMessage response,
            CancellationToken cancellationToken) {
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            try {
                return await ReadFirstMjpegPartAsync(response, timeoutSource.Token);
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                throw new TimeoutException(
                    $"the MJPEG stream sent no complete frame within {(int)_timeout.TotalSeconds} seconds");
            }
        }

        /// <summary>
        /// Scans for the JPEG SOI/EOI markers because many cameras send a boundary that disagrees with the
        /// header, or leave out Content-Length. JPEG stuffs any 0xFF in the data as 0xFF 0x00, so a bare
        /// 0xFF 0xD9 can't appear inside a frame.
        /// </summary>
        private static async Task<byte[]> ReadFirstMjpegPartAsync(HttpResponseMessage response,
            CancellationToken cancellationToken) {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            byte[] rentedChunk = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
            try {
                using MemoryStream frame = new MemoryStream();
                bool hasStarted = false;
                int previous = NoPreviousByte;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(rentedChunk, cancellationToken)) > 0) {
                    if (ScanChunk(rentedChunk, bytesRead, frame, ref hasStarted, ref previous))
                        return frame.ToArray();

                    if (frame.Length > FeedFrameEncoder.MaxSourceBytes)
                        throw new InvalidOperationException("MJPEG part exceeded the size limit before ending");
                }
                throw new InvalidOperationException("MJPEG stream ended before a complete frame arrived");
            } finally {
                ArrayPool<byte>.Shared.Return(rentedChunk);
            }
        }

        private static bool ScanChunk(byte[] chunk, int count, MemoryStream frame, ref bool hasStarted,
            ref int previous) {
            for (int i = 0; i < count; i++) {
                byte current = chunk[i];
                if (!hasStarted) {
                    // Skip the part headers and resync on SOI (FF D8).
                    if (previous == JpegMarkerPrefix && current == StartOfImageMarker) {
                        frame.WriteByte(JpegMarkerPrefix);
                        frame.WriteByte(StartOfImageMarker);
                        hasStarted = true;
                    }
                } else {
                    frame.WriteByte(current);
                    if (previous == JpegMarkerPrefix && current == EndOfImageMarker) return true;
                }
                previous = current;
            }
            return false;
        }

        public ValueTask DisposeAsync() {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
