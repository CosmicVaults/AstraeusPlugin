using CosmicVaults.NINA.Astraeus.Engine.Calibration;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>Handed from ReportResult through CalibrateFrame to QueueUpload.</summary>
    internal sealed record PendingUpload(int CaptureId, string FilePath, byte[]? PreviewJpeg) {
        /// <summary>The calibrated copy written beside the raw, when calibration succeeded.</summary>
        public string? CalibratedFilePath { get; init; }

        /// <summary>The project's Keep Raw: false uploads only the calibrated frame. True without a plan.</summary>
        public bool ShouldKeepRaw { get; init; } = true;

        /// <summary>What CalibrateFrame did, for the upload-complete report. Null when no plan was given.</summary>
        public CalibrationProvenance? Provenance { get; init; }
    }
}
