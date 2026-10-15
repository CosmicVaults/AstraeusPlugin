using CosmicVaults.NINA.Astraeus.Engine.Imaging;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Imaging {
    [TestFixture]
    public class AcquisitionMetadataTests {
        // N.I.N.A. reports -1 for a gain or offset the camera can't expose, and the server rejects negatives, so it
        // becomes 0 to keep a light matched to its masters.
        [TestCase(-1, 0)]
        [TestCase(0, 0)]
        [TestCase(120, 120)]
        public void AMissingGainOrOffsetBecomesZero(int reported, int expected) {
            Assert.That(AcquisitionMetadata.NormalizeGainOrOffset(reported), Is.EqualTo(expected));
        }

        // The nullable overload keeps a true null as null and clamps a reported value the same way.
        [Test]
        public void ANullableGainOrOffsetKeepsNullAndClampsTheRest() {
            Assert.That(AcquisitionMetadata.NormalizeGainOrOffset((int?)null), Is.Null);
            Assert.That(AcquisitionMetadata.NormalizeGainOrOffset((int?)-1), Is.EqualTo(0));
            Assert.That(AcquisitionMetadata.NormalizeGainOrOffset((int?)56), Is.EqualTo(56));
        }

        // Only a negative value counts as missing, so a real gain of 0 is not mistaken for not reported.
        [TestCase(-1, true)]
        [TestCase(0, false)]
        [TestCase(5, false)]
        public void OnlyANegativeValueIsMissing(int value, bool isMissing) {
            Assert.That(AcquisitionMetadata.IsMissing(value), Is.EqualTo(isMissing));
        }

        // The sensor reading wins over a set point that may be stale, and rounds away from zero like the web side, so
        // -9.5 matches a -10 master and not a -9.
        [TestCase(-9.6, -10.0, -10)]
        [TestCase(-9.5, -10.0, -10)]
        [TestCase(-9.4, -10.0, -9)]
        [TestCase(10.5, 0.0, 11)]
        [TestCase(-5.2, -10.0, -5)]
        public void TheSensorReadingWinsAndRoundsAwayFromZero(double sensor, double setPoint, int expected) {
            Assert.That(AcquisitionMetadata.MatchingTemperature(sensor, setPoint), Is.EqualTo(expected));
        }

        // A camera with no sensor reading falls back to its set point rather than reporting no temperature.
        [Test]
        public void WithoutAReadingTheSetPointIsUsed() {
            Assert.That(AcquisitionMetadata.MatchingTemperature(double.NaN, -10.4), Is.EqualTo(-10));
        }

        // With neither a reading nor a set point there is no temperature to match on, and null says so.
        [Test]
        public void WithNeitherThereIsNoTemperature() {
            Assert.That(AcquisitionMetadata.MatchingTemperature(double.NaN, double.NaN), Is.Null);
        }

        // Blank text reads as null, and anything else is passed through as typed, with no trimming.
        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("   ", null)]
        [TestCase("L", "L")]
        [TestCase(" L ", " L ")]
        public void BlankTextIsNullAndAnythingElseIsLeftAsTyped(string? text, string? expected) {
            Assert.That(AcquisitionMetadata.NullIfBlank(text), Is.EqualTo(expected));
        }
    }
}