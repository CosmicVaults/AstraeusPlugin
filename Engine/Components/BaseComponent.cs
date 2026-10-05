using NINA.Core.Model;
using NINA.Core.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    /// <summary>Shared base for device and service components. DeviceComponent adds the NINA device side.</summary>
    public abstract class BaseComponent(Observatory observatory, IWebSocketBus webSocketBus) : IComponent {
        
        public const string NoDevice = "None";
        public abstract string DeviceType { get; }
        public virtual bool IsBusy => false;
        public abstract LogCategory DefaultLogCategory { get; }

        /// <summary>One-time setup, before the first Start.</summary>
        public virtual Task Awake() => Task.CompletedTask;
        /// <summary>Overrides call the base.</summary>
        public virtual Task Start() {
            WebSocketBus.CommandReceived += OnCommandReceived;
            return Task.CompletedTask;
        }
        /// <summary>Called each observatory tick.</summary>
        public virtual Task Update() => Task.CompletedTask;
        /// <summary>Called each tick after Update.</summary>
        public virtual Task LateUpdate() => Task.CompletedTask;
        /// <summary>Overrides call the base last, which sets IsDestroyed.</summary>
        public virtual Task Destroy() {
            WebSocketBus.CommandReceived -= OnCommandReceived;
            IsDestroyed = true;
            return Task.CompletedTask;
        }
        public abstract WsMessage? GetUpdateMessage();

        protected IWebSocketBus WebSocketBus = webSocketBus;
        protected Observatory Observatory = observatory;

        /// <summary>Overrides return early on this, so a second Destroy can't throw on disposed state.</summary>
        protected bool IsDestroyed { get; private set; }

        /// <summary>
        /// Gets commands already filtered by device type. Override to intercept standard actions
        /// before HandleCommandAsync, and call base for anything unhandled.
        /// </summary>
        protected virtual Task RouteCommandAsync(WsCommand command) => HandleCommandAsync(command);

        /// <summary>Handles the component's own commands, the ones RouteCommandAsync doesn't know.</summary>
        protected abstract Task HandleCommandAsync(WsCommand command);

        protected async Task BroadcastMessageReceivedAsync(string? id, bool isAccepted, string? message = null) {
            var payload = new { status = isAccepted ? "accepted" : "rejected", message = message };
            await WebSocketBus.SendAsync(new WsResponse(DeviceType, id, "received", payload));
        }

        /// <summary>
        /// Logs a fault from an unawaited command handler so it isn't lost. Long motions run unawaited
        /// so they don't hold up the next command.
        /// </summary>
        protected void LogCommandFault(Exception exception) =>
            LogError($"A {DeviceType} command failed: {exception.Message}");

        /// <summary>Uses the invariant culture because some cultures swap the ':' separator or the calendar.</summary>
        protected static string UtcTimestampNow() =>
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        protected bool IsAutopilotRunning =>
            Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot) && autopilot.IsRunning;

        /// <summary>
        /// Rejects a command with a missing or mistyped field, so the dashboard hears why instead of
        /// waiting out its timeout.
        /// </summary>
        protected Task RejectMalformedCommandAsync(WsCommand command, string fieldName) =>
            BroadcastMessageReceivedAsync(command.Id, false,
                $"Malformed {command.Action} command: '{fieldName}' is missing or the wrong type");

        /// <summary>Mediators expect a non-null IProgress.</summary>
        protected static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>(_ => { });

        /// <summary>How long a push on connect waits, so it lands after the server's fetchDevices burst.</summary>
        protected static readonly TimeSpan PushOnConnectDelay = TimeSpan.FromSeconds(2);

        ///////////// Logging /////////////
        // All component logging goes through these so the device and category are always right.
        // Only pass device or category when a message belongs to another component, e.g. Mount's
        // plate-solve messages, which are logged under the autopilot.

        // The caller attributes are passed on so NINA's log names the method that logged, not this wrapper.

        protected void Log(string message, string? device = null, LogCategory? category = null,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Observatory.Log(message, device ?? DeviceType, category ?? DefaultLogCategory, member, file, line);

        protected void LogWarning(string message, string? device = null, LogCategory? category = null,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Observatory.LogWarning(message, device ?? DeviceType, category ?? DefaultLogCategory, member, file, line);

        protected void LogError(string message, string? device = null, LogCategory? category = null,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Observatory.LogError(message, device ?? DeviceType, category ?? DefaultLogCategory, member, file, line);

        protected void LogDebug(string message, string? device = null, LogCategory? category = null,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Observatory.LogDebug(message, device ?? DeviceType, category ?? DefaultLogCategory, member, file, line);

        private async void OnCommandReceived(WsCommand command) {
            if (!command.Device.Equals(DeviceType, StringComparison.OrdinalIgnoreCase)) return;
            try {
                await RouteCommandAsync(command);
            } catch (Exception ex) {
                LogError($"Unhandled exception handling {DeviceType}/{command.Action}: {ex.Message}");
            }
        }
    }
}