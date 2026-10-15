using CosmicVaults.NINA.Astraeus.Engine.Imaging;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Imaging {
    [TestFixture]
    public class StorageCheckResultTests {
        // Decimal units, as the site quotes quotas, so 50 GB reads as 50 GB in the log and not 46.6 GB, which looks
        // like a bug.
        [TestCase(0, "0 bytes")]
        [TestCase(999_999, "999999 bytes")]
        [TestCase(1_000_000, "1 MB")]
        [TestCase(52_600_000, "53 MB")]
        [TestCase(999_999_999, "1000 MB")]
        [TestCase(1_000_000_000, "1.0 GB")]
        [TestCase(1_500_000_000, "1.5 GB")]
        [TestCase(50_000_000_000, "50.0 GB")]
        public void BytesAreFormattedInDecimalUnits(long bytes, string expected) {
            Assert.That(StorageCheckResult.FormatBytes(bytes), Is.EqualTo(expected));
        }

        // Pins the storage log line's shape so the night's log keeps reading the same.
        [Test]
        public void DescribeReadsAsUsedOfQuotaAndFree() {
            StorageCheckResult result = Result(usedBytes: 1_500_000_000, quotaBytes: 50_000_000_000, remainingBytes: 48_500_000_000);

            Assert.That(result.Describe(), Is.EqualTo("1.5 GB of 50.0 GB used, 48.5 GB free"));
        }

        // The policy is logged in the words the site uses, with a fallback for none and underscores spaced out for
        // one it doesn't know.
        [TestCase("overwrite_oldest", "Overwrite Oldest")]
        [TestCase(null, "your storage policy")]
        [TestCase("", "your storage policy")]
        [TestCase("pause_when_full", "pause when full")]
        public void ThePolicyIsNamedInTheSitesWords(string? policy, string expected) {
            Assert.That(Result(policy: policy).DescribePolicy(), Is.EqualTo(expected));
        }

        private static StorageCheckResult Result(long usedBytes = 0, long quotaBytes = 0, long remainingBytes = 0,
            string? policy = null) =>
            new StorageCheckResult(
                HasSpace: true,
                UsedBytes: usedBytes,
                QuotaBytes: quotaBytes,
                RemainingBytes: remainingBytes,
                EvictedFrames: 0,
                FreedBytes: 0,
                Policy: policy,
                ShouldPauseAutopilotWhenFull: false);
    }
}