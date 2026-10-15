using CosmicVaults.NINA.Astraeus.Engine.Feed;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Feed {
    [TestFixture]
    public class FeedUrlTests {
        private const string CameraUrl = "rtsp://user:secret@203.0.113.5:554/live";

        ///////////// RedactCameraDetails /////////////

        // An exception message can carry the full camera URL, its credentials and its host. All three must be
        // replaced before the message is logged.
        [Test]
        public void TheAddressCredentialsAndHostAreAllRedacted() {
            string message = $"Failed to open {CameraUrl}: 203.0.113.5 refused user:secret";

            string redacted = FeedUrl.RedactCameraDetails(message, CameraUrl);

            Assert.That(redacted, Is.EqualTo("Failed to open the camera address: the camera refused ***"));
        }

        // The privacy rule stated directly: no host, no user and no password survives in text that reaches the
        // dashboard.
        [Test]
        public void NothingOfTheCameraIsLeftInTheText() {
            string message = $"Failed to open {CameraUrl}";

            string redacted = FeedUrl.RedactCameraDetails(message, CameraUrl);

            Assert.That(redacted, Does.Not.Contain("203.0.113.5").And.Not.Contain("secret").And.Not.Contain("user"));
        }

        // Drivers echo the URL in whatever case they like, and it must still be found and replaced.
        [Test]
        public void TheAddressIsFoundWhateverItsCase() {
            string message = "Failed to open RTSP://USER:SECRET@203.0.113.5:554/LIVE";

            Assert.That(FeedUrl.RedactCameraDetails(message, CameraUrl), Is.EqualTo("Failed to open the camera address"));
        }

        // With no URL, or one that isn't absolute, there is nothing to redact and the message passes through.
        [TestCase(null)]
        [TestCase("")]
        [TestCase("not a url")]
        public void WithoutAUsableUrlTheMessageIsUnchanged(string? url) {
            const string message = "Failed to open the stream";

            Assert.That(FeedUrl.RedactCameraDetails(message, url), Is.EqualTo(message));
        }

        // An empty message stays empty rather than becoming a placeholder.
        [Test]
        public void AnEmptyMessageStaysEmpty() {
            Assert.That(FeedUrl.RedactCameraDetails("", CameraUrl), Is.Empty);
        }

        ///////////// WithoutCredentials /////////////

        // A pasted rtsp://user:pass@host URL has its credentials split out, so they go to the protected fields and
        // not the plain-text profile.
        [Test]
        public void CredentialsAreTakenOutOfTheUrlAndReturnedSeparately() {
            string? url = FeedUrl.WithoutCredentials(CameraUrl, out string? username, out string? password);

            Assert.Multiple(() => {
                Assert.That(url, Is.EqualTo("rtsp://203.0.113.5:554/live"));
                Assert.That(username, Is.EqualTo("user"));
                Assert.That(password, Is.EqualTo("secret"));
            });
        }

        // A URL with a user but no password gives a null password, not an empty string that would be sent as one.
        [Test]
        public void AUserWithoutAPasswordGivesANullPassword() {
            string? url = FeedUrl.WithoutCredentials("rtsp://user@203.0.113.5/live", out string? username, out string? password);

            Assert.Multiple(() => {
                Assert.That(url, Is.EqualTo("rtsp://203.0.113.5/live"));
                Assert.That(username, Is.EqualTo("user"));
                Assert.That(password, Is.Null);
            });
        }

        // Credentials typed with URL escapes are unescaped, so an @ or a colon in a password still connects.
        [Test]
        public void EscapedCredentialsAreUnescaped() {
            FeedUrl.WithoutCredentials("rtsp://us%40er:p%3Ass@203.0.113.5/live", out string? username, out string? password);

            Assert.That(username, Is.EqualTo("us@er"));
            Assert.That(password, Is.EqualTo("p:ss"));
        }

        // A URL with nothing to strip, or no URL at all, comes back exactly as given with no credentials.
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a url")]
        [TestCase("rtsp://203.0.113.5/live")]
        public void AUrlWithoutCredentialsIsReturnedAsIs(string? url) {
            string? result = FeedUrl.WithoutCredentials(url, out string? username, out string? password);

            Assert.Multiple(() => {
                Assert.That(result, Is.EqualTo(url));
                Assert.That(username, Is.Null);
                Assert.That(password, Is.Null);
            });
        }

        ///////////// ResolveCredentials /////////////

        // The configured user and password win over any in the URL, so the protected fields are the source of truth.
        [Test]
        public void ConfiguredCredentialsWinOverTheUrls() {
            (string? user, string? password) = FeedUrl.ResolveCredentials(CameraUrl, "configured", "configured-secret");

            Assert.That(user, Is.EqualTo("configured"));
            Assert.That(password, Is.EqualTo("configured-secret"));
        }

        // A configured user with no password still wins, rather than falling back to a password buried in the URL.
        [Test]
        public void AConfiguredUserWithoutAPasswordStillWins() {
            (string? user, string? password) = FeedUrl.ResolveCredentials(CameraUrl, "configured", null);

            Assert.That(user, Is.EqualTo("configured"));
            Assert.That(password, Is.Null);
        }

        // With no configured user, a pasted full URL still connects with the credentials it carries.
        [TestCase(null)]
        [TestCase("")]
        public void WithNoConfiguredUserTheUrlsCredentialsAreUsed(string? configuredUser) {
            (string? user, string? password) = FeedUrl.ResolveCredentials(CameraUrl, configuredUser, null);

            Assert.That(user, Is.EqualTo("user"));
            Assert.That(password, Is.EqualTo("secret"));
        }

        // With none configured and none in the URL, both are null so no empty credentials are sent.
        [TestCase("rtsp://203.0.113.5/live")]
        [TestCase(null)]
        public void WithNoCredentialsAnywhereBothAreNull(string? url) {
            (string? user, string? password) = FeedUrl.ResolveCredentials(url, null, null);

            Assert.That(user, Is.Null);
            Assert.That(password, Is.Null);
        }
    }
}