using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _alpacaSettingsPush;

        private async Task HandleAlpacaUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            IAlpacaSettings alpacaSettings = Profile!.AlpacaSettings;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .Int("number_of_polls", value => alpacaSettings.NumberOfPolls = value)
                .Int("poll_interval", value => alpacaSettings.PollInterval = value)
                .Int("discovery_port", value => alpacaSettings.DiscoveryPort = value)
                .Double("discovery_duration", value => alpacaSettings.DiscoveryDuration = value)
                .Bool("resolve_dns_name", value => alpacaSettings.ResolveDnsName = value)
                .Bool("use_ipv4", value => alpacaSettings.UseIPv4 = value)
                .Bool("use_ipv6", value => alpacaSettings.UseIPv6 = value)
                .Bool("use_https", value => alpacaSettings.UseHttps = value);
            if (!await TryApplyServerUpdateAsync(update, "Alpaca", PushAlpacaSettingsAsync)) return;

            Log($"Alpaca settings updated via WS: port={alpacaSettings.DiscoveryPort}, " +
                $"duration={alpacaSettings.DiscoveryDuration}");
        }

        internal Task PushAlpacaSettingsAsync() {
            IAlpacaSettings alpacaSettings = Profile!.AlpacaSettings;
            AlpacaSettingsPayload payload = new AlpacaSettingsPayload {
                NumberOfPolls = alpacaSettings.NumberOfPolls,
                PollInterval = alpacaSettings.PollInterval,
                DiscoveryPort = alpacaSettings.DiscoveryPort,
                DiscoveryDuration = alpacaSettings.DiscoveryDuration,
                ResolveDnsName = alpacaSettings.ResolveDnsName,
                UseIPv4 = alpacaSettings.UseIPv4,
                UseIPv6 = alpacaSettings.UseIPv6,
                UseHttps = alpacaSettings.UseHttps
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "alpaca"));
        }

        private void OnAlpacaSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _alpacaSettingsPush ??= CreatePush(PushAlpacaSettingsAsync, "Alpaca");
            _alpacaSettingsPush.Schedule();
        }
    }
}
