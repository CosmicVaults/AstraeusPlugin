namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// The answer to POST /api/observatory/storage/check/. HasSpace is true on any headroom at all, so it
    /// doesn't promise this frame fits. Going one frame over quota is fine. The server reads every number
    /// here from R2, and the plugin never reports sizes.
    /// </summary>
    public sealed record StorageCheckResult(
        bool HasSpace,
        long UsedBytes,
        long QuotaBytes,
        long RemainingBytes,
        int EvictedFrames,
        long FreedBytes,
        string? Policy,
        bool ShouldPauseAutopilotWhenFull) {

        public string Describe() =>
            $"{FormatBytes(UsedBytes)} of {FormatBytes(QuotaBytes)} used, {FormatBytes(RemainingBytes)} free";

        /// <summary>
        /// The eviction policy in the site's own words, so the log matches the setting the observer
        /// chose. An unknown policy is shown as the server sent it, with underscores as spaces.
        /// </summary>
        public string DescribePolicy() => Policy switch {
            "overwrite_oldest" => "Overwrite Oldest",
            null or "" => "your storage policy",
            { } other => other.Replace('_', ' '),
        };

        /// <summary>
        /// Bytes in decimal units. The site quotes quotas in decimal GB, and a 50 GB allowance logged as
        /// 46.6 GB looks like a bug.
        /// </summary>
        public static string FormatBytes(long bytes) {
            const double BytesPerMegabyte = 1_000_000d;
            const double BytesPerGigabyte = 1_000_000_000d;
            if (bytes >= BytesPerGigabyte) return $"{bytes / BytesPerGigabyte:F1} GB";
            if (bytes >= BytesPerMegabyte) return $"{bytes / BytesPerMegabyte:F0} MB";
            return $"{bytes} bytes";
        }
    }
}
