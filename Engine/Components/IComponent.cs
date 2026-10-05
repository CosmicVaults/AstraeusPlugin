using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public interface IComponent {
        string DeviceType { get; }
        bool IsBusy { get; }
        Task Awake();
        Task Start();
        Task Update();
        Task LateUpdate();
        Task Destroy();
        WsMessage? GetUpdateMessage();
    }
}