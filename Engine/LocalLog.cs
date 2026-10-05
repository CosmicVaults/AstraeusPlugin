using NINA.Core.Utility;
using System;
using System.Runtime.CompilerServices;

namespace CosmicVaults.NINA.Astraeus.Engine {
    /// <summary>
    /// Writes to NINA's log file only, so AstraeusLogSink doesn't mirror it to the dashboard. For lines
    /// about the socket itself, or already sent as a typed notification. Serilog calls sinks synchronously,
    /// so a thread-static flag is enough.
    /// </summary>
    internal static class LocalLog {
        [ThreadStatic] private static bool _isWriting;

        public static bool IsSuppressed => _isWriting;

        public static void Debug(string message,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Write(() => Logger.Debug(message, member, file, line));

        public static void Info(string message,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Write(() => Logger.Info(message, member, file, line));

        public static void Warning(string message,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Write(() => Logger.Warning(message, member, file, line));

        public static void Error(string message,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
            => Write(() => Logger.Error(message, member, file, line));

        private static void Write(Action write) {
            bool wasWriting = _isWriting;
            _isWriting = true;
            try {
                write();
            } finally {
                _isWriting = wasWriting;
            }
        }
    }
}
