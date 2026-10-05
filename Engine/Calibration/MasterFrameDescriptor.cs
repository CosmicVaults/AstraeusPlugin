using CosmicVaults.NINA.Astraeus.Engine.Imaging;

namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>
    /// One calibration frame on disk, described from its header. The acquisition settings group like
    /// frames for stacking and describe a generated master to the server. Pixels aren't kept here.
    /// The generator loads them when it stacks a group.
    /// </summary>
    public sealed class MasterFrameDescriptor {
        private const int NotReported = -1;

        public required string FilePath { get; init; }
        public required MasterFrameType FrameType { get; init; }
        public string? CameraName { get; init; }
        public int Gain { get; init; } = NotReported;
        public int Offset { get; init; } = NotReported;
        public int BinX { get; init; } = 1;
        public int BinY { get; init; } = 1;
        public int Width { get; init; }
        public int Height { get; init; }
        public int BitDepth { get; init; } = 16;

        public double ExposureTimeSeconds { get; init; } = double.NaN;
        public double SetPointCelsius { get; init; } = double.NaN;
        public double TemperatureCelsius { get; init; } = double.NaN;
        public string? FilterName { get; init; }
        public string? ReadoutMode { get; init; }

        public override string ToString() {
            string exposure = double.IsNaN(ExposureTimeSeconds) ? "-" : $"{ExposureTimeSeconds:0.###}s";
            string temperature = AcquisitionMetadata.MatchingTemperature(TemperatureCelsius, SetPointCelsius)
                is int degrees ? $"{degrees}°C" : "-";
            string filter = string.IsNullOrEmpty(FilterName) ? "" : $", filter {FilterName}";
            return $"{FrameType} [{System.IO.Path.GetFileName(FilePath)}] " +
                   $"cam {CameraName ?? "-"}, gain {Gain}, offset {Offset}, bin {BinX}x{BinY}, {Width}x{Height}, " +
                   $"{exposure}, {temperature}{filter}";
        }
    }
}