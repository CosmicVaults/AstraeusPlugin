using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public record DevicePayload {
        public string? Name { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }
        [JsonPropertyName("connected")]
        public bool? IsConnected { get; init; }
        [JsonPropertyName("display_name")]
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        [JsonPropertyName("driver_info")]
        public string? DriverInfo { get; init; }
        [JsonPropertyName("driver_version")]
        public string? DriverVersion { get; init; }

        /// <summary>
        /// Turns NaN and infinity into null, never 0. N.I.N.A. reports a missing reading as NaN, and a
        /// made-up zero would look real on the dashboard.
        /// </summary>
        public static double? CleanValue(double? value) {
            if (value is not { } number) return null;
            return double.IsFinite(number) ? number : null;
        }
    }
}