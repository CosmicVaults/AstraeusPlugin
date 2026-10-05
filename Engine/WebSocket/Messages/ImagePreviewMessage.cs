using System;
using System.IO;
using System.Text.Json.Serialization;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    /// <summary>
    /// A frame's stretched JPEG preview and N.I.N.A.'s statistics, in the envelope the server and both
    /// browser camera handlers already parse. A failed manual frame is still sent, so the control hub's
    /// frame counter advances.
    /// </summary>
    public sealed record ImagePreviewMessage : WsMessage {
        public ImagePreviewMessage(ImagePreviewPayload payload)
            : base(
                type: "preview",
                device: "camera",
                context: "image_preview",
                payload: ToJsonElement(new DataPayload<ImagePreviewPayload>(payload))
            ) { }
    }

    /// <summary>
    /// Wire shape of a preview frame. Keys are snake_case to match the CapturedImage columns and the
    /// capture-complete HTTP body. Nulls are dropped, so the browser reads a missing key as "not available".
    /// </summary>
    public sealed record ImagePreviewPayload {
        ///////////// Frame bookkeeping /////////////
        [JsonPropertyName("success")]           public bool    IsSuccess { get; init; }
        [JsonPropertyName("fail_reason")]       public string? FailReason { get; init; }
        /// <summary>A PreviewSource value. The control hub keeps only its own manual frames.</summary>
        [JsonPropertyName("source")]            public string? Source { get; init; }
        /// <summary>1-based position of this frame in its manual run. Absent for other sources.</summary>
        [JsonPropertyName("frame_index")]       public int?    FrameIndex { get; init; }
        [JsonPropertyName("frame_count")]       public int?    FrameCount { get; init; }
        /// <summary>The run's Save flag, true when N.I.N.A. wrote the frame to disk.</summary>
        [JsonPropertyName("saved")]             public bool    IsSaved { get; init; }
        [JsonPropertyName("client_ref")]        public string? ClientRef { get; init; }
        /// <summary>The server's CapturedImage id, once a saved frame has been registered.</summary>
        [JsonPropertyName("captured_image_id")] public int?    CapturedImageId { get; init; }

        ///////////// Image /////////////
        /// <summary>Base64 JPEG. Absent on failure or when encoding failed.</summary>
        [JsonPropertyName("image")]             public string? Image { get; init; }
        [JsonPropertyName("format")]            public string? Format { get; init; }
        [JsonPropertyName("file_name")]         public string? FileName { get; init; }

        ///////////// Statistics /////////////
        [JsonPropertyName("hfr")]                       public double? Hfr { get; init; }
        [JsonPropertyName("hfr_std_dev")]               public double? HfrStdDev { get; init; }
        [JsonPropertyName("detected_stars")]            public int?    DetectedStars { get; init; }
        [JsonPropertyName("mean")]                      public double? Mean { get; init; }
        [JsonPropertyName("median")]                    public double? Median { get; init; }
        [JsonPropertyName("median_absolute_deviation")] public double? MedianAbsoluteDeviation { get; init; }
        [JsonPropertyName("std_dev")]                   public double? StdDev { get; init; }
        [JsonPropertyName("min_adu")]                   public double? MinAdu { get; init; }
        [JsonPropertyName("max_adu")]                   public double? MaxAdu { get; init; }
        [JsonPropertyName("min_occurrences")]           public long?   MinOccurrences { get; init; }
        [JsonPropertyName("max_occurrences")]           public long?   MaxOccurrences { get; init; }
        [JsonPropertyName("bit_depth")]                 public int?    BitDepth { get; init; }

        ///////////// Acquisition /////////////
        [JsonPropertyName("camera_name")]          public string? CameraName { get; init; }
        [JsonPropertyName("readout_mode")]         public string? ReadoutMode { get; init; }
        [JsonPropertyName("filter_name")]          public string? FilterName { get; init; }
        /// <summary>Exposure actually taken, 3 dp, e.g. "30.000".</summary>
        [JsonPropertyName("exposure_seconds")]     public string? ExposureSeconds { get; init; }
        /// <summary>Shutter-open time, ISO-8601 UTC.</summary>
        [JsonPropertyName("observed_at")]          public string? ObservedAt { get; init; }
        [JsonPropertyName("gain")]                 public int?    Gain { get; init; }
        [JsonPropertyName("offset")]               public int?    Offset { get; init; }
        [JsonPropertyName("binning_x")]            public int?    BinningX { get; init; }
        [JsonPropertyName("binning_y")]            public int?    BinningY { get; init; }
        [JsonPropertyName("sensor_temperature_c")] public double? SensorTemperatureC { get; init; }

        public static ImagePreviewPayload FromResult(CaptureResult result, string source, bool isSaved,
            int? frameIndex = null, int? frameCount = null, string? clientRef = null, int? capturedImageId = null) {
            bool hasImage = result.PreviewJpeg is { Length: > 0 };
            return new ImagePreviewPayload {
                IsSuccess = true,
                Source = source,
                FrameIndex = frameIndex,
                FrameCount = frameCount,
                IsSaved = isSaved,
                ClientRef = clientRef,
                CapturedImageId = capturedImageId,

                Image = hasImage ? Convert.ToBase64String(result.PreviewJpeg!) : null,
                Format = hasImage ? "jpeg" : null,
                FileName = result.FilePath is { } path ? Path.GetFileName(path) : null,

                Hfr = Finite(result.Hfr),
                HfrStdDev = Finite(result.HfrStdDev),
                DetectedStars = result.DetectedStars,
                Mean = Finite(result.Mean),
                Median = Finite(result.Median),
                MedianAbsoluteDeviation = Finite(result.MedianAbsoluteDeviation),
                StdDev = Finite(result.StdDev),
                MinAdu = Finite(result.MinAdu),
                MaxAdu = Finite(result.MaxAdu),
                MinOccurrences = result.MinOccurrences,
                MaxOccurrences = result.MaxOccurrences,
                BitDepth = result.BitDepth,

                CameraName = result.CameraName,
                ReadoutMode = result.ReadoutMode,
                FilterName = result.FilterName,
                ExposureSeconds = result.ExposureSeconds,
                ObservedAt = result.ObservedAt,
                Gain = result.Gain,
                Offset = result.Offset,
                BinningX = result.BinningX,
                BinningY = result.BinningY,
                SensorTemperatureC = Finite(result.SensorTemperatureC),
            };
        }

        public static ImagePreviewPayload Failure(int frameIndex, int frameCount, string reason, bool isSaved,
            string? clientRef) =>
            new ImagePreviewPayload {
                IsSuccess = false,
                FailReason = reason,
                Source = PreviewSource.Manual,
                FrameIndex = frameIndex,
                FrameCount = frameCount,
                IsSaved = isSaved,
                ClientRef = clientRef,
            };

        /// <summary>
        /// Drops NaN and Infinity, since the envelope serializer allows named float literals and would put
        /// the string "NaN" where the browser expects a number.
        /// </summary>
        private static double? Finite(double? value) =>
            value is double number && !double.IsNaN(number) && !double.IsInfinity(number) ? number : null;
    }

    public static class PreviewSource {
        /// <summary>A control-hub capture run.</summary>
        public const string Manual = "manual";
        public const string Autopilot = "autopilot";
        /// <summary>An exposure N.I.N.A.'s solver took while the plugin was centring or solving.</summary>
        public const string PlateSolve = "plate_solve";
        /// <summary>An exposure N.I.N.A.'s autofocus took for a run the plugin started.</summary>
        public const string Autofocus = "autofocus";
        /// <summary>
        /// An exposure N.I.N.A. took during a meridian flip the plugin started, for the re-centre or for
        /// an autofocus after the flip if the profile asks for one.
        /// </summary>
        public const string MeridianFlip = "meridian_flip";
    }
}
