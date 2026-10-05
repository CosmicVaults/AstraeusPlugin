using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CosmicVaults.NINA.Astraeus.Engine.Utility;

namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>
    /// A master the server picked for a frame, from the calibration.masters block of a capture
    /// registration. We look the file up in MasterIndex and never download it.
    /// </summary>
    internal sealed record MasterRef(int Id, int Revision, string? ChecksumSha256) {

        /// <summary>Null when it's missing or malformed.</summary>
        public static MasterRef? Parse(JToken? token) {
            if (token is not JObject master) return null;
            int? id = ResponseFields.Int(master, "id");
            if (id is null) return null;
            string? checksum = (string?)master["checksum_sha256"];
            return new MasterRef(
                Id: id.Value,
                Revision: ResponseFields.Int(master, "revision") ?? 0,
                ChecksumSha256: string.IsNullOrEmpty(checksum) ? null : checksum);
        }
    }

    /// <summary>
    /// The server's plan for calibrating one light frame, returned when the frame is registered by
    /// the autopilot or by a manual capture with Calibrate on. The server decides because it owns
    /// the master index. The pixel work happens here in FrameCalibrator.
    /// </summary>
    internal sealed record CalibrationPlan(
        string Strategy, double? DarkScalingRatio, bool ShouldKeepRaw, IReadOnlyList<string> Warnings,
        MasterRef? Bias, MasterRef? Dark, MasterRef? Flat) {

        /// <summary>The masters in the plan with their role names, in the order they're applied.</summary>
        public IEnumerable<(string Role, MasterRef Master)> Roles {
            get {
                if (Bias != null) yield return ("bias", Bias);
                if (Dark != null) yield return ("dark", Dark);
                if (Flat != null) yield return ("flat", Flat);
            }
        }

        /// <summary>Returns null when the block is missing or names no masters.</summary>
        public static CalibrationPlan? Parse(JToken? token) {
            if (token is not JObject calibration) return null;
            JObject? masters = calibration["masters"] as JObject;
            CalibrationPlan plan = new CalibrationPlan(
                Strategy: (string?)calibration["strategy"] ?? "none",
                DarkScalingRatio: ResponseFields.FiniteDouble(calibration, "dark_scaling_ratio"),
                ShouldKeepRaw: ResponseFields.Bool(calibration, "keep_raw") ?? true,
                Warnings: ParseWarnings(calibration["warnings"]),
                Bias: MasterRef.Parse(masters?["bias"]),
                Dark: MasterRef.Parse(masters?["dark"]),
                Flat: MasterRef.Parse(masters?["flat"]));
            return plan.Roles.Any() ? plan : null;
        }

        private static List<string> ParseWarnings(JToken? token) {
            List<string> warnings = new List<string>();
            if (token is not JArray warningArray) return warnings;
            foreach (JToken item in warningArray) {
                string? warning = (string?)item;
                if (!string.IsNullOrEmpty(warning)) warnings.Add(warning);
            }
            return warnings;
        }

        /// <summary>Log line like "scaled_dark x2.5 (bias #4, dark #12, flat #7), keep raw".</summary>
        public string Describe() {
            string masters = string.Join(", ", Roles.Select(role => $"{role.Role} #{role.Master.Id}"));
            string scaling = DarkScalingRatio is double ratio ? $" x{ratio:0.##}" : "";
            string rawHandling = ShouldKeepRaw ? "keep raw" : "calibrated only";
            return $"{Strategy}{scaling} ({masters}), {rawHandling}";
        }
    }
}
