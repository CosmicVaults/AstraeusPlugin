using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Feed;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CosmicVaults.NINA.Astraeus.Controls {

    public record FeedSourceKindOption(FeedSourceKind Value, string DisplayName);

    public record FeedChoice(int Value, string DisplayName);

    /// <summary>
    /// These settings are never sent to the server, since a feed URL often carries the camera password.
    /// </summary>
    public class FeedSettingsViewModel : BaseViewModel {
        /// <summary>
        /// The longest a Test connection may run. Each source has its own deadlines, so this only catches
        /// one that hangs past them. It covers RTSP's worst case, where libvlc's first start took 41 s on a
        /// PC whose antivirus hadn't seen its files yet, then 20 s to wait for video and 10 s for a frame.
        /// </summary>
        private static readonly TimeSpan TestBackstop = TimeSpan.FromSeconds(90);

        private const double AssumedHeightToWidthRatio = 9.0 / 16.0;
        private const double EstimatedJpegCompressionFactor = 0.42;

        private readonly AstraeusSettings _settings;
        private readonly Action? _settingsChanged;
        private bool _isTesting;
        private string _testStatus = string.Empty;

        /// <param name="settingsChanged">Raised after any setting is written, so the feed loop can
        /// re-read its gate and its source signature without waiting out a sleep.</param>
        public FeedSettingsViewModel(AstraeusSettings settings, Action? settingsChanged = null) {
            _settings = settings;
            _settingsChanged = settingsChanged;
            ClearPasswordCommand = new RelayCommand(() => Password = null, () => HasPassword);
            TestConnectionCommand = new RelayCommand(TestConnection, () => !_isTesting);
        }

        private void Changed() => _settingsChanged?.Invoke();

        public List<FeedSourceKindOption> SourceKinds { get; } = new() {
            new(FeedSourceKind.HttpSnapshot, "HTTP snapshot (JPEG URL)"),
            new(FeedSourceKind.Mjpeg,        "MJPEG stream"),
            new(FeedSourceKind.Rtsp,         "RTSP stream"),
        };

        public bool IsFeedEnabled {
            get => _settings.IsFeedEnabled();
            set { _settings.SetIsFeedEnabled(value); OnPropertyChanged(); Changed(); }
        }

        public FeedSourceKind SourceKind {
            get => _settings.GetFeedSourceKind();
            set {
                _settings.SetFeedSourceKind(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(UrlHint));
                Changed();
            }
        }

        public string? Url {
            get => _settings.GetFeedUrl();
            set {
                // Credentials pasted into the URL move to the username and the protected password,
                // so the URL kept in the plain-text profile carries no secret.
                string? url = FeedUrl.WithoutCredentials(value,
                    out string? embeddedUsername, out string? embeddedPassword);
                _settings.SetFeedUrl(url);
                if (embeddedUsername != null) Username = embeddedUsername;
                if (embeddedPassword != null) Password = embeddedPassword;
                OnPropertyChanged();
                Changed();
            }
        }

        public string? Username {
            get => _settings.GetFeedUsername();
            set { _settings.SetFeedUsername(value); OnPropertyChanged(); Changed(); }
        }

        /// <summary>
        /// Write-only as far as the UI goes. PasswordBox can't be bound, so the code-behind pushes into
        /// this on every change. The getter is never displayed. The page shows HasPassword instead.
        /// </summary>
        public string? Password {
            get => _settings.GetFeedPassword();
            set {
                _settings.SetFeedPassword(value);
                OnPropertyChanged(nameof(HasPassword));
                OnPropertyChanged(nameof(PasswordStatus));
                ClearPasswordCommand.RaiseCanExecuteChanged();
                Changed();
            }
        }

        public bool HasPassword => !string.IsNullOrEmpty(_settings.GetFeedPassword());

        public string PasswordStatus => HasPassword
            ? "A password is stored on this machine and is never sent to Astraeus."
            : "No password stored.";

        // The three bandwidth settings are fixed lists so the user picks the limits instead of hitting
        // a silent clamp. Each is still clamped on read and write for values written some other way,
        // and a stored value snaps to the nearest choice on read so a dropdown is never blank.

        public List<FeedChoice> WidthOptions { get; } = new() {
            new(320,  "320 px (smallest)"),
            new(480,  "480 px"),
            new(640,  "640 px (default)"),
            new(800,  "800 px"),
            new(960,  "960 px"),
            new(1280, "1280 px"),
            new(1920, "1920 px (largest)"),
        };

        public List<FeedChoice> QualityOptions { get; } = new() {
            new(40, "40 (smallest files)"),
            new(55, "55"),
            new(65, "65"),
            new(78, "78 (default)"),
            new(85, "85"),
            new(92, "92 (best)"),
        };

        /// <summary>
        /// Each frame is about 47 KB and goes once to each watching browser, so the fastest choice costs
        /// thirty times the default. The server won't relay faster than that whatever a plugin sends.
        /// </summary>
        public List<FeedChoice> IntervalOptions { get; } = new() {
            new(2,   "Every 2 s (fastest)"),
            new(5,   "Every 5 s"),
            new(10,  "Every 10 s"),
            new(15,  "Every 15 s"),
            new(30,  "Every 30 s"),
            new(60,  "Every minute (default)"),
            new(120, "Every 2 minutes"),
            new(300, "Every 5 minutes"),
        };

        private static int Nearest(List<FeedChoice> choices, int storedValue) =>
            choices.MinBy(choice => Math.Abs(choice.Value - storedValue))!.Value;

        public int MaxWidth {
            get => Nearest(WidthOptions, _settings.GetFeedMaxWidth());
            set {
                _settings.SetFeedMaxWidth(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(BandwidthEstimate));
                Changed();
            }
        }

        public int JpegQuality {
            get => Nearest(QualityOptions, _settings.GetFeedJpegQuality());
            set {
                _settings.SetFeedJpegQuality(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(BandwidthEstimate));
                Changed();
            }
        }

        public int IntervalSeconds {
            get => Nearest(IntervalOptions, _settings.GetFeedIntervalSeconds());
            set {
                _settings.SetFeedIntervalSeconds(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(BandwidthEstimate));
                Changed();
            }
        }

        public string UrlHint => SourceKind switch {
            FeedSourceKind.HttpSnapshot =>
                "A URL that returns one JPEG per request, e.g. http://camera/onvif/snapshot or " +
                "http://camera/ISAPI/Streaming/channels/101/picture. Needs no decoding, so try this first.",
            FeedSourceKind.Mjpeg =>
                "A multipart MJPEG stream, e.g. http://camera/video.mjpg. Astraeus takes one frame and disconnects.",
            FeedSourceKind.Rtsp =>
                "An RTSP stream, e.g. rtsp://camera:554/stream1. Astraeus connects over TCP. Prefer the " +
                "camera's low-resolution substream. The port and path vary by camera: check its manual " +
                "if it logs in but sends no video.",
            _ => string.Empty
        };

        /// <summary>
        /// Rough upload cost at the current settings, so the user sees what raising the quality costs
        /// before their connection does. Real frame sizes vary a lot with how busy the scene is.
        /// </summary>
        public string BandwidthEstimate {
            get {
                double megapixels = MaxWidth * (MaxWidth * AssumedHeightToWidthRatio) / 1_000_000.0;
                double kilobytes = megapixels * 1024 * (JpegQuality / 100.0) * EstimatedJpegCompressionFactor;
                double kilobytesPerSecond = kilobytes / IntervalSeconds;
                return $"About {kilobytes:F0} KB per frame, roughly {kilobytesPerSecond * 8:F0} kbps " +
                       $"while somebody is watching, and near zero when nobody is.";
            }
        }

        public RelayCommand ClearPasswordCommand { get; }
        public RelayCommand TestConnectionCommand { get; }

        public string TestStatus {
            get => _testStatus;
            private set { _testStatus = value; OnPropertyChanged(); }
        }

        public string TestButtonText => _isTesting ? "Testing..." : "Test connection";

        /// <summary>
        /// It uses the same factory as the running feed, so a passing test means the feed will connect.
        /// </summary>
        private void TestConnection() {
            if (_isTesting) return;
            SetTesting(true);
            TestStatus = SourceKind == FeedSourceKind.Rtsp
                ? "Connecting... The first RTSP connection can take up to a minute while Windows checks " +
                  "the video files."
                : "Connecting...";
            // On the thread pool, because building an RTSP source loads and starts libvlc, which blocks.
            // On the UI thread that froze N.I.N.A.
            _ = Task.Run(RunTestAsync);
        }

        private async Task RunTestAsync() {
            string result;
            try {
                (IFeedSource? source, string? unavailableReason) = FeedSourceFactory.Create(_settings);
                await using IFeedSource? ownedSource = source;
                if (ownedSource == null) {
                    result = unavailableReason ?? "The feed is not configured.";
                } else {
                    using CancellationTokenSource backstopCancellation = new CancellationTokenSource(TestBackstop);
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    SourceFrame? capturedFrame = await ownedSource.CaptureAsync(backstopCancellation.Token);
                    stopwatch.Stop();

                    if (capturedFrame is not { } frame) {
                        result = "Connected, but the camera returned no frame.";
                    } else {
                        FeedFrame prepared = FeedFrameEncoder.Prepare(frame, MaxWidth, JpegQuality);
                        result = $"OK, {prepared.Width}x{prepared.Height}, " +
                                 $"{prepared.Jpeg.Length / 1024.0:F0} KB in {stopwatch.ElapsedMilliseconds} ms.";
                    }
                }
            } catch (OperationCanceledException) {
                result = "Timed out waiting for the camera.";
            } catch (Exception ex) {
                // The exception may quote the URL, which can carry the camera password.
                result = $"Failed: {FeedUrl.RedactCameraDetails(ex.Message, _settings.GetFeedUrl())}";
            }

            FinishTestOnUi(result);
        }

        private void SetTesting(bool value) {
            _isTesting = value;
            OnPropertyChanged(nameof(TestButtonText));
            TestConnectionCommand.RaiseCanExecuteChanged();
        }

        // The test runs on the thread pool, so marshal the result back before touching bound state.
        private void FinishTestOnUi(string message) {
            void Finish() {
                TestStatus = message;
                SetTesting(false);
            }

            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(Finish);
            else Finish();
        }
    }
}
