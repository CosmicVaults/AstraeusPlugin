using CosmicVaults.NINA.Astraeus.Engine.Calibration;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// One frame's post-capture work. ReportResult queues it on Context.PendingFrames, then
    /// CalibrateFrame and QueueUpload work through it. Each part is cleared when done, so a frame cut
    /// short by a secure shows what's still owed when those states next run.
    /// </summary>
    internal sealed class PendingFrame(string filePath) {
        public string FilePath { get; } = filePath;

        /// <summary>
        /// The frame's cloud storage key, or null with no camera to name it. Fixed at report time, since it
        /// holds the target name and date and a frame held back by a secure may upload after both change.
        /// </summary>
        public string? UploadKey { get; init; }

        /// <summary>
        /// The server's calibration plan, or null once calibration has run (whatever the outcome) or
        /// when the server sent none.
        /// </summary>
        public CalibrationPlan? Plan { get; set; }

        /// <summary>
        /// The upload to make, or null when the frame stays on this machine or once the upload has been
        /// attempted. CalibrateFrame attaches the calibrated copy and its provenance.
        /// </summary>
        public PendingUpload? Upload { get; set; }

        public bool IsFinished => Plan == null && Upload == null;
    }
}
