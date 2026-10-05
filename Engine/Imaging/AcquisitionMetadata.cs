using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// Normalises the acquisition settings the server uses to match a light to its masters. The capture
    /// path and the master generator both use these, so their rules can't drift apart.
    /// </summary>
    internal static class AcquisitionMetadata {
        /// <summary>
        /// Maps the -1 N.I.N.A. reports for an unexposed gain or offset to 0. Fixed-gain CCDs never report
        /// one, and the server rejects negatives, so 0 keeps a light matched to its masters.
        /// </summary>
        public static int NormalizeGainOrOffset(int value) => Math.Max(0, value);

        public static int? NormalizeGainOrOffset(int? value) =>
            value is { } reportedValue ? Math.Max(0, reportedValue) : (int?)null;

        public static bool IsMissing(int value) => value < 0;

        /// <summary>
        /// The sensor reading, or the set point if there's none, rounded away from zero like the web side.
        /// The reading wins because a set point can be stale (some camera drivers reset it to 0 on
        /// connect), and a cooler short of its target isn't at it.
        /// </summary>
        public static int? MatchingTemperature(double sensorTemperature, double setPoint) {
            double reading = double.IsFinite(sensorTemperature) ? sensorTemperature : setPoint;
            return double.IsFinite(reading) ? (int)Math.Round(reading, MidpointRounding.AwayFromZero) : (int?)null;
        }

        public static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
