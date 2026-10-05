using Newtonsoft.Json;
namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>
    /// Body for POST /api/pipeline/masters/upload-url/. Nulls are left out when serialized, and the
    /// server treats absent, null, "" and 0 as omitted. Of the conditional fields, each type must send
    /// only these or the server returns 400:
    ///   BIAS     temperature_c
    ///   DARK     temperature_c, exposure_seconds
    ///   DARKFLAT temperature_c, exposure_seconds
    ///   FLAT     exposure_seconds, filter_name
    /// </summary>
    public sealed class MasterUploadRequest {
        ///////////// Required for every type /////////////
        [JsonProperty("frame_type")]  public string FrameType { get; set; } = "";
        [JsonProperty("camera_name")] public string CameraName { get; set; } = "";
        [JsonProperty("binning_x")]   public int BinningX { get; set; }
        [JsonProperty("binning_y")]   public int BinningY { get; set; }
        [JsonProperty("gain")]        public int Gain { get; set; }
        [JsonProperty("offset")]      public int Offset { get; set; }

        ///////////// Conditional by frame type /////////////
        [JsonProperty("temperature_c")]      public int? TemperatureC { get; set; }
        [JsonProperty("exposure_seconds")]   public string? ExposureSeconds { get; set; } // string with at most 3 decimals, e.g. "300.000"
        [JsonProperty("filter_name")]        public string? FilterName { get; set; }

        ///////////// Optional everywhere /////////////
        [JsonProperty("readout_mode")]         public string? ReadoutMode { get; set; }
        [JsonProperty("file_extension")]       public string? FileExtension { get; set; } // fits|fit|fts|xisf (default fits)
        [JsonProperty("source_frame_count")]   public int? SourceFrameCount { get; set; }
        [JsonProperty("stacking_method")]      public string? StackingMethod { get; set; }
        [JsonProperty("sensor_temperature_c")] public double? SensorTemperatureC { get; set; }
        [JsonProperty("image_width")]          public int? ImageWidth { get; set; }
        [JsonProperty("image_height")]         public int? ImageHeight { get; set; }
        [JsonProperty("bit_depth")]            public int? BitDepth { get; set; }
        [JsonProperty("nina_profile_name")]    public string? NinaProfileName { get; set; }
        [JsonProperty("checksum_sha256")]      public string? ChecksumSha256 { get; set; } // 64 hex
    }
}