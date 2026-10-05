using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Weather(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        IWeatherDataMediator weatherMediator)
        : DeviceComponent<IWeatherDataVM, IWeatherDataConsumer, WeatherDataInfo>(
            weatherMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "weather";
        public override LogCategory DefaultLogCategory => LogCategory.Weather;
        public override string DisplayName { get; } = "Weather";
     
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(55);
        private const int ReadingDecimalPlaces = 2;
        private DateTime _lastSampleAt = DateTime.MinValue;

        /// <summary>Null when the station has no such sensor (NaN).</summary>
        private static double? Reading(double? value) {
            double? finiteValue = DevicePayload.CleanValue(value);
            if (finiteValue == null) return null;
            return Math.Round(finiteValue.Value, ReadingDecimalPlaces);
        }

        private WeatherPayload? BuildReadingPayload() {
            if (LastInfo is null) {
                return null;
            }
            return new WeatherPayload {
                DeviceId = DeviceId,
                CloudCover = Reading(LastInfo.CloudCover),
                DewPoint = Reading(LastInfo.DewPoint),
                Humidity = Reading(LastInfo.Humidity),
                Pressure = Reading(LastInfo.Pressure),
                RainRate = Reading(LastInfo.RainRate),
                SkyBrightness = Reading(LastInfo.SkyBrightness),
                SkyQuality = Reading(LastInfo.SkyQuality),
                SkyTemperature = Reading(LastInfo.SkyTemperature),
                StarFWHM = Reading(LastInfo.StarFWHM),
                Temperature = Reading(LastInfo.Temperature),
                WindDirection = Reading(LastInfo.WindDirection),
                WindGust = Reading(LastInfo.WindGust),
                WindSpeed = Reading(LastInfo.WindSpeed),
                IsConnected = true
            };
        }

        public override WsMessage? GetUpdateMessage() {
            WeatherPayload? payload = BuildReadingPayload();
            return payload is null ? null : new WeatherUpdate(payload, "dashboard");
        }

        /// <summary>
        /// Files one reading a minute on the durable path, since GetUpdateMessage only runs while a browser
        /// tab is open. The socket check is here because Observatory.LateUpdate doesn't check it.
        /// </summary>
        public override async Task LateUpdate() {
            // Before base.LateUpdate(), which nulls LastInfo.
            if (Observatory.IsSocketOpen && LastInfo is not null && IsConnected
                && DateTime.UtcNow - _lastSampleAt >= SampleInterval) {
                if (BuildReadingPayload() is { } payload) {
                    _lastSampleAt = DateTime.UtcNow;
                    await WebSocketBus.SendAsync(new WeatherUpdate(payload, "event"));
                }
            }
            await base.LateUpdate();
        }

        public override WsMessage? GetDeviceStaticInfo() {
            WeatherDataInfo info = weatherMediator.GetInfo();
            WeatherPayload payload = new WeatherPayload {
                DeviceId = info.DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                AveragePeriod = DevicePayload.CleanValue(info.AveragePeriod),
            };
            return new WeatherUpdate(payload);
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Current ambient temperature in °C, or null when no connected sensor reports one. Used as the
        /// autofocus temperature-change fallback when the focuser has no temperature probe.
        /// </summary>
        public double? GetTemperature() {
            WeatherDataInfo? info = weatherMediator.GetInfo();
            if (info is not { Connected: true }) return null;
            return double.IsNaN(info.Temperature) ? null : info.Temperature;
        }

        /// <summary>
        /// The station's readings for an alert. Reads the mediator because LastInfo is empty outside a
        /// tick and while the socket is down.
        /// </summary>
        public Dictionary<string, double>? Snapshot() {
            WeatherDataInfo? info = weatherMediator.GetInfo();
            if (info is not { Connected: true }) return null;
            var readings = new Dictionary<string, double>();
            void Add(string key, double value) {
                if (Reading(value) is { } reading) readings[key] = reading;
            }
            Add("temperature", info.Temperature);
            Add("humidity", info.Humidity);
            Add("dew_point", info.DewPoint);
            Add("cloud_cover", info.CloudCover);
            Add("wind_speed", info.WindSpeed);
            Add("wind_gust", info.WindGust);
            Add("rain_rate", info.RainRate);
            Add("sky_quality", info.SkyQuality);
            return readings;
        }
    }
}