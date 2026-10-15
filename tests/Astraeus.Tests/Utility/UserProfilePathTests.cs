using System;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Utility {
    [TestFixture]
    public class UserProfilePathTests {
        // The real profile folder of whoever runs the tests, so no user name is written into the test.
        private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // A path under the profile folder carries the Windows user name, and it must leave as %USERPROFILE% before
        // anything is sent.
        [Test]
        public void APathUnderTheProfileFolderIsRedacted() {
            string text = $@"Saved {Profile}\Documents\N.I.N.A\Light_001.fits";

            Assert.That(UserProfilePath.Redact(text), Is.EqualTo(@"Saved %USERPROFILE%\Documents\N.I.N.A\Light_001.fits"));
        }

        // Windows paths are case-insensitive, so a differently cased profile folder must still be redacted.
        [Test]
        public void TheMatchIgnoresCase() {
            string text = $@"{Profile.ToUpperInvariant()}\Documents\a.fits";

            Assert.That(UserProfilePath.Redact(text), Is.EqualTo(@"%USERPROFILE%\Documents\a.fits"));
        }

        // A line naming two files must have both redacted, not just the first.
        [Test]
        public void EveryOccurrenceIsRedacted() {
            string text = $@"{Profile}\a.fits and {Profile}\b.fits";

            Assert.That(UserProfilePath.Redact(text), Is.EqualTo(@"%USERPROFILE%\a.fits and %USERPROFILE%\b.fits"));
        }

        // Text with no profile folder in it passes through untouched, empty text included.
        [TestCase("")]
        [TestCase(@"D:\Astro\Lights\a.fits")]
        public void TextWithoutTheProfileFolderIsUnchanged(string text) {
            Assert.That(UserProfilePath.Redact(text), Is.EqualTo(text));
        }

        // Only this machine's profile folder is known. Another user's path on a shared PC isn't redacted, and this
        // pins that limit so nobody assumes more.
        [Test]
        public void AnotherUsersFolderIsNotRedacted() {
            string text = @"C:\Users\someone-else\a.fits";

            Assert.That(UserProfilePath.Redact(text), Is.EqualTo(text));
        }
    }
}