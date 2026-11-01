using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>The timing and limit constants the autopilot runs on. Settings the observer can change live are on Context.</summary>
    internal static class AutopilotConstants
    {
        public const int DefaultRetryDelaySeconds = 60;
        public const int SafetyPollIntervalSeconds = 30;
        // Faster while the safe settle runs, so we resume close to when it ends.
        public const int SafetySettlePollIntervalSeconds = 10;

        public const int SafetyWatchIntervalSeconds = 5;

        // How long a watcher can go before a failed driver reading triggers the watcher. Allows for brief drops by the drivers.
        public const int WatcherBlindGraceSeconds = 30;

        // A longer silence between blind readings ends a watcher's blind spell.
        public static readonly TimeSpan WatcherReadingGap = TimeSpan.FromSeconds(60);

        // Slewing, centring and focusing come before the shutter opens, so the limit look-ahead adds
        // this to the exposure. The server has its own setup margin and the two may drift apart, but
        // this is the one that decides whether the mount actually goes anywhere.
        public const int LimitLookAheadMarginSeconds = 300;

        // A target is only committed to when the prediction puts it clear of the altitude floor by this
        // much. The mount's own reading, which the live watcher goes by, differs from a prediction by
        // refraction and pointing error, so a target on the floor would pass the checks and trip the watcher.
        public const double LimitAltitudeMarginDegrees = 1.0;

        // Assumed exposure when a target has none, just so the look-ahead has a length.
        public const double DefaultExposureSeconds = 60.0;

        // The most the server may ask of one frame. Anything past these is a server fault, not a plan,
        // and CaptureImage reports the frame failed instead of handing it to the camera.
        public const double MaxExposureSeconds = 7200.0;
        public const short MaxBinning = 4;

        // How many times a secure tries to close the roof, and the pause between tries. N.I.N.A.
        // reports a close as done whatever the shutter did, so we read the shutter back after each
        // try. A driver stuck on "closing" gets another go before anyone is emailed.
        public const int RoofCloseAttempts = 3;
        public const int RoofCloseRetrySeconds = 30;

        // How often WaitForSafe has another go at a roof the secure could not close.
        public static readonly TimeSpan RoofRecloseCooldown = TimeSpan.FromMinutes(5);

        // How long to leave a device that wouldn't reconnect before trying again. The waits poll
        // every 30 seconds, and a missing driver shouldn't be asked twice a minute all night.
        public static readonly TimeSpan ReconnectCooldown = TimeSpan.FromMinutes(5);

        // How long one reconnect gets before we press N.I.N.A.'s own Cancel on it. N.I.N.A. waits
        // on a driver's connect with no time limit.
        public static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(60);

        // The most a secure may take (see States.SecureObservatory). It covers two parks at N.I.N.A.'s
        // ten-minute limit (the retry when the roof reads open again), each with its wait for a roof
        // still opening and a park still under way, every roof close attempt at the Dome's move
        // timeout, one flat panel cover move, a reconnect each for the mount, dome and panel, and margin.
        public static readonly TimeSpan SecureTimeout = TimeSpan.FromMinutes(55);

        /// <summary>
        /// How far ahead a frame commits the mount, its exposure plus setup. The limit check and the
        /// meridian-flip decision must use the same window. If they disagree we either refuse a frame for
        /// a limit nothing would fix, or cross a limit with no flip scheduled.
        /// </summary>
        public static TimeSpan FrameWindow(AutopilotNextResponse? target) =>
            TimeSpan.FromSeconds((target?.Exposure ?? DefaultExposureSeconds) + LimitLookAheadMarginSeconds);

        // The pre-slew limit check has to guess how long setup takes, and autofocus can blow past
        // it. So we check again over just the exposure right before it starts.
        public const double ExposureSettleSeconds = 30.0;

        /// <summary>
        /// The exposure plus a settle allowance. That's all that's left between the last limit check
        /// and the shutter closing.
        /// </summary>
        public static TimeSpan ExposureWindow(AutopilotNextResponse? target) =>
            TimeSpan.FromSeconds((target?.Exposure ?? DefaultExposureSeconds) + ExposureSettleSeconds);

        // Waiting states log a "still waiting" line every Nth poll (10 x 30s = 5 minutes), so a long
        // wait shows it's alive without a line every 30 seconds.
        public const int WaitHeartbeatPolls = 10;

        // Free space the image directory needs before a frame starts. Well over one frame, so a long
        // sub can't half-write into a full disk.
        public const double MinimumFreeDiskGb = 5.0;
    }
}
