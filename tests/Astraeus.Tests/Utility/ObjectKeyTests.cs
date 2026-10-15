using CosmicVaults.NINA.Astraeus.Engine.Utility;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Utility {
    [TestFixture]
    public class ObjectKeyTests {
        ///////////// SafeSegment /////////////

        // A null, empty or blank target name gives no folder segment, which callers read as no folder, rather than a
        // key with an empty level.
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void NothingUsableIsNull(string? name) {
            Assert.That(ObjectKey.SafeSegment(name), Is.Null);
        }

        // Each character that would add a folder level, break a filesystem or confuse a signed URL becomes an
        // underscore instead.
        [TestCase('/')]
        [TestCase('\\')]
        [TestCase(':')]
        [TestCase('*')]
        [TestCase('?')]
        [TestCase('"')]
        [TestCase('<')]
        [TestCase('>')]
        [TestCase('|')]
        [TestCase('#')]
        public void AnUnsafeCharacterBecomesAnUnderscore(char character) {
            Assert.That(ObjectKey.SafeSegment($"NGC{character}7000"), Is.EqualTo("NGC_7000"));
        }

        // Control characters from a bad server string become underscores rather than ending up inside an object key.
        [TestCase("\t")]
        [TestCase("\u0001")]
        public void AControlCharacterBecomesAnUnderscore(string character) {
            Assert.That(ObjectKey.SafeSegment($"NGC{character}7000"), Is.EqualTo("NGC_7000"));
        }

        // Spaces, accents and ordinary punctuation are kept, since NGC 7000 is what the user expects to see in the
        // bucket.
        [TestCase("NGC 7000")]
        [TestCase("Étoile du Berger")]
        [TestCase("Sh2-155 (Cave)")]
        public void SpacesAccentsAndPunctuationSurvive(string name) {
            Assert.That(ObjectKey.SafeSegment(name), Is.EqualTo(name));
        }

        // Leading and trailing spaces and dots are trimmed, since some tools hide a dotted name and others drop a
        // trailing dot.
        [TestCase("  M31  ", "M31")]
        [TestCase(".hidden", "hidden")]
        [TestCase("name.", "name")]
        [TestCase(" . M31 . ", "M31")]
        public void LeadingAndTrailingSpacesAndDotsAreTrimmed(string name, string expected) {
            Assert.That(ObjectKey.SafeSegment(name), Is.EqualTo(expected));
        }

        // A name that is nothing but dots and spaces trims to nothing, and nothing usable is null.
        [TestCase("...")]
        [TestCase(" . ")]
        public void OnlyDotsAndSpacesIsNull(string name) {
            Assert.That(ObjectKey.SafeSegment(name), Is.Null);
        }

        // Underscores aren't trimmed, so a name made only of unsafe characters still gives a folder rather than
        // disappearing.
        [Test]
        public void OnlyUnsafeCharactersGivesUnderscores() {
            Assert.That(ObjectKey.SafeSegment("///"), Is.EqualTo("___"));
        }

        // A name past the segment limit is cut, so a runaway target name can't produce a key the bucket rejects.
        [Test]
        public void ALongNameIsCutTo120Characters() {
            string name = new string('a', 130);

            Assert.That(ObjectKey.SafeSegment(name), Is.EqualTo(new string('a', 120)));
        }

        // Cutting mid-name can leave a new trailing dot, so the trim runs again after the cut.
        [Test]
        public void ACutThatLeavesATrailingDotTrimsItAgain() {
            string name = new string('a', 119) + ".bbbb";

            Assert.That(ObjectKey.SafeSegment(name), Is.EqualTo(new string('a', 119)));
        }

        ///////////// Join /////////////

        // Keys are joined with forward slashes, never Path.Combine, since R2 only reads the forward slash as a
        // folder, and empty segments are dropped.
        [Test]
        public void JoinUsesForwardSlashesAndSkipsEmptySegments() {
            Assert.That(ObjectKey.Join("captures", null, "", "NGC 7000", "frame.fits"),
                Is.EqualTo("captures/NGC 7000/frame.fits"));
        }

        // Joining nothing gives an empty key rather than a stray separator.
        [Test]
        public void JoinWithNoSegmentsIsEmpty() {
            Assert.That(ObjectKey.Join(), Is.Empty);
        }

        ///////////// ReplaceFileName /////////////

        // Swapping the file name keeps every folder above it, and a bare file name is replaced whole.
        [TestCase("captures/NGC 7000/old.fits", "new.xisf", "captures/NGC 7000/new.xisf")]
        [TestCase("old.fits", "new.xisf", "new.xisf")]
        public void ReplaceFileNameKeepsTheFolders(string key, string fileName, string expected) {
            Assert.That(ObjectKey.ReplaceFileName(key, fileName), Is.EqualTo(expected));
        }

        ///////////// WithExtension /////////////

        // Only a dot in the last segment is an extension, so a dotted folder name isn't mistaken for one.
        [TestCase("captures/frame.fits", ".xisf", "captures/frame.xisf")]
        [TestCase("captures/frame.tar.gz", ".xisf", "captures/frame.tar.xisf")]
        [TestCase("captures/frame", ".xisf", "captures/frame.xisf")]
        [TestCase("v1.2/frame", ".xisf", "v1.2/frame.xisf")]
        public void WithExtensionOnlyCountsADotInTheLastSegment(string key, string extension, string expected) {
            Assert.That(ObjectKey.WithExtension(key, extension), Is.EqualTo(expected));
        }
    }
}