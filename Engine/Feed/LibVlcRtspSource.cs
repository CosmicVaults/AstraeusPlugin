using LibVLCSharp.Shared;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// System.Windows.Media also has a MediaPlayer, so say which one we mean.
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// Pulls frames from an RTSP camera using LibVLC. The connection stays open across captures,
    /// because a fresh decoder per frame pays the RTSP handshake and a wait for the next keyframe
    /// each time. That's several seconds on cameras with 2-4 second keyframe intervals.
    ///
    /// Frames come through LibVLC's video callbacks. TakeSnapshot can only write a file, which would
    /// put camera images on disk and split the encoding in two. The decoder writes into our own
    /// buffer and FeedFrameEncoder does all the encoding.
    ///
    /// isbeorn/nina.plugin.rtsp, by N.I.N.A.'s author, ships the same native libraries the same way.
    /// We load them by full path from the plugin folder before initialising LibVLCSharp. LibVLCSharp
    /// would load them itself, but it throws away Windows' reason for a refusal, so every failure
    /// reads as "module not found".
    /// </summary>
    internal sealed class LibVlcRtspSource : IFeedSource {
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DisposeCaptureWait = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Cap on one decode buffer, sized for 4K BGRA. Raw pixels are far bigger than the encoded frame
        /// limit, so this isn't FeedFrameEncoder.MaxSourceBytes. It only refuses absurd formats.
        /// </summary>
        private const long MaxBufferBytes = 4096L * 2304L * 4L;

        private static readonly TimeSpan VideoPollInterval = TimeSpan.FromMilliseconds(100);

        private const int BytesPerPixel = 4;
        private const double BitmapDpi = 96;
        private const int MinFrameDimension = 2;

        private const int ErrorAccessDenied = 5;
        private const int ErrorBadExeFormat = 193;
        private const int ErrorVirusInfected = 225;
        private const int ErrorVirusDeleted = 226;
        private const int ErrorInvalidImageHash = 577;
        private const int ErrorSystemIntegrityPolicyViolation = 4551;

        private static readonly object InitializationLock = new();
        private static bool _isInitialised;
        private static string? _initializationError;

        private const string LibVlcFile = "libvlc.dll";
        private const string LibVlcCoreFile = "libvlccore.dll";
        private const string MissingFilesError =
            "The RTSP support files (libvlc) are missing from the Astraeus plugin folder.";

        /// <summary>Lets a DLL loaded by full path find its own imports in its own folder.</summary>
        private const uint LoadWithAlteredSearchPath = 0x8;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr reserved, uint flags);

        /// <summary>Anything like a URL. libvlc quotes the stream's full URL, login included.</summary>
        private static readonly Regex UrlPattern = new(@"\b[a-z][a-z0-9+.-]*://\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>How live555 starts the debug message carrying a socket error or RTSP status.</summary>
        private const string ConnectionErrorPrefix = "connection error ";

        private readonly record struct DecodedFrame(byte[] Pixels, int Width, int Height, int Pitch);

        private readonly string _url;
        private readonly string? _username;
        private readonly string? _password;
        private readonly int _maxWidth;
        private readonly SemaphoreSlim _captureLock = new(1, 1);
        private volatile bool _isDisposed;

        // Held in fields because LibVLC keeps the native function pointers for the life of the player.
        // An inline lambda would be collected after SetVideoCallbacks returns and crash later somewhere
        // unrelated.
        private readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback;
        private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
        private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;

        // Why the current connection is failing, from what libvlc's RTSP client (live555) has logged.
        // Written on libvlc's log thread and read by the waiting capture, so these are volatile or
        // swapped atomically instead of locked. Cleared for each connection.
        private volatile bool _isLoginRefused;
        private volatile bool _isCameraUnreachable;
        private string? _streamRefusal;

        // Guards every field below it. Held by the decoder thread in the video callbacks and by
        // CaptureAsync, so nothing under it may block or call out.
        private readonly object _frameLock = new();
        private IntPtr _buffer;
        private int _bufferBytes;
        private int _frameWidth;
        private int _frameHeight;
        private int _framePitch;
        private bool _isFrameAvailable;

        // Set by a waiting capture and completed by the next display callback. It's null otherwise, so
        // the decoder doesn't copy frames at the stream's frame rate when we only want one every few
        // seconds.
        private TaskCompletionSource<DecodedFrame>? _pendingFrame;

        private LibVLC? _libVlc;
        private MediaPlayer? _player;

        public LibVlcRtspSource(string url, string? username, string? password, int maxWidth) {
            _url = url;
            (_username, _password) = FeedUrl.ResolveCredentials(url, username, password);
            _maxWidth = maxWidth;

            _formatCallback = OnFormat;
            _cleanupCallback = OnCleanup;
            _lockCallback = OnLock;
            _unlockCallback = OnUnlock;
            _displayCallback = OnDisplay;
        }

        public static bool TryInitialize(out string? error) {
            lock (InitializationLock) {
                if (_isInitialised) { error = null; return true; }
                if (_initializationError != null) { error = _initializationError; return false; }

                // A native load failure lasts for the life of the process, so the result is cached
                // either way. Retrying every capture would just fail the same way.
                try {
                    _initializationError = LoadLibVlc();
                    if (_initializationError == null) {
                        _isInitialised = true;
                        error = null;
                        return true;
                    }
                } catch (Exception ex) {
                    _initializationError = $"RTSP support could not be loaded: {ex.Message}";
                }
                Logger.Warning($"[Astraeus] {_initializationError}");
                error = _initializationError;
                return false;
            }
        }

        private static string? LoadLibVlc() {
            string? directory = ResolveLibVlcDirectory();
            if (directory == null) return MissingFilesError;

            string? error = FindMissingFiles(directory) ?? PreloadNative(directory);
            if (error != null) return error;

            Core.Initialize(directory);
            return null;
        }

        private static string? ResolveLibVlcDirectory() {
            string? pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return string.IsNullOrEmpty(pluginDirectory)
                ? null
                : Path.Combine(pluginDirectory, "libvlc", "win-x64");
        }

        /// <summary>
        /// Names whatever the libvlc folder is missing, or null when it's all there. A folder copied by
        /// hand can be incomplete, and each gap fails later as something vaguer, like "module not
        /// found" for a missing libvlccore or a stream that never opens for missing plugins.
        /// </summary>
        private static string? FindMissingFiles(string directory) {
            List<string> missing = new List<string>();
            if (!File.Exists(Path.Combine(directory, LibVlcFile))) missing.Add(LibVlcFile);
            if (!File.Exists(Path.Combine(directory, LibVlcCoreFile))) missing.Add(LibVlcCoreFile);
            if (!Directory.Exists(Path.Combine(directory, "plugins"))) missing.Add("the plugins folder");

            return missing.Count switch {
                0 => null,
                3 => MissingFilesError,
                _ => $"The RTSP support files are incomplete: {string.Join(" and ", missing)} " +
                     $"{(missing.Count == 1 ? "is" : "are")} missing from the plugin's libvlc\\win-x64 " +
                     "folder. Reinstall Astraeus, or copy the whole libvlc folder again."
            };
        }

        /// <summary>
        /// Returns Windows' reason if libvlccore or libvlc won't load, or null. Core.Initialize makes the
        /// same calls but only traces the path on failure, so every cause reads as "module not found".
        /// The handles are never freed because libvlc stays loaded for the whole process.
        /// </summary>
        private static string? PreloadNative(string directory) {
            foreach (string file in new[] { LibVlcCoreFile, LibVlcFile }) {
                IntPtr handle = LoadLibraryExW(Path.Combine(directory, file), IntPtr.Zero, LoadWithAlteredSearchPath);
                if (handle == IntPtr.Zero)
                    return $"RTSP support could not be loaded. {file} would not load: " +
                           DescribeLoadError(Marshal.GetLastPInvokeError());
            }
            return null;
        }

        /// <summary>
        /// Windows' reason for refusing a DLL, with a hint for causes the user can fix. We only ever
        /// report the file name, since the path has the Windows user name in it.
        /// </summary>
        private static string DescribeLoadError(int code) {
            string reason = $"{new Win32Exception(code).Message.TrimEnd('.')} (error {code})";
            return code switch {
                // Windows' own text for this one is "%1 is not a valid Win32 application".
                ErrorBadExeFormat => "it is not a 64-bit Windows DLL (error 193). Reinstall Astraeus.",
                ErrorAccessDenied or ErrorVirusInfected or ErrorVirusDeleted =>
                    $"{reason}. Check Windows Security > Protection history, or your antivirus.",
                ErrorInvalidImageHash or ErrorSystemIntegrityPolicyViolation =>
                    $"{reason}. Windows application control is blocking it.",
                _ => $"{reason}."
            };
        }

        public async Task<SourceFrame?> CaptureAsync(CancellationToken cancellationToken) {
            // One capture at a time. A second caller would overwrite the first one's request and leave
            // it waiting for a frame that goes to someone else.
            await _captureLock.WaitAsync(cancellationToken);
            try {
                // A capture that queued behind the dispose must not build a new player on a dead source.
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                await EnsurePlayingAsync(cancellationToken);

                TaskCompletionSource<DecodedFrame> request = new TaskCompletionSource<DecodedFrame>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_frameLock) _pendingFrame = request;

                DecodedFrame decoded = await WaitForFrameAsync(request, cancellationToken);

                BitmapSource image = BitmapSource.Create(decoded.Width, decoded.Height, BitmapDpi, BitmapDpi,
                    PixelFormats.Bgr32, null, decoded.Pixels, decoded.Pitch);
                image.Freeze();
                return SourceFrame.FromImage(image);
            } finally {
                _captureLock.Release();
            }
        }

        private async Task<DecodedFrame> WaitForFrameAsync(
            TaskCompletionSource<DecodedFrame> request, CancellationToken cancellationToken) {
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(FrameTimeout);
            try {
                return await request.Task.WaitAsync(timeoutSource.Token);
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                // Clear the request so the next display callback doesn't fill a capture that gave up.
                // Only if it's still ours, though, since a teardown may have replaced it.
                lock (_frameLock) {
                    if (ReferenceEquals(_pendingFrame, request)) _pendingFrame = null;
                }
                throw new TimeoutException(
                    $"the camera sent no frame within {(int)FrameTimeout.TotalSeconds} seconds");
            }
        }

        private async Task EnsurePlayingAsync(CancellationToken cancellationToken) {
            if (_player is { IsPlaying: true }) return;

            TearDownPlayer();
            if (!TryInitialize(out string? error))
                throw new InvalidOperationException(error ?? "RTSP support is unavailable.");

            // --no-audio drops a subsystem we never use. The quiet flags matter because LibVLC logs the
            // full MRL, and AstraeusLogSink would show it, password and all, on the dashboard.
            // --avcodec-hw=none keeps decoding on the CPU. GPU decoding via Direct3D 11 twice crashed
            // the host in testing (an access violation in an unloaded D3D11.dll), which in N.I.N.A.
            // would kill the imaging session. Software decoding costs about 2% of a core at 640x480.
            _libVlc = new LibVLC(
                "--no-audio",
                "--no-osd",
                "--no-video-title-show",
                "--quiet",
                "--no-stats",
                "--avcodec-hw=none");

            // Listen before anything can fail so we catch the camera's reason. These messages are only
            // read for that and never passed on to N.I.N.A.'s log, for the reason above.
            _isLoginRefused = false;
            _isCameraUnreachable = false;
            Volatile.Write(ref _streamRefusal, null);
            _libVlc.Log += OnLibVlcLog;

            using Media media = new Media(_libVlc, _url, FromType.FromLocation);
            // TCP is far more reliable on consumer network gear. On a feed that updates every few
            // seconds, UDP's lower latency isn't worth the dropped packets.
            media.AddOption(":rtsp-tcp");
            media.AddOption(":network-caching=300");
            if (!string.IsNullOrEmpty(_username)) {
                media.AddOption($":rtsp-user={_username}");
                media.AddOption($":rtsp-pwd={_password ?? string.Empty}");
            }

            _player = new MediaPlayer(_libVlc);
            // Both before Play. The format callback runs as the first picture is set up, and a player
            // that has already started would have opened a window instead.
            _player.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
            _player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);

            if (!_player.Play(media))
                throw new InvalidOperationException("the stream could not be opened");

            await WaitForVideoAsync(_player, cancellationToken);
        }

        /// <summary>
        /// Waits for the first display callback, since IsPlaying turns true as soon as the stream opens,
        /// well before there's anything to capture.
        /// </summary>
        private async Task WaitForVideoAsync(MediaPlayer player, CancellationToken cancellationToken) {
            DateTime deadline = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadline) {
                cancellationToken.ThrowIfCancellationRequested();

                // Fail fast instead of sitting out the timeout. A refused login or stream rarely reaches
                // Error. libvlc usually just ends the stream within a second or two.
                if (player.State is VLCState.Error or VLCState.Ended or VLCState.Stopped)
                    throw new InvalidOperationException(DescribeStreamFailure());

                lock (_frameLock) {
                    if (_isFrameAvailable) return;
                }

                await Task.Delay(VideoPollInterval, cancellationToken);
            }

            int seconds = (int)StartupTimeout.TotalSeconds;
            // Playing means the camera accepted the stream, so the problem is on its side. Some cameras
            // answer RTSP on several ports but only send video on one, and a high-res stream over a
            // slow link can take longer than this.
            if (player.State == VLCState.Playing)
                throw new TimeoutException(
                    $"the camera accepted the stream but sent no video within {seconds} seconds " +
                    "(check the port, or try its lower-resolution substream)");
            string? knownProblem = KnownStreamProblem();
            throw new TimeoutException(
                $"no video arrived within {seconds} seconds" + (knownProblem == null ? "" : $" ({knownProblem})"));
        }

        private string DescribeStreamFailure() =>
            KnownStreamProblem() ??
            "the camera ended the stream without sending video (check the URL, port and login)";

        /// <summary>The camera's own reason for refusing the stream, or null when it gave none.</summary>
        private string? KnownStreamProblem() {
            if (_isLoginRefused)
                return string.IsNullOrEmpty(_username)
                    ? "the camera needs a login, so enter its username and password"
                    : "the camera rejected the username or password";
            string? refusal = Volatile.Read(ref _streamRefusal);
            if (refusal != null) return $"the camera refused the stream: {refusal}";
            if (_isCameraUnreachable) return "nothing answered at that address and port";
            return null;
        }

        /// <summary>
        /// Notes the camera's reason from live555's messages, some of which only come at debug level. Runs
        /// on libvlc's threads for every message, so like the video callbacks it must not throw or log.
        /// </summary>
        private void OnLibVlcLog(object? sender, LogEventArgs logEvent) {
            try {
                if (logEvent.Module != "live555" || string.IsNullOrEmpty(logEvent.Message)) return;
                string message = logEvent.Message;

                if (message.StartsWith("authentication failed", StringComparison.OrdinalIgnoreCase)) {
                    _isLoginRefused = true;
                } else if (message.StartsWith("connection timeout", StringComparison.OrdinalIgnoreCase)) {
                    _isCameraUnreachable = true;
                } else if (message.StartsWith(ConnectionErrorPrefix, StringComparison.OrdinalIgnoreCase)) {
                    // Negative codes are socket errors. Positive ones are the camera's RTSP status.
                    if (int.TryParse(message[ConnectionErrorPrefix.Length..].Trim(), out int code) && code > 0)
                        Interlocked.CompareExchange(ref _streamRefusal, $"RTSP error {code}", null);
                    else
                        _isCameraUnreachable = true;
                } else if (logEvent.Level == LogLevel.Error &&
                           !message.StartsWith("Failed to connect with", StringComparison.OrdinalIgnoreCase) &&
                           !message.StartsWith("Nothing to play for", StringComparison.OrdinalIgnoreCase)) {
                    Interlocked.CompareExchange(ref _streamRefusal, Scrub(message), null);
                }
            } catch {
                // A reason is a nicety. Losing one only means the generic message is shown.
            }
        }

        /// <summary>A libvlc message with any URL and the camera's host taken out.</summary>
        private string Scrub(string message) =>
            FeedUrl.RedactCameraDetails(UrlPattern.Replace(message, "the stream"), _url).Trim();

        ///////////// Video callbacks /////////////
        // All five run on LibVLC's decoder thread. An exception would cross into native code and take
        // N.I.N.A. down, so every body is wrapped. They mustn't log either, since AstraeusLogSink sends
        // log events to the dashboard and these run at the stream's frame rate.

        private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
            ref uint pitches, ref uint lines) {
            try {
                // RV32 is BGRA32, which is WPF's Bgr32 with no conversion at the other end.
                WriteFourCc(chroma, "RV32");

                // Scaling here makes libvlc insert a scaler, so the frame arrives at its final size and
                // we never hold or resample a 4K buffer. The size is fixed for the connection, which is
                // why WebcamFeed rebuilds the source when the configured width changes.
                (int scaledWidth, int scaledHeight) = Fit((int)width, (int)height, _maxWidth);
                width = (uint)scaledWidth;
                height = (uint)scaledHeight;

                uint pitch = (uint)scaledWidth * BytesPerPixel;
                pitches = pitch;
                lines = height;

                long bytes = (long)pitch * scaledHeight;
                if (bytes <= 0 || bytes > MaxBufferBytes) return 0;

                lock (_frameLock) {
                    FreeBuffer();
                    _bufferBytes = (int)bytes;
                    _buffer = Marshal.AllocHGlobal(_bufferBytes);
                    _frameWidth = scaledWidth;
                    _frameHeight = scaledHeight;
                    _framePitch = (int)pitch;
                    _isFrameAvailable = false;
                }
                return 1;   // one plane
            } catch {
                // Zero planes tells libvlc we refused the format, which shows up as a play failure
                // instead of a crash.
                return 0;
            }
        }

        private void OnCleanup(ref IntPtr opaque) {
            try {
                lock (_frameLock) FreeBuffer();
            } catch {
                // Nothing useful to do on the decoder thread. Teardown frees the buffer again.
            }
        }

        private IntPtr OnLock(IntPtr opaque, IntPtr planes) {
            try {
                lock (_frameLock) {
                    // No buffer (a refused format, or one already cleaned up) leaves planes[0] alone.
                    if (_buffer != IntPtr.Zero) Marshal.WriteIntPtr(planes, _buffer);
                }
            } catch {
                // Leaving planes[0] untouched makes libvlc skip the picture rather than write wild.
            }
            return IntPtr.Zero;   // single buffer, so there is no picture identity to track
        }

        private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) {
            // Nothing to do. The picture is only complete at display, and there's one buffer.
        }

        private void OnDisplay(IntPtr opaque, IntPtr picture) {
            try {
                TaskCompletionSource<DecodedFrame>? request;
                DecodedFrame frame;

                lock (_frameLock) {
                    _isFrameAvailable = true;

                    // Only copy for a waiting capture, not every multi-megabyte frame at the stream's
                    // frame rate.
                    request = _pendingFrame;
                    if (request == null || _buffer == IntPtr.Zero) return;
                    _pendingFrame = null;

                    byte[] pixels = new byte[_bufferBytes];
                    Marshal.Copy(_buffer, pixels, 0, _bufferBytes);
                    frame = new DecodedFrame(pixels, _frameWidth, _frameHeight, _framePitch);
                }

                // Completed outside the lock, and with RunContinuationsAsynchronously set on the
                // source, so the waiting capture never resumes on the decoder thread.
                request.TrySetResult(frame);
            } catch {
                // A dropped frame isn't worth risking the decoder thread. The capture that asked for
                // it times out and the loop backs off.
            }
        }

        private static void WriteFourCc(IntPtr chroma, string fourCc) {
            for (int i = 0; i < 4; i++) Marshal.WriteByte(chroma, i, (byte)fourCc[i]);
        }

        /// <summary>Dimensions are kept even because the scaler prefers them.</summary>
        private static (int Width, int Height) Fit(int width, int height, int maxWidth) {
            if (width <= 0 || height <= 0 || maxWidth <= 0 || width <= maxWidth)
                return (Math.Max(MinFrameDimension, width & ~1), Math.Max(MinFrameDimension, height & ~1));

            int scaledHeight = (int)Math.Round(height * (double)maxWidth / width);
            return (Math.Max(MinFrameDimension, maxWidth & ~1), Math.Max(MinFrameDimension, scaledHeight & ~1));
        }

        /// <summary>Callers hold _frameLock.</summary>
        private void FreeBuffer() {
            if (_buffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferBytes = 0;
        }

        private void TearDownPlayer() {
            // Stop and dispose first. The decoder thread must be gone before its buffer is freed, and
            // disposing the player runs the cleanup callback.
            try {
                _player?.Stop();
                _player?.Dispose();
            } catch (Exception ex) {
                Logger.Warning($"[Astraeus] RTSP player did not stop cleanly: {ex.Message}");
            }
            _player = null;

            try {
                if (_libVlc != null) _libVlc.Log -= OnLibVlcLog;
                _libVlc?.Dispose();
            } catch (Exception ex) {
                Logger.Warning($"[Astraeus] LibVLC did not dispose cleanly: {ex.Message}");
            }
            _libVlc = null;

            TaskCompletionSource<DecodedFrame>? abandoned;
            lock (_frameLock) {
                FreeBuffer();
                _isFrameAvailable = false;
                abandoned = _pendingFrame;
                _pendingFrame = null;
            }
            // Otherwise a capture waiting on the old player would sit out its timeout for nothing.
            abandoned?.TrySetException(
                new InvalidOperationException("the RTSP connection was closed while waiting for a frame"));
        }

        public ValueTask DisposeAsync() {
            // Stopping the player can block briefly on the decoder thread, so keep it off the caller.
            return new ValueTask(Task.Run(() => DisposeUnderCaptureLockAsync()));
        }

        // A capture in flight gets a moment to finish before its player is torn down. One that runs
        // over is failed by the teardown, and the lock is left undisposed because that capture still
        // has to release it.
        private async Task DisposeUnderCaptureLockAsync() {
            bool hasCaptureLock = await _captureLock.WaitAsync(DisposeCaptureWait);
            _isDisposed = true;
            try {
                TearDownPlayer();
            } finally {
                if (hasCaptureLock) {
                    _captureLock.Release();
                    _captureLock.Dispose();
                }
            }
        }
    }
}
