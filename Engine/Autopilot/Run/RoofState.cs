namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// What the run knows about the roof: whether the autopilot is working it, whether it has been seen
    /// open for this imaging stretch, and whether the autopilot opened it tonight. Changed through the
    /// events that change them, and replaced with the night. The setters stay so a test can describe a
    /// night in progress.
    /// </summary>
    internal sealed class RoofState
    {
        /// <summary>
        /// True from waiting for safe conditions until a secure finishes. The Dome's roof watchdog stands
        /// down while it's set, and covers the other phases, which have no safety watcher.
        /// </summary>
        public bool IsUnderAutopilotControl { get; set; }

        /// <summary>
        /// Whether the roof has been seen open for this imaging stretch, so a roof that stops reading
        /// open is a surprise, not one that hasn't opened yet. Never set under RoofMode.Ignore.
        /// </summary>
        public bool IsExpectedOpen { get; set; }

        /// <summary>
        /// Whether this autopilot opened the roof tonight. The secure closes a roof it opened even if
        /// the roof mode has changed from Operate since.
        /// </summary>
        public bool HasOpenedTonight { get; set; }

        // A roof that won't open fails the same way every pass, all night, with only an ERROR line each
        // time. The third failure in a row earns one alert. Unsafe conditions aren't counted, since
        // waiting those out is normal.
        private const int FailedOpensBeforeAlert = 3;

        /// <summary>Failed opens in a row tonight, the park N.I.N.A. needs first included. An open resets it.</summary>
        public int FailedOpenCount { get; private set; }

        /// <summary>Counts a failed open and returns true when it is the one that earns the alert.</summary>
        public bool RecordFailedOpen()
        {
            FailedOpenCount++;
            return FailedOpenCount == FailedOpensBeforeAlert;
        }

        public void RecordOpened() => FailedOpenCount = 0;

        /// <summary>
        /// Waiting for safe conditions: under Operate the autopilot works the roof from here, and nothing
        /// has been seen open yet.
        /// </summary>
        public void TakeControl(RoofMode mode)
        {
            IsUnderAutopilotControl = mode == RoofMode.Operate;
            IsExpectedOpen = false;
        }

        /// <summary>
        /// An open is under way, so the roof is not open until it reads open. Otherwise the roof watcher
        /// would mistake a roof still opening for one that closed mid-night.
        /// </summary>
        public void Opening() => IsExpectedOpen = false;

        /// <summary>The roof reads open. From here the roof watcher treats it closing again as a surprise.</summary>
        public void SeenOpen(bool didAutopilotOpenIt)
        {
            IsExpectedOpen = true;
            if (didAutopilotOpenIt) HasOpenedTonight = true;
        }

        /// <summary>A secure or stop has finished with the roof. The Dome's watchdog takes it back from here.</summary>
        public void Released()
        {
            IsUnderAutopilotControl = false;
            IsExpectedOpen = false;
        }
    }
}
