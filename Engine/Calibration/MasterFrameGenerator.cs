using CosmicVaults.NINA.Astraeus.Engine.Imaging;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using NINA.Core.Enum;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>
    /// Stacks masters from a folder of raw darks, bias and flats and uploads them to the server. Files
    /// are sorted by their IMAGETYP header, grouped so each filter, exposure and temperature gets its
    /// own master, and stacked with a sigma-clipped mean. The settings button and the "Generate &amp;
    /// Upload Calibration Masters" sequence item both call GenerateAndUploadAsync. Never throws.
    /// Failures are logged and skipped.
    /// </summary>
    public sealed class MasterFrameGenerator : ServiceComponent {
        // FITS and XISF only. Raw calibration frames here are never bitmaps or camera RAW.
        private static readonly string[] SupportedExtensions = { ".fit", ".fits", ".fts", ".fz", ".xisf" };

        // Masters get this file-name prefix so a re-run skips them. They land in the scan folder when
        // no output folder is set.
        private const string MasterFilePrefix = "master_";
        private const string CreatorValue = "Astraeus";
        private const string CombineMethod = "SigmaClipMean";

        private const double SigmaLow = 3.0;
        private const double SigmaHigh = 3.0;
        private const int SigmaIterations = 2;
        private const int MinFramesForSigmaClip = 3;

        // Every frame in a group is held in memory as 16-bit pixels while stacking. A bigger group is
        // stacked from its newest frames that fit.
        private const long StackingMemoryBudgetBytes = 2L * BytesPerGigabyte;
        private const long BytesPerGigabyte = 1024L * 1024 * 1024;

        private const int ReadBitDepth = 16;
        private const int CancellationCheckPixelMask = 0xFFFFF;
        private const int MaxUploadAttempts = 2;

        private readonly IImageDataFactory _imageDataFactory;

        /// <summary>
        /// The running generator, so sequence items built by MEF can reach it without the observatory.
        /// Null when the plugin is disabled or shutting down.
        /// </summary>
        public static MasterFrameGenerator? Active { get; private set; }

        public MasterFrameGenerator(Observatory observatory, IWebSocketBus webSocketBus,
            IImageDataFactory imageDataFactory)
            : base(observatory, webSocketBus) {
            _imageDataFactory = imageDataFactory;
        }

        public override string DeviceType => "calibration";
        public override LogCategory DefaultLogCategory => LogCategory.Equipment;
        public override WsMessage? GetUpdateMessage() => null;
        protected override Task HandleCommandAsync(WsCommand command) => Task.CompletedTask;

        public override async Task Start() {
            await base.Start();
            Active = this;
        }

        public override Task Destroy() {
            if (ReferenceEquals(Active, this)) Active = null;
            return base.Destroy();
        }

        /// <summary>Never throws. onStatus, if given, gets the progress lines for the settings page.</summary>
        public async Task<MasterGenerationResult> GenerateAndUploadAsync(Action<string>? onStatus,
            CancellationToken cancellationToken) {
            try {
                return await Task.Run(() => GenerateAndUploadInternalAsync(onStatus, cancellationToken),
                    cancellationToken);
            } catch (OperationCanceledException) {
                Report(onStatus, "Master generation cancelled.");
                return new MasterGenerationResult(IsSuccess: false, WasCancelled: true, Summary: "Cancelled.");
            } catch (Exception ex) {
                LogError($"Master generation failed unexpectedly: {ex.Message}");
                Report(onStatus, $"Failed: {ex.Message}");
                return MasterGenerationResult.Failure($"Failed: {ex.Message}");
            }
        }

        private async Task<MasterGenerationResult> GenerateAndUploadInternalAsync(Action<string>? onStatus,
            CancellationToken cancellationToken) {
            string? scanFolder = Observatory.Settings.GetCalibrationScanFolder();
            if (string.IsNullOrWhiteSpace(scanFolder) || !Directory.Exists(scanFolder)) {
                Report(onStatus, "Calibration scan folder is not set or does not exist.");
                return MasterGenerationResult.Failure("No scan folder configured.");
            }

            string outputFolder = MasterIndex.ResolveMastersFolder(Observatory.Settings) ?? scanFolder;
            try {
                Directory.CreateDirectory(outputFolder);
            } catch (Exception ex) {
                LogWarning($"Cannot create the masters output folder: {ex.Message}");
                return MasterGenerationResult.Failure("Output folder unavailable.");
            }

            Report(onStatus, $"Scanning {scanFolder}...");
            List<MasterFrameDescriptor> descriptors = await ScanAsync(scanFolder, cancellationToken);
            if (descriptors.Count == 0) {
                Report(onStatus, "No raw calibration frames found.");
                return MasterGenerationResult.Failure("No calibration frames found.");
            }

            List<FrameGroup> groups = BuildGroups(descriptors);
            Report(onStatus, $"Found {descriptors.Count} frame(s) in {groups.Count} group(s). Stacking...");

            MasterIndex masterIndex = new MasterIndex(outputFolder);
            bool isAuthenticated = Observatory.Authenticator.IsAuthenticated;
            int generated = 0, uploaded = 0, declined = 0;

            foreach (FrameGroup group in groups) {
                cancellationToken.ThrowIfCancellationRequested();
                (MasterFrameDescriptor? master, string? savedPath, int combinedFrameCount) =
                    await StackAndWriteAsync(group, outputFolder, onStatus, cancellationToken);
                if (master == null || savedPath == null) continue;
                generated++;

                if (!isAuthenticated) continue;

                // Masters count against storage quota and are the biggest files we upload. We ask per
                // master, before the checksum work in UploadAsync. The stack is already on disk, so a
                // refusal only costs the transfer, and a later master can still go if space frees up.
                StorageVerdict verdict = await StorageGate.CheckAsync(Observatory, DeviceType, DefaultLogCategory);
                if (verdict != StorageVerdict.Upload) {
                    declined++;
                    LogWarning($"{Path.GetFileName(savedPath)} was not uploaded because your cloud storage is " +
                               "full, so it is not used for calibration.");
                    continue;
                }

                bool wasUploaded = await UploadAsync(master, savedPath, combinedFrameCount, masterIndex, onStatus,
                    cancellationToken);
                if (wasUploaded) uploaded++;
            }

            string summary;
            if (!isAuthenticated)
                summary = $"Generated {generated} master(s) in the masters folder. Not signed in, so skipped upload.";
            else if (declined > 0)
                summary = $"Generated {generated} master(s), uploaded {uploaded}. " +
                          $"No room in your cloud storage for {declined}.";
            else
                summary = $"Generated {generated} master(s), uploaded {uploaded}.";
            Report(onStatus, summary);
            Log(summary);

            // Success means every group stacked and every master uploaded. Calibration goes by the
            // server's masters, so one left only on this PC is no use.
            bool isEveryGroupStacked = generated == groups.Count;
            bool isEveryMasterUploaded = isAuthenticated && uploaded == generated;
            bool isSuccess = isEveryGroupStacked && isEveryMasterUploaded;
            return new MasterGenerationResult(IsSuccess: isSuccess, WasCancelled: false, Summary: summary);
        }

        ///////////// Scan + classify /////////////

        private async Task<List<MasterFrameDescriptor>> ScanAsync(string folder, CancellationToken cancellationToken) {
            string[] files;
            try {
                IEnumerable<string> allFiles = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
                files = allFiles.Where(IsRawCalibrationFile).ToArray();
            } catch (Exception ex) {
                LogWarning($"Failed to enumerate the calibration scan folder: {ex.Message}");
                return new List<MasterFrameDescriptor>();
            }

            List<MasterFrameDescriptor> descriptors = new List<MasterFrameDescriptor>();
            int missingGainOrOffsetCount = 0;
            foreach (string path in files) {
                cancellationToken.ThrowIfCancellationRequested();
                (MasterFrameDescriptor? descriptor, bool isGainOrOffsetMissing) =
                    await TryReadDescriptorAsync(path, cancellationToken);
                if (descriptor == null) continue;
                descriptors.Add(descriptor);
                if (isGainOrOffsetMissing) missingGainOrOffsetCount++;
            }

            // One line for the whole scan, since a camera that never writes them would warn on every file.
            if (missingGainOrOffsetCount > 0)
                LogWarning($"{missingGainOrOffsetCount} of {descriptors.Count} calibration frames have no " +
                           "GAIN/OFFSET headers. They are grouped and reported as gain 0, offset 0, as for " +
                           "lights from the same camera.");

            return descriptors;
        }

        private static bool IsRawCalibrationFile(string path) {
            if (!SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return false;
            return !Path.GetFileName(path).StartsWith(MasterFilePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private Task<IImageData> LoadFrameAsync(string path, CancellationToken cancellationToken) =>
            _imageDataFactory.CreateFromFile(path, bitDepth: ReadBitDepth, isBayered: false,
                RawConverterEnum.FREEIMAGE, cancellationToken);

        private async Task<(MasterFrameDescriptor? Descriptor, bool IsGainOrOffsetMissing)>
            TryReadDescriptorAsync(string path, CancellationToken cancellationToken) {
            try {
                IImageData image = await LoadFrameAsync(path, cancellationToken);
                ImageMetaData metadata = image.MetaData;
                MasterFrameType? frameType = ClassifyFrameType(metadata.Image.ImageType);
                if (frameType == null) {
                    Log($"Skipping (unrecognised IMAGETYP '{metadata.Image.ImageType}'): {Path.GetFileName(path)}");
                    return (null, false);
                }

                // N.I.N.A. gives -1 for a missing header and the server needs >= 0, so we use 0 and ScanAsync warns.
                int gain = metadata.Camera.Gain;
                int offset = metadata.Camera.Offset;
                bool isGainOrOffsetMissing =
                    AcquisitionMetadata.IsMissing(gain) || AcquisitionMetadata.IsMissing(offset);

                MasterFrameDescriptor descriptor = new MasterFrameDescriptor {
                    FilePath = path,
                    FrameType = frameType.Value,
                    CameraName = AcquisitionMetadata.NullIfBlank(metadata.Camera.Name),
                    Gain = AcquisitionMetadata.NormalizeGainOrOffset(gain),
                    Offset = AcquisitionMetadata.NormalizeGainOrOffset(offset),
                    BinX = metadata.Camera.BinX,
                    BinY = metadata.Camera.BinY,
                    Width = image.Properties.Width,
                    Height = image.Properties.Height,
                    BitDepth = image.Properties.BitDepth,
                    ExposureTimeSeconds = metadata.Image.ExposureTime,
                    SetPointCelsius = metadata.Camera.SetPoint,
                    TemperatureCelsius = metadata.Camera.Temperature,
                    ReadoutMode = AcquisitionMetadata.NullIfBlank(metadata.Camera.ReadoutModeName),
                    FilterName = AcquisitionMetadata.NullIfBlank(metadata.FilterWheel.Filter)
                };
                return (descriptor, isGainOrOffsetMissing);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogWarning($"Failed to read '{Path.GetFileName(path)}': {ex.Message}");
                return (null, false);
            }
        }

        private static MasterFrameType? ClassifyFrameType(string? imageType) {
            if (string.IsNullOrWhiteSpace(imageType)) return null;
            string normalized = imageType.Trim().ToUpperInvariant();
            if (normalized.Contains("BIAS") || normalized == "OFFSET") return MasterFrameType.Bias;
            // A dark-flat (FLATDARK, DARKFLAT, DARK_FLAT) contains both words, so check it before the
            // plain flat and dark rules.
            if (normalized.Contains("FLAT") && normalized.Contains("DARK")) return MasterFrameType.DarkFlat;
            if (normalized.Contains("FLAT")) return MasterFrameType.Flat;
            if (normalized.Contains("DARK")) return MasterFrameType.Dark;
            return null;
        }

        ///////////// Grouping /////////////

        private sealed record FrameGroup(
            MasterFrameType Type,
            MasterFrameDescriptor Representative,
            List<MasterFrameDescriptor> Frames);

        // One master per server signature. The key follows the server's uniqueness rule, plus width and
        // height so frames of different sizes never end up in one stack.
        private List<FrameGroup> BuildGroups(List<MasterFrameDescriptor> descriptors) {
            IEnumerable<IGrouping<string, MasterFrameDescriptor>> framesBySignature = descriptors.GroupBy(GroupKey);
            List<FrameGroup> groups = new List<FrameGroup>();
            foreach (IGrouping<string, MasterFrameDescriptor> signatureFrames in framesBySignature) {
                MasterFrameDescriptor representative = signatureFrames.First();
                groups.Add(new FrameGroup(representative.FrameType, representative, signatureFrames.ToList()));
            }
            return groups;
        }

        private static string GroupKey(MasterFrameDescriptor frame) {
            string common = $"{frame.CameraName?.ToLowerInvariant()}|{frame.BinX}x{frame.BinY}" +
                            $"|{frame.Width}x{frame.Height}|g{frame.Gain}|o{frame.Offset}" +
                            $"|rm{frame.ReadoutMode?.ToLowerInvariant()}";
            string filter = frame.FilterName?.ToLowerInvariant() ?? "";
            return frame.FrameType switch {
                MasterFrameType.Bias     => $"BIAS|{common}|t{TemperatureKey(frame)}",
                MasterFrameType.Dark     => $"DARK|{common}|t{TemperatureKey(frame)}|e{ExposureKey(frame)}",
                MasterFrameType.DarkFlat => $"DARKFLAT|{common}|t{TemperatureKey(frame)}|e{ExposureKey(frame)}",
                MasterFrameType.Flat     => $"FLAT|{common}|e{ExposureKey(frame)}|f{filter}",
                _                        => $"?|{common}"
            };
        }

        private static string TemperatureKey(MasterFrameDescriptor frame)
            => MatchingTemperature(frame)?.ToString(CultureInfo.InvariantCulture) ?? "na";

        private static string ExposureKey(MasterFrameDescriptor frame) {
            if (double.IsNaN(frame.ExposureTimeSeconds)) return "na";
            return frame.ExposureTimeSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        }

        ///////////// Stack + write /////////////

        private async Task<(MasterFrameDescriptor? Master, string? SavedPath, int CombinedFrameCount)>
            StackAndWriteAsync(FrameGroup group, string outputFolder, Action<string>? onStatus,
                CancellationToken cancellationToken) {
            IImageData? template = null;
            int width = group.Representative.Width, height = group.Representative.Height;
            int expectedLength = width * height;

            int maxFrames = MaxFramesWithinMemoryBudget(width, height);
            bool isOverMemoryBudget = group.Frames.Count > maxFrames;
            List<MasterFrameDescriptor> frames = isOverMemoryBudget ? NewestFirst(group.Frames) : group.Frames;
            List<ushort[]> buffers = new List<ushort[]>(Math.Min(group.Frames.Count, maxFrames));

            foreach (MasterFrameDescriptor frame in frames) {
                if (buffers.Count >= maxFrames) break;
                cancellationToken.ThrowIfCancellationRequested();
                IImageData image;
                try {
                    image = await LoadFrameAsync(frame.FilePath, cancellationToken);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    LogWarning($"Failed to load '{Path.GetFileName(frame.FilePath)}': {ex.Message}");
                    continue;
                }

                ushort[]? pixels = image.Data?.FlatArray;
                if (pixels == null || pixels.Length != expectedLength) {
                    LogWarning($"Skipping '{Path.GetFileName(frame.FilePath)}': " +
                               $"pixel count {pixels?.Length ?? 0} != {expectedLength}.");
                    continue;
                }

                template ??= image;
                buffers.Add(pixels);
            }

            if (buffers.Count == 0 || template == null) {
                LogWarning($"{Describe(group)}: no usable frames, skipped.");
                return (null, null, 0);
            }

            if (isOverMemoryBudget) {
                long budgetGigabytes = StackingMemoryBudgetBytes / BytesPerGigabyte;
                LogWarning($"{Describe(group)}: stacking the newest {buffers.Count} of {group.Frames.Count} " +
                           $"frames; more would not fit the {budgetGigabytes} GB stacking memory limit.");
            }

            Report(onStatus, $"Stacking {Describe(group)} from {buffers.Count} frame(s)...");
            ushort[] masterPixels = SigmaClipMeanStack(buffers, expectedLength, cancellationToken);

            ImageMetaData metadata = template.MetaData;
            metadata.Image.ImageType = FrameTypeName(group.Type);
            metadata.GenericHeaders.Add(new StringMetaDataHeader("CREATOR", CreatorValue, "Generated by Astraeus"));
            metadata.GenericHeaders.Add(new IntMetaDataHeader("NCOMBINE", buffers.Count, "Number of frames combined"));
            metadata.GenericHeaders.Add(new StringMetaDataHeader("STACKALG", CombineMethod, "Stacking algorithm"));
            metadata.GenericHeaders.Add(new DoubleMetaDataHeader("SIGMALO", SigmaLow, "Sigma-clip low threshold"));
            metadata.GenericHeaders.Add(new DoubleMetaDataHeader("SIGMAHI", SigmaHigh, "Sigma-clip high threshold"));
            metadata.GenericHeaders.Add(new StringMetaDataHeader("ASTRDATE",
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "Astraeus generation time (UTC)"));

            BaseImageData masterImage = _imageDataFactory.CreateBaseImageData(
                masterPixels, width, height, template.Properties.BitDepth, template.Properties.IsBayered, metadata);

            string savedPath;
            try {
                FileSaveInfo fileSaveInfo = new FileSaveInfo(Observatory.Settings.ProfileService) {
                    FilePath = outputFolder, FilePattern = BuildMasterFileName(group), FileType = FileTypeEnum.FITS
                };
                savedPath = await masterImage.SaveToDisk(fileSaveInfo, cancellationToken, forceFileType: true);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogError($"Failed to write master for {Describe(group)}: {ex.Message}");
                return (null, null, 0);
            }

            Log($"Wrote master {Path.GetFileName(savedPath)} ({buffers.Count} x {group.Type}).");
            return (group.Representative, savedPath, buffers.Count);
        }

        private static int MaxFramesWithinMemoryBudget(int width, int height) {
            long frameBytes = Math.Max(1L, (long)width * height * sizeof(ushort));
            long maxFrames = StackingMemoryBudgetBytes / frameBytes;
            return (int)Math.Clamp(maxFrames, 1L, int.MaxValue);
        }

        /// <summary>By file write time, since the descriptor has no acquisition time.</summary>
        private static List<MasterFrameDescriptor> NewestFirst(List<MasterFrameDescriptor> frames) {
            return frames.OrderByDescending(frame => LastWrittenUtc(frame.FilePath)).ToList();
        }

        private static DateTime LastWrittenUtc(string path) {
            try {
                return File.GetLastWriteTimeUtc(path);
            } catch (Exception) {
                return DateTime.MinValue;
            }
        }

        private static ushort[] SigmaClipMeanStack(List<ushort[]> frames, int length,
            CancellationToken cancellationToken) {
            int frameCount = frames.Count;
            ushort[] result = new ushort[length];
            double[] values = new double[frameCount];
            bool[] isActive = new bool[frameCount];

            for (int i = 0; i < length; i++) {
                // Check for Stop about every 1M pixels, so it's quick without checking every pixel.
                if ((i & CancellationCheckPixelMask) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++) {
                    values[frameIndex] = frames[frameIndex][i];
                    isActive[frameIndex] = true;
                }

                if (frameCount >= MinFramesForSigmaClip) SigmaClip(values, isActive);

                double finalSum = SumOfActive(values, isActive, out int finalCount);
                double average = finalCount > 0 ? finalSum / finalCount : 0;
                result[i] = ClampToUShort(average);
            }

            return result;
        }

        private static void SigmaClip(double[] values, bool[] isActive) {
            for (int iteration = 0; iteration < SigmaIterations; iteration++) {
                double sum = SumOfActive(values, isActive, out int count);
                if (count == 0) return;
                double mean = sum / count;
                double standardDeviation = Math.Sqrt(SquaredDeviationOfActive(values, isActive, mean) / count);
                if (standardDeviation <= 0) return;
                double lowerBound = mean - SigmaLow * standardDeviation;
                double upperBound = mean + SigmaHigh * standardDeviation;
                int removed = DeactivateOutside(values, isActive, lowerBound, upperBound);
                if (removed == 0) return;
            }
        }

        private static double SumOfActive(double[] values, bool[] isActive, out int activeCount) {
            double sum = 0;
            activeCount = 0;
            for (int frameIndex = 0; frameIndex < values.Length; frameIndex++) {
                if (!isActive[frameIndex]) continue;
                sum += values[frameIndex];
                activeCount++;
            }
            return sum;
        }

        private static double SquaredDeviationOfActive(double[] values, bool[] isActive, double mean) {
            double squaredDeviation = 0;
            for (int frameIndex = 0; frameIndex < values.Length; frameIndex++) {
                if (!isActive[frameIndex]) continue;
                double deviation = values[frameIndex] - mean;
                squaredDeviation += deviation * deviation;
            }
            return squaredDeviation;
        }

        private static int DeactivateOutside(double[] values, bool[] isActive, double lowerBound, double upperBound) {
            int removed = 0;
            for (int frameIndex = 0; frameIndex < values.Length; frameIndex++) {
                bool isOutlier = values[frameIndex] < lowerBound || values[frameIndex] > upperBound;
                if (!isActive[frameIndex] || !isOutlier) continue;
                isActive[frameIndex] = false;
                removed++;
            }
            return removed;
        }

        private static ushort ClampToUShort(double value) {
            if (value <= 0) return 0;
            if (value >= ushort.MaxValue) return ushort.MaxValue;
            return (ushort)Math.Round(value);
        }

        private static string FrameTypeName(MasterFrameType frameType) => frameType switch {
            MasterFrameType.Dark => "DARK",
            MasterFrameType.Bias => "BIAS",
            MasterFrameType.Flat => "FLAT",
            MasterFrameType.DarkFlat => "DARKFLAT",
            _ => "DARK"
        };

        private static string BuildMasterFileName(FrameGroup group) {
            MasterFrameDescriptor representative = group.Representative;
            string binning = $"bin{representative.BinX}x{representative.BinY}";
            string temperature = FormatTemperature(representative);
            string exposure = $"{FormatExposure(representative.ExposureTimeSeconds)}s";
            string gainAndOffset = $"g{representative.Gain}_o{representative.Offset}";
            string filter = Sanitize(representative.FilterName ?? "none");
            string darkParameters = $"{gainAndOffset}_{binning}_{exposure}_{temperature}";
            return group.Type switch {
                MasterFrameType.Bias     => $"{MasterFilePrefix}bias_{gainAndOffset}_{binning}_{temperature}",
                MasterFrameType.Flat     => $"{MasterFilePrefix}flat_{filter}_{binning}_{exposure}",
                MasterFrameType.DarkFlat => $"{MasterFilePrefix}darkflat_{darkParameters}",
                _                        => $"{MasterFilePrefix}dark_{darkParameters}"
            };
        }

        private static string FormatExposure(double seconds)
            => double.IsNaN(seconds) ? "NA" : seconds.ToString("0.###", CultureInfo.InvariantCulture);

        private static string FormatTemperature(MasterFrameDescriptor frame) {
            int? temperature = MatchingTemperature(frame);
            return temperature.HasValue ? $"{temperature.Value}C" : "NAC";
        }

        private static string Sanitize(string name) {
            StringBuilder builder = new StringBuilder(name.Length);
            foreach (char character in name) {
                bool isSafe = char.IsLetterOrDigit(character) || character == '-' || character == '_';
                builder.Append(isSafe ? character : '_');
            }
            return builder.ToString();
        }

        private static string Describe(FrameGroup group) {
            MasterFrameDescriptor representative = group.Representative;
            string size = $"{representative.Width}x{representative.Height}";
            string temperature = FormatTemperature(representative);
            string exposure = FormatExposure(representative.ExposureTimeSeconds);
            string gain = $"gain {representative.Gain}";
            return group.Type switch {
                MasterFrameType.Bias     => $"bias ({gain}, {size}, {temperature})",
                MasterFrameType.Flat     => $"flat (filter {representative.FilterName ?? "-"}, {size}, {exposure}s)",
                MasterFrameType.DarkFlat => $"dark-flat ({exposure}s, {temperature}, {gain})",
                _                        => $"dark ({exposure}s, {temperature}, {gain})"
            };
        }

        ///////////// Upload /////////////

        private async Task<bool> UploadAsync(MasterFrameDescriptor master, string savedPath, int sourceFrameCount,
            MasterIndex masterIndex, Action<string>? onStatus, CancellationToken cancellationToken) {
            string filename = Path.GetFileName(savedPath);

            if (string.IsNullOrWhiteSpace(master.CameraName)) {
                LogWarning($"Skipping upload of {filename}: no camera name in the frame headers.");
                return false;
            }

            int? temperature = MatchingTemperature(master);
            string? exposure = double.IsNaN(master.ExposureTimeSeconds)
                ? null : master.ExposureTimeSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            double? sensorTemperature = double.IsNaN(master.TemperatureCelsius) ? null : master.TemperatureCelsius;

            MasterUploadRequest request = new MasterUploadRequest {
                FrameType          = FrameTypeName(master.FrameType),
                CameraName         = master.CameraName!,
                BinningX           = master.BinX,
                BinningY           = master.BinY,
                Gain               = master.Gain,
                Offset             = master.Offset,
                ReadoutMode        = master.ReadoutMode,
                FileExtension      = "fits",
                SourceFrameCount   = sourceFrameCount,
                StackingMethod     = CombineMethod,
                SensorTemperatureC = sensorTemperature,
                ImageWidth         = master.Width,
                ImageHeight        = master.Height,
                BitDepth           = master.BitDepth,
                NinaProfileName    = Observatory.Settings.ProfileService.ActiveProfile?.Name
            };

            // Per-type fields. Sending one the type doesn't use gets a 400.
            switch (master.FrameType) {
                case MasterFrameType.Bias:
                    request.TemperatureC = temperature;
                    break;
                case MasterFrameType.Dark:
                case MasterFrameType.DarkFlat:
                    request.TemperatureC = temperature;
                    request.ExposureSeconds = exposure;
                    break;
                case MasterFrameType.Flat:
                    request.ExposureSeconds = exposure;
                    request.FilterName = master.FilterName;
                    break;
            }

            long fileSize;
            try { fileSize = new FileInfo(savedPath).Length; } catch { fileSize = 0; }
            request.ChecksumSha256 = await TryComputeSha256Async(savedPath, cancellationToken);

            Report(onStatus, $"Uploading {filename}...");

            // A 409 at complete means the PUT bytes didn't land, so we get a fresh URL and try once more.
            AstraeusWebClient client = Observatory.AstraeusWebClient;
            for (int attempt = 1; attempt <= MaxUploadAttempts; attempt++) {
                MasterUploadUrlResult? uploadTarget = await client.PostMasterUploadUrlAsync(request);
                if (uploadTarget == null) {
                    LogWarning($"No upload URL for {filename}.");
                    return false;
                }

                if (!await client.PutR2FileAsync(uploadTarget.UploadUrl, savedPath, cancellationToken)) {
                    LogWarning($"Upload PUT failed for {filename}.");
                    return false;
                }

                (bool isComplete, bool areBytesMissing, int? confirmedRevision) = await client.PostMasterCompleteAsync(
                    uploadTarget.MasterFrameId, fileSize, request.ChecksumSha256, sourceFrameCount, CombineMethod);
                if (isComplete) {
                    string action = uploadTarget.IsCreated ? "created" : "overwrote";
                    Log($"Uploaded master {filename} (id {uploadTarget.MasterFrameId}, {action}).");
                    RecordUploadedMaster(masterIndex, uploadTarget.MasterFrameId, confirmedRevision,
                        request.ChecksumSha256, savedPath);
                    return true;
                }
                if (!areBytesMissing) return false;
                LogWarning($"Complete reported bytes missing for {filename}; retrying (attempt {attempt}).");
            }
            return false;
        }

        // Masters are grouped, named and keyed the way their lights are matched, so the two agree.
        private static int? MatchingTemperature(MasterFrameDescriptor frame)
            => AcquisitionMetadata.MatchingTemperature(frame.TemperatureCelsius, frame.SetPointCelsius);

        // The index lets frames be calibrated here with this master, so a failure only costs that. We
        // use the confirmed revision because plans name it, and the upload-url reply's is one behind.
        private void RecordUploadedMaster(MasterIndex masterIndex, int masterFrameId, int? confirmedRevision,
            string? checksumSha256, string savedPath) {
            string fileName = Path.GetFileName(savedPath);
            if (confirmedRevision is not int revision) {
                LogWarning($"{fileName} is uploaded, but the server did not confirm its revision, so it " +
                           "cannot be used to calibrate on this PC. Generate it again to use it here.");
                return;
            }
            string? replacedPath;
            try {
                replacedPath = masterIndex.Record(masterFrameId, revision, checksumSha256, savedPath);
            } catch (Exception ex) {
                LogWarning($"Could not record {fileName} in the local master index: {ex.Message}");
                return;
            }
            DeleteReplacedMaster(replacedPath, savedPath);
        }

        // N.I.N.A. never overwrites, so a regenerated master lands beside the one it replaces. The
        // older file goes only once the index points at the new one.
        private void DeleteReplacedMaster(string? replacedPath, string savedPath) {
            if (replacedPath == null || !File.Exists(replacedPath)
                || string.Equals(Path.GetFullPath(replacedPath), Path.GetFullPath(savedPath),
                    StringComparison.OrdinalIgnoreCase)) {
                return;
            }
            try {
                File.Delete(replacedPath);
                Log($"Deleted {Path.GetFileName(replacedPath)}, replaced by {Path.GetFileName(savedPath)}.");
            } catch (Exception ex) {
                LogWarning($"Could not delete the replaced master {Path.GetFileName(replacedPath)}: {ex.Message}");
            }
        }

        private async Task<string?> TryComputeSha256Async(string path, CancellationToken cancellationToken) {
            try {
                await using FileStream stream = File.OpenRead(path);
                using SHA256 sha = SHA256.Create();
                byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken);
                return Convert.ToHexString(hash).ToLowerInvariant();
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogWarning($"Checksum failed for {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private void Report(Action<string>? onStatus, string message) {
            onStatus?.Invoke(message);
        }
    }

    /// <param name="IsSuccess">Every group became a master and every master reached the server.</param>
    public sealed record MasterGenerationResult(bool IsSuccess, bool WasCancelled, string Summary) {
        public static MasterGenerationResult Failure(string summary)
            => new MasterGenerationResult(IsSuccess: false, WasCancelled: false, Summary: summary);
    }
}