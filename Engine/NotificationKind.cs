namespace CosmicVaults.NINA.Astraeus.Engine {
    // The server rejects an unknown kind with a 400. The web client logs a warning and the alert never
    // arrives.
    internal static class NotificationKind {
        public const string RoofOpened       = "roof_opened";
        public const string RoofClosed       = "roof_closed";
        public const string AutopilotStarted = "autopilot_started";
        public const string AutopilotStopped = "autopilot_stopped";
        public const string AutopilotFailed  = "autopilot_failed";
        public const string CriticalError    = "critical_error";
    }
}
