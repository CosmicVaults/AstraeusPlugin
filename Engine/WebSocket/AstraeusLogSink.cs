using System;
using Serilog.Core;
using Serilog.Events;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket {
    /// <summary>
    /// Mirrors N.I.N.A.'s log at Information and above onto the dashboard. LocalLog lines are skipped, since
    /// they're transport chatter or already sent as notifications. A token bucket stops a driver stuck in an
    /// error loop from flooding the socket.
    /// </summary>
    public class AstraeusLogSink(IWebSocketBus webSocketBus) : ILogEventSink {
        private const double Burst = 30;
        private const double RefillPerSecond = 1.0;

        private readonly object _gate = new();
        private double _tokens = Burst;
        private DateTime _refilledAt = DateTime.UtcNow;
        private int _suppressedCount;

        public void Emit(LogEvent logEvent) {
            if (LocalLog.IsSuppressed) return;
            if (logEvent.Level < LogEventLevel.Information) return;

            string rendered = logEvent.RenderMessage();
            // RenderMessage() returns: "SOURCE"|"MEMBER"|LINE|"MESSAGE"
            string[] parts = rendered.Split('|');
            if (parts.Length < 4) return;

            string source  = parts[0].Trim('"');
            string message = UserProfilePath.Redact(string.Join("|", parts[3..]).Trim('"'));

            string severity = logEvent.Level switch {
                LogEventLevel.Warning => "WARNING",
                LogEventLevel.Error   => "ERROR",
                LogEventLevel.Fatal   => "FATAL",
                _                     => "INFO"
            };

            int suppressedCount;
            lock (_gate) {
                Refill();
                if (_tokens < 1) {
                    _suppressedCount++;
                    return;
                }
                _tokens -= 1;
                suppressedCount = _suppressedCount;
                _suppressedCount = 0;
            }

            if (suppressedCount > 0) {
                webSocketBus.SendAsync(new WsNotification(
                    device:        "nina",
                    category:      "SYSTEM",
                    message:       $"{suppressedCount} N.I.N.A. log line(s) were not forwarded (rate limit). " +
                                   "See N.I.N.A.'s log file.",
                    severity:      "WARNING",
                    shouldTimeout: true,
                    isToast:       false
                )).ObserveFaults(ReportForwardFailure);
            }

            webSocketBus.SendAsync(new WsNotification(
                device:        source,
                category:      "SYSTEM",
                message:       message,
                severity:      severity,
                shouldTimeout: true,
                isToast:       logEvent.Level >= LogEventLevel.Error
            )).ObserveFaults(ReportForwardFailure);
        }

        // LocalLog, so the report isn't forwarded down the socket that just failed.
        private static void ReportForwardFailure(Exception exception)
            => LocalLog.Warning($"Could not forward a N.I.N.A. log line to the dashboard: {exception.Message}");

        // Called under the gate.
        private void Refill() {
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - _refilledAt).TotalSeconds;
            if (elapsed <= 0) return;
            _tokens = Math.Min(Burst, _tokens + elapsed * RefillPerSecond);
            _refilledAt = now;
        }
    }
}
