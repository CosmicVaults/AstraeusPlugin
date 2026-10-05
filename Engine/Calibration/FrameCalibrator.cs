using NINA.Core.Enum;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>
    /// The result of calibrating one frame. OutputPath is the calibrated file when it worked. If not,
    /// SkipReason says there was nothing to calibrate with (e.g. a master that isn't on this PC), or
    /// Error says the arithmetic or the write failed.
    /// </summary>
    internal sealed record CalibrationOutcome(
        string? OutputPath, string? Error, string? SkipReason, double DurationSeconds, IReadOnlyList<string> Warnings) {
        public bool IsSuccess => OutputPath != null;

        public string Status {
            get {
                if (IsSuccess) return "completed";
                if (SkipReason != null) return "skipped";
                return "failed";
            }
        }
    }

    /// <summary>
    /// Applies the server's chosen masters to one light frame on this PC. Never throws except on
    /// cancellation.
    ///
    /// Standard reduction in double precision, written back as 16-bit integers with a pedestal so
    /// the background noise never clips at zero:
    ///
    ///   additive = matched dark                      (exposure_matched_dark)
    ///            = bias + (dark - bias) * ratio       (scaled_dark)
    ///            = bias                               (bias_only)
    ///   flatNorm = median(flat - bias)                sampled, one scalar for the whole frame
    ///   result   = round((light - additive) / ((flat - bias) / flatNorm) + PEDESTAL)
    ///
    /// Our masters are raw stacked ADU, so the flat still has the bias in it. We take it out when the
    /// plan has a bias, and the flat is normalised to 1 at its median. A flat pixel below FlatMin
    /// counts as 1 so we never divide by nothing.
    ///
    /// Files are read and written through N.I.N.A., so the calibrated copy gets the profile's file
    /// type and compression, same as the raw.
    /// </summary>
    internal sealed class FrameCalibrator(Observatory observatory) {
        /// <summary>ADU added to every calibrated pixel. The PEDESTAL header records it for removal.</summary>
        public const int Pedestal = 1000;
        private const double FlatMin = 0.001;
        private const int FlatSampleStride = 16;
        private const string CalibratedSuffix = "_cal";
        private const int ReadBitDepth = 16;

        public async Task<CalibrationOutcome> CalibrateAsync(string lightPath, CalibrationPlan plan,
            CancellationToken cancellationToken) {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<string> warnings = new List<string>(plan.Warnings);
            try {
                string? mastersFolder = MasterIndex.ResolveMastersFolder(observatory.Settings);
                if (mastersFolder == null)
                    return Skipped("No calibration masters folder is set on this PC.", stopwatch, warnings);
                MasterIndex masterIndex = new MasterIndex(mastersFolder);
                Dictionary<string, string> masterPaths = new Dictionary<string, string>();
                foreach ((string role, MasterRef master) in plan.Roles) {
                    string? path = masterIndex.FindLocalPath(master, out string? missingReason);
                    if (path is null) {
                        string reason = $"Master {role} #{master.Id} r{master.Revision} is not usable here: " +
                                        $"{missingReason}.";
                        return Skipped(reason, stopwatch, warnings);
                    }
                    masterPaths[role] = path;
                }

                // NINA's reader gives 16-bit unsigned planes whatever the file format.
                IImageDataFactory factory = observatory.ImageDataFactory;
                IImageData light = await factory.CreateFromFile(lightPath, bitDepth: ReadBitDepth, isBayered: false,
                    RawConverterEnum.FREEIMAGE, cancellationToken);
                ushort[] lightPixels = light.Data?.FlatArray
                    ?? throw new InvalidOperationException("The light frame has no pixel data.");
                int width = light.Properties.Width, height = light.Properties.Height;

                async Task<ushort[]?> LoadAsync(string role) {
                    if (!masterPaths.TryGetValue(role, out string? path)) return null;
                    IImageData image = await factory.CreateFromFile(path, bitDepth: ReadBitDepth, isBayered: false,
                        RawConverterEnum.FREEIMAGE, cancellationToken);
                    ushort[] pixels = image.Data?.FlatArray
                        ?? throw new InvalidOperationException($"Master {role} has no pixel data.");
                    if (image.Properties.Width != width || image.Properties.Height != height)
                        throw new InvalidOperationException(
                            $"Master {role} is {image.Properties.Width}x{image.Properties.Height}, " +
                            $"the light is {width}x{height}.");
                    return pixels;
                }
                ushort[]? bias = await LoadAsync("bias");
                ushort[]? dark = await LoadAsync("dark");
                ushort[]? flat = await LoadAsync("flat");

                double flatNorm = 1.0;
                ushort[] calibrated = await Task.Run(
                    () => Apply(lightPixels, bias, dark, flat, plan.DarkScalingRatio, warnings, out flatNorm),
                    cancellationToken);

                ImageMetaData metadata = light.MetaData;
                AddCalibrationHeaders(metadata, plan, flat != null, flatNorm);

                BaseImageData image = factory.CreateBaseImageData(calibrated, width, height, light.Properties.BitDepth,
                    light.Properties.IsBayered, metadata);
                string directory = Path.GetDirectoryName(lightPath) ?? ".";
                string stem = Stem(lightPath) + CalibratedSuffix;
                RemoveStaleOutput(directory, stem);
                IProfileService profile = observatory.Settings.ProfileService;
                FileSaveInfo saveInfo = new FileSaveInfo(profile) {
                    FilePath = directory,
                    FilePattern = stem,
                    FileType = profile.ActiveProfile?.ImageFileSettings.FileType ?? FileTypeEnum.FITS,
                };
                string savedPath = await image.SaveToDisk(saveInfo, cancellationToken, forceFileType: true);

                return new CalibrationOutcome(savedPath, null, null, stopwatch.Elapsed.TotalSeconds, warnings);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                return new CalibrationOutcome(null, ex.Message, null, stopwatch.Elapsed.TotalSeconds, warnings);
            }
        }

        private static void AddCalibrationHeaders(ImageMetaData metadata, CalibrationPlan plan, bool hasFlat,
            double flatNorm) {
            List<IGenericMetaDataHeader> headers = metadata.GenericHeaders;
            headers.Add(new StringMetaDataHeader("CREATOR", "Astraeus", "Calibrated by Astraeus"));
            headers.Add(new StringMetaDataHeader("CALSTRAT", plan.Strategy, "Astraeus calibration strategy"));
            if (plan.Bias != null) headers.Add(new IntMetaDataHeader("MBIAS", plan.Bias.Id, "Astraeus master bias id"));
            if (plan.Dark != null) headers.Add(new IntMetaDataHeader("MDARK", plan.Dark.Id, "Astraeus master dark id"));
            if (plan.Flat != null) headers.Add(new IntMetaDataHeader("MFLAT", plan.Flat.Id, "Astraeus master flat id"));
            if (plan.DarkScalingRatio is double ratio)
                headers.Add(new DoubleMetaDataHeader("DARKSCAL", ratio, "Dark scaling ratio (light/dark exposure)"));
            if (hasFlat)
                headers.Add(new DoubleMetaDataHeader("FLATNORM", flatNorm, "Flat normalisation (median ADU)"));
            headers.Add(new IntMetaDataHeader("PEDESTAL", Pedestal, "ADU added after calibration"));
            headers.Add(new StringMetaDataHeader("ASTRDATE",
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "Astraeus calibration time (UTC)"));
        }

        private static CalibrationOutcome Skipped(string reason, Stopwatch stopwatch, List<string> warnings)
            => new CalibrationOutcome(null, null, reason, stopwatch.Elapsed.TotalSeconds, warnings);

        private static ushort[] Apply(ushort[] light, ushort[]? bias, ushort[]? dark, ushort[]? flat,
            double? darkScalingRatio, List<string> warnings, out double flatNorm) {
            int count = light.Length;
            flatNorm = 1.0;

            if (flat != null) {
                List<double> samples = new List<double>(count / FlatSampleStride + 1);
                for (int i = 0; i < count; i += FlatSampleStride)
                    samples.Add(flat[i] - (bias?[i] ?? 0));
                samples.Sort();
                flatNorm = samples[samples.Count / 2];
                if (flatNorm <= 0) {
                    warnings.Add("Master flat has no signal after bias subtraction; flat correction skipped.");
                    flat = null;
                    flatNorm = 1.0;
                }
            }

            bool shouldScaleDark = dark != null && bias != null && darkScalingRatio is double;
            double ratio = darkScalingRatio ?? 1.0;
            ushort[] result = new ushort[count];
            int clippedFlat = 0;
            for (int i = 0; i < count; i++) {
                double additive = 0;
                if (dark != null)
                    additive = shouldScaleDark ? bias![i] + (dark[i] - bias[i]) * ratio : dark[i];
                else if (bias != null)
                    additive = bias[i];

                double value = light[i] - additive;
                if (flat != null) {
                    double gain = (flat[i] - (bias?[i] ?? 0)) / flatNorm;
                    if (gain < FlatMin) {
                        gain = 1.0;
                        clippedFlat++;
                    }
                    value /= gain;
                }

                value = Math.Round(value + Pedestal);
                result[i] = ClampToUShort(value);
            }
            if (clippedFlat > 0)
                warnings.Add($"{clippedFlat} flat pixel(s) below {FlatMin} were treated as unity.");
            return result;
        }

        private static ushort ClampToUShort(double value) {
            if (value <= 0) return 0;
            if (value >= ushort.MaxValue) return ushort.MaxValue;
            return (ushort)value;
        }

        /// <summary>"LIGHT_0001" from LIGHT_0001.fits, LIGHT_0001.xisf or LIGHT_0001.fits.fz.</summary>
        private static string Stem(string path) {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".fz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
            return Path.GetFileNameWithoutExtension(name);
        }

        // A retried report can leave an earlier output behind. N.I.N.A. would then save beside it under
        // a numbered name, and the upload would pick that up.
        private static void RemoveStaleOutput(string directory, string stem) {
            try {
                foreach (string stale in Directory.EnumerateFiles(directory, stem + ".*"))
                    File.Delete(stale);
            } catch { /* the save reports its own problems */ }
        }
    }
}
