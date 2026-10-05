using Serilog;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using NINA.Core.Utility;
using System;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Components {
    public sealed class AstraeusLoggerComponent(Observatory observatory, IWebSocketBus webSocketBus)
        : ServiceComponent(observatory, webSocketBus) {

        public override string DeviceType => "system";
        public override LogCategory DefaultLogCategory => LogCategory.System;

        /// <summary>
        /// NINA's logger from before Start() wrapped it, so Destroy() can put it back. Serilog.Log.Logger
        /// is process-global, so just disposing our wrapper would leave NINA logging into a dead logger.
        /// </summary>
        private Serilog.ILogger? _previousLogger;

        private Serilog.ILogger? _wrappingLogger;

        public override Task Start() {
            _previousLogger = Serilog.Log.Logger;
            _wrappingLogger = new LoggerConfiguration()
                // Serilog defaults to Information, which would drop Debug and Verbose events before
                // NINA's own logger (the one that honours the Options log level) ever sees them.
                // Pass everything through and let NINA decide. The dashboard sink keeps its own
                // Information floor.
                .MinimumLevel.Verbose()
                .WriteTo.Logger(_previousLogger)
                .WriteTo.Sink(new AstraeusLogSink(WebSocketBus), Serilog.Events.LogEventLevel.Information)
                .CreateLogger();
            Serilog.Log.Logger = _wrappingLogger;
            return base.Start();
        }

        public override Task Destroy() {
            if (IsDestroyed) return Task.CompletedTask;
            // Only restore while ours is still installed. A plugin that wrapped after us writes through
            // ours, and putting NINA's back would drop its wrapper. Restore before disposing so nothing
            // logs into a disposed logger. WriteTo.Logger doesn't own NINA's logger, so its file sink
            // keeps running.
            if (_previousLogger != null && ReferenceEquals(Serilog.Log.Logger, _wrappingLogger)) {
                Serilog.Log.Logger = _previousLogger;
                (_wrappingLogger as IDisposable)?.Dispose();
            }
            _previousLogger = null;
            _wrappingLogger = null;
            return base.Destroy();
        }

        public override WsMessage? GetUpdateMessage() => null;
        protected override Task HandleCommandAsync(WsCommand command) => Task.CompletedTask;
    }
}