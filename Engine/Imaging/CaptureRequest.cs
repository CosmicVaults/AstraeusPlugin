using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    public sealed record CaptureRequest {
        private const int UseProfileGainOrOffset = -1;

        public double ExposureTime { get; init; }
        public BinningMode Binning { get; init; } = new BinningMode(1, 1);
        public string ImageType { get; init; } = CaptureSequence.ImageTypes.LIGHT;

        /// <summary>Whether to plate-solve the frame and embed the solution in the saved file.</summary>
        public bool IsPlateSolveRequested { get; init; }

        /// <summary>Rough pointing for the solver. Null means use the mount's reported position.</summary>
        public Coordinates? SolveHint { get; init; }

        /// <summary>Camera gain, or -1 to leave the camera on its current/profile gain.</summary>
        public int Gain { get; init; } = UseProfileGainOrOffset;

        /// <summary>Camera offset, or -1 to leave the camera on its current/profile offset.</summary>
        public int Offset { get; init; } = UseProfileGainOrOffset;

        public bool ShouldSaveToDisk { get; init; } = true;

        /// <summary>
        /// Zero-based frame number N.I.N.A. writes for $$FRAMENR$$ in the file name pattern. Left at
        /// zero, every frame of a target gets the same name and N.I.N.A. appends (1), (2)... to it.
        /// </summary>
        public int FrameNumber { get; init; }

        /// <summary>
        /// A frame asked for from the dashboard, of any image type, plate-solved from the mount's own
        /// position when requested. There's no target, so no offset to measure.
        /// </summary>
        public static CaptureRequest Manual(double exposureTime, string imageType, BinningMode binning,
            bool shouldSaveToDisk, int gain = UseProfileGainOrOffset, int offset = UseProfileGainOrOffset,
            bool isPlateSolveRequested = false) =>
            new CaptureRequest {
                ExposureTime = exposureTime,
                ImageType = imageType,
                Binning = binning,
                Gain = gain,
                Offset = offset,
                ShouldSaveToDisk = shouldSaveToDisk,
                IsPlateSolveRequested = isPlateSolveRequested
            };

        public static CaptureRequest Autopilot(double exposureTime, short binning, Coordinates? solveHint,
            int gain = UseProfileGainOrOffset, int offset = UseProfileGainOrOffset, int frameNumber = 0) =>
            new CaptureRequest {
                ExposureTime = exposureTime,
                ImageType = CaptureSequence.ImageTypes.LIGHT,
                Binning = new BinningMode(binning, binning),
                IsPlateSolveRequested = solveHint is not null,
                SolveHint = solveHint,
                Gain = gain,
                Offset = offset,
                ShouldSaveToDisk = true,
                FrameNumber = frameNumber
            };
    }
}