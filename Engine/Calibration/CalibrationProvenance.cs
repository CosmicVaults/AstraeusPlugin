using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>
    /// What happened when a frame was calibrated here. Sent with the upload-complete call so the
    /// capture's calibration page can show which masters were applied, or why none were. Nulls are
    /// left out when serialized, and the field lengths match the server's serializer.
    /// </summary>
    public sealed class CalibrationProvenance {
        private const int MaxWarningLength = 500;
        private const int MaxErrorLength = 4000;
        private const int MaxSkipReasonLength = 500;

        [JsonProperty("status")]             public string Status { get; init; } = "failed";   // completed | failed | skipped
        [JsonProperty("strategy")]           public string? Strategy { get; init; }
        [JsonProperty("master_bias_id")]     public int? MasterBiasId { get; init; }
        [JsonProperty("master_dark_id")]     public int? MasterDarkId { get; init; }
        [JsonProperty("master_flat_id")]     public int? MasterFlatId { get; init; }
        [JsonProperty("dark_scaling_ratio")] public double? DarkScalingRatio { get; init; }
        [JsonProperty("warnings")]           public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        [JsonProperty("error")]              public string? Error { get; init; }
        [JsonProperty("skip_reason")]        public string? SkipReason { get; init; }
        [JsonProperty("duration_seconds")]   public double? DurationSeconds { get; init; }

        internal static CalibrationProvenance From(CalibrationPlan plan, CalibrationOutcome outcome) => new() {
            Status = outcome.Status,
            Strategy = plan.Strategy,
            MasterBiasId = plan.Bias?.Id,
            MasterDarkId = plan.Dark?.Id,
            MasterFlatId = plan.Flat?.Id,
            DarkScalingRatio = plan.DarkScalingRatio,
            Warnings = outcome.Warnings.Select(warning => Clip(warning, MaxWarningLength)!).ToList(),
            Error = Clip(outcome.Error, MaxErrorLength),
            SkipReason = Clip(outcome.SkipReason, MaxSkipReasonLength),
            DurationSeconds = Math.Round(outcome.DurationSeconds, 2),
        };

        /// <summary>The same report, marked failed because the result never reached R2.</summary>
        public CalibrationProvenance AsFailed(string error) => new() {
            Status = "failed",
            Strategy = Strategy,
            MasterBiasId = MasterBiasId,
            MasterDarkId = MasterDarkId,
            MasterFlatId = MasterFlatId,
            DarkScalingRatio = DarkScalingRatio,
            Warnings = Warnings,
            Error = Clip(error, MaxErrorLength),
            DurationSeconds = DurationSeconds,
        };

        private static string? Clip(string? text, int maxLength) {
            if (text is null) return null;
            if (text.Length <= maxLength) return text;
            return text[..maxLength];
        }
    }
}
