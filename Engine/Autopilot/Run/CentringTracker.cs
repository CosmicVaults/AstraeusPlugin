namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// How centring has gone on the target in hand. Centring is retried once on the next frame, and after
    /// that the target is captured on the mount's own coordinates. Keyed by the target's coordinates, so
    /// a new target starts clean without a reset, and a new night replaces the tracker whole.
    /// </summary>
    internal sealed class CentringTracker(Context context)
    {
        public const int MaxRetries = 1;

        public int FailureCount { get; private set; }

        public string? FailureReason { get; private set; }

        // Which target the count belongs to.
        private string? _targetKey;
        private bool _isGiveUpAnnounced;

        /// <summary>The coordinates that identify a target, to five decimals, or null for one without any.</summary>
        internal static string? KeyOf(AutopilotNextResponse? target) =>
            target?.Ra is double ra && target.Dec is double dec ? $"{ra:F5}|{dec:F5}" : null;

        private bool IsForCurrentTarget =>
            _targetKey != null && KeyOf(context.Frame.Target) == _targetKey;

        /// <summary>
        /// Whether the frame in hand rests on a centring that worked, meaning none has failed on this
        /// target since the last success. Sent to the server with every frame.
        /// </summary>
        public bool IsFrameCentred => !IsForCurrentTarget || FailureCount == 0;

        public bool IsRetryDue => IsForCurrentTarget && FailureCount > 0 && FailureCount <= MaxRetries;

        /// <summary>
        /// True once centring on this target has failed more often than it's retried, so we capture on
        /// the mount's own coordinates. The frame's pointing check reads it too, or an off-target solve
        /// would send the next frame back into the centring we gave up on.
        /// </summary>
        public bool IsGivenUp => IsForCurrentTarget && FailureCount > MaxRetries;

        public void RecordFailure(string reason)
        {
            string? key = KeyOf(context.Frame.Target);
            if (key != _targetKey)
            {
                _targetKey = key;
                FailureCount = 0;
                _isGiveUpAnnounced = false;
            }
            FailureCount++;
            FailureReason = reason;
        }

        public void Clear()
        {
            _targetKey = null;
            _isGiveUpAnnounced = false;
            FailureCount = 0;
            FailureReason = null;
        }

        /// <summary>True once per target, the first time giving up is asked about.</summary>
        public bool ShouldAnnounceGivenUp()
        {
            if (!IsGivenUp) return false;
            if (_isGiveUpAnnounced) return false;
            _isGiveUpAnnounced = true;
            return true;
        }
    }
}
