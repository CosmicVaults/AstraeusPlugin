using NINA.Equipment.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Components {
    public interface IDeviceComponent : IComponent {
        bool IsConnected { get; }
        string DisplayName { get; }
        /// <summary>Id of the default device. Null or "None" means none chosen.</summary>
        string? DefaultDevice { get; }

        /// <summary>Logs the change with its source, e.g. "options page". Null or empty saves "None".</summary>
        void SetDefaultDevice(string? deviceId, string source);

        /// <summary>True when a real device is selected, so this component is in use.</summary>
        bool HasDefaultDevice { get; }

        event EventHandler? DefaultDeviceChanged;

        IList<IDevice> GetAvailableDevices();

        string? ConnectedDeviceId { get; }

        bool IsConnectedToDevice(string deviceId);

        Task<bool> TryConnectAsync(string deviceId);
        
        Task<bool> TryConnectDefaultDeviceAsync();

        /// <summary>
        /// Presses N.I.N.A.'s own Cancel on a connect in progress. False when there's none to press. Never throws.
        /// </summary>
        bool TryCancelConnect();

        /// <summary>Disconnects whatever is connected. True once nothing is. Never throws.</summary>
        Task<bool> TryDisconnectAsync();
    }
}
