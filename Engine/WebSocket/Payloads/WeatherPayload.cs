using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record WeatherUpdate : WsMessage {
        public WeatherUpdate(WeatherPayload payload, string? context = null)
            : base(
                type: "update",
                device: "weather",
                context: context,
                payload: ToJsonElement(new DataPayload<WeatherPayload>(payload))
            ) { }
    }

    public record WeatherPayload : DevicePayload {
        [JsonPropertyName("average_period")]
        public double? AveragePeriod { get; init; }

        [JsonPropertyName("cloud_cover")]
        public double? CloudCover { get; init; }

        [JsonPropertyName("dew_point")]
        public double? DewPoint { get; init; }

        public double? Humidity { get; init; }

        public double? Pressure { get; init; }

        [JsonPropertyName("rain_rate")]
        public double? RainRate { get; init; }

        [JsonPropertyName("sky_brightness")]
        public double? SkyBrightness { get; init; }

        [JsonPropertyName("sky_quality")]
        public double? SkyQuality { get; init; }

        // Not "rain_temperature". Nothing on the server or dashboard reads that key, so the Sky Temp
        // line would stay blank.
        [JsonPropertyName("sky_temperature")]
        public double? SkyTemperature { get; init; }

        [JsonPropertyName("star_FWHM")]
        public double? StarFWHM { get; init; }

        public double? Temperature { get; init; }

        [JsonPropertyName("wind_direction")]
        public double? WindDirection { get; init; }

        [JsonPropertyName("wind_gust")]
        public double? WindGust { get; init; }

        [JsonPropertyName("wind_speed")]
        public double? WindSpeed { get; init; }
    }
}