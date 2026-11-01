using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// An unbroken run of blind readings from one kind of watcher. A good reading, or more than
    /// WatcherReadingGap without a blind one, ends it.
    /// </summary>
    internal sealed class BlindSpell(string watcher, AutopilotStateResult failSafe, string failSafeAction)
    {
        private DateTime? _startedUtc;
        private DateTime _lastBlindUtc;

        public string Watcher => watcher;
        public AutopilotStateResult FailSafe => failSafe;
        public string FailSafeAction => failSafeAction;

        /// <summary>Counts a blind reading and returns how long the spell has lasted.</summary>
        public TimeSpan Observe(DateTime nowUtc, out bool isNew)
        {
            isNew = _startedUtc is null || nowUtc - _lastBlindUtc > AutopilotConstants.WatcherReadingGap;
            if (isNew) _startedUtc = nowUtc;
            _lastBlindUtc = nowUtc;
            return nowUtc - _startedUtc.GetValueOrDefault(nowUtc);
        }

        /// <summary>True if a spell was running.</summary>
        public bool End()
        {
            bool wasBlind = _startedUtc is not null;
            _startedUtc = null;
            return wasBlind;
        }
    }
}
