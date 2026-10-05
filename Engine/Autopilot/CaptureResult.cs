namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    public class CaptureResult {
        public bool    IsSuccess                 { get; init; }
        public bool    IsPlateSolved             { get; init; }
        // False when no plate solver is configured, so an unsolved frame there says nothing about the sky.
        public bool    WasPlateSolveAttempted    { get; init; }
        public string? FilePath                  { get; init; }
        public string? FailReason                { get; init; }
        public double? Hfr                       { get; init; }
        public double? HfrStdDev                 { get; init; }
        public int?    DetectedStars             { get; init; }
        public int?    BitDepth                  { get; init; }
        public double? StdDev                    { get; init; }
        public double? Mean                      { get; init; }
        public double? Median                    { get; init; }
        public double? MedianAbsoluteDeviation   { get; init; }
        public double? MaxAdu                    { get; init; }
        public long?   MaxOccurrences            { get; init; }
        public double? MinAdu                    { get; init; }
        public long?   MinOccurrences            { get; init; }

        ///////////// Acquisition metadata /////////////
        // For master matching on the server.
        public string? CameraName          { get; init; }
        public int?    Gain                { get; init; }
        public int?    Offset              { get; init; }
        public string? ReadoutMode         { get; init; }
        public int?    TemperatureC        { get; init; } // whole degrees: the reading, else the set point
        public double? SensorTemperatureC  { get; init; } // measured CCD-TEMP
        public int?    BinningX            { get; init; }
        public int?    BinningY            { get; init; }
        public string? ExposureSeconds     { get; init; } // 3dp string, e.g. "300.000"
        public string? FilterName          { get; init; }
        public string? ObservedAt          { get; init; } // ISO-8601 (DATE-OBS)

        /// <summary>
        /// The pier side from the frame's headers, so it's the side during the exposure, not where the
        /// mount is now. Null when the mount has no pier side or its driver doesn't report one.
        /// </summary>
        public string? SideOfPier          { get; init; }

        /// <summary>
        /// How far the plate solve put the frame from its target, which shows where the mount really pointed
        /// whatever it reported. Null when it wasn't solved or had no target.
        /// </summary>
        public double? PointingOffsetArcmin { get; init; }

        // Rendered from the frame's BitmapSource at capture time, because the FITS on disk can't be
        // rendered later without NINA's pipeline. Not sent in the complete call.
        public byte[]? PreviewJpeg               { get; set; }
    }
}