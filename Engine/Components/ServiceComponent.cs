using CosmicVaults.NINA.Astraeus.Engine.WebSocket;

namespace CosmicVaults.NINA.Astraeus.Engine {
    /// <summary>Base for components with no NINA device behind them, e.g. Settings.</summary>
    public abstract class ServiceComponent(Observatory observatory, IWebSocketBus webSocketBus)
        : BaseComponent(observatory, webSocketBus) {
    }
}