using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Profile.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests
{
    [TestFixture]
    public class AuthenticatorTests
    {
        private const string TokenPath = "/o/token/";
        private const string Email = "observer@example.com";

        ///////////// ReloadForProfileChangeAsync /////////////

        [Test]
        public async Task ASignedInSessionMovesIntoAProfileWithNoSignIn()
        {
            FakeNina nina = await SignedInAsync(Email);
            IProfile oldProfile = SwitchToNewProfile(nina);

            await nina.Observatory.Authenticator.ReloadForProfileChangeAsync(oldProfile);

            Assert.Multiple(() =>
            {
                Assert.That(nina.Observatory.Authenticator.IsAuthenticated, Is.True);
                Assert.That(nina.Settings.GetRefreshToken(), Is.EqualTo("rt-2"));
                Assert.That(nina.Settings.GetUserEmail(), Is.EqualTo(Email));
                Assert.That(TokenRequests(nina), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task ASignedInSessionReplacesTheSameAccountsOlderToken()
        {
            FakeNina nina = await SignedInAsync(Email);
            IProfile oldProfile = SwitchToNewProfile(nina);
            nina.Settings.SetRefreshToken("rt-old");
            nina.Settings.SetUserEmail("Observer@Example.com");

            await nina.Observatory.Authenticator.ReloadForProfileChangeAsync(oldProfile);

            Assert.Multiple(() =>
            {
                Assert.That(nina.Observatory.Authenticator.IsAuthenticated, Is.True);
                Assert.That(nina.Settings.GetRefreshToken(), Is.EqualTo("rt-2"));
                Assert.That(nina.Settings.GetUserEmail(), Is.EqualTo(Email));
                Assert.That(TokenRequests(nina), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task AProfileSignedInAsAnotherAccountKeepsThatSignIn()
        {
            FakeNina nina = await SignedInAsync(Email);
            IProfile oldProfile = SwitchToNewProfile(nina);
            nina.Settings.SetRefreshToken("rt-other");
            nina.Settings.SetUserEmail("other@example.com");
            nina.Server.Reply("POST", TokenPath, TokenReply("rt-other-2"));

            await nina.Observatory.Authenticator.ReloadForProfileChangeAsync(oldProfile);

            Assert.Multiple(() =>
            {
                Assert.That(nina.Observatory.Authenticator.IsAuthenticated, Is.True);
                Assert.That(nina.Observatory.Authenticator.SignedInAs, Is.EqualTo("other@example.com"));
                Assert.That(TokenRequests(nina).Last().Body, Does.Contain("refresh_token=rt-other&"));
                Assert.That(nina.Settings.GetRefreshToken(), Is.EqualTo("rt-other-2"));
                Assert.That(nina.Settings.GetRefreshToken(oldProfile), Is.EqualTo("rt-2"));
            });
        }

        [Test]
        public async Task ASignedOutPluginRestoresFromTheNewProfile()
        {
            FakeNina nina = new FakeNina();
            IProfile oldProfile = SwitchToNewProfile(nina);
            nina.Settings.SetRefreshToken("rt-b");
            nina.Settings.SetUserEmail(Email);
            nina.Server.Reply("POST", TokenPath, TokenReply("rt-b-2"));

            await nina.Observatory.Authenticator.ReloadForProfileChangeAsync(oldProfile);

            Assert.Multiple(() =>
            {
                Assert.That(nina.Observatory.Authenticator.IsAuthenticated, Is.True);
                Assert.That(TokenRequests(nina).Single().Body, Does.Contain("refresh_token=rt-b&"));
                Assert.That(nina.Settings.GetRefreshToken(), Is.EqualTo("rt-b-2"));
            });
        }

        [Test]
        public async Task WithoutTheOldProfileTheSessionIsDropped()
        {
            FakeNina nina = await SignedInAsync(Email);
            IProfile oldProfile = SwitchToNewProfile(nina);

            await nina.Observatory.Authenticator.ReloadForProfileChangeAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(nina.Observatory.Authenticator.IsAuthenticated, Is.False);
                Assert.That(nina.Settings.GetRefreshToken(), Is.Null);
                Assert.That(nina.Settings.GetRefreshToken(oldProfile), Is.EqualTo("rt-2"));
            });
        }

        [TestCase(null, Email)]
        [TestCase(Email, null)]
        public async Task AnUnknownEmailOnEitherSideCountsAsTheSameAccount(string? sessionEmail, string? storedEmail)
        {
            FakeNina nina = await SignedInAsync(sessionEmail);
            IProfile oldProfile = SwitchToNewProfile(nina);
            nina.Settings.SetRefreshToken("rt-old");
            nina.Settings.SetUserEmail(storedEmail);

            await nina.Observatory.Authenticator.ReloadForProfileChangeAsync(oldProfile);

            Assert.Multiple(() =>
            {
                Assert.That(nina.Observatory.Authenticator.IsAuthenticated, Is.True);
                Assert.That(nina.Settings.GetRefreshToken(), Is.EqualTo("rt-2"));
                Assert.That(TokenRequests(nina), Has.Count.EqualTo(1));
            });
        }

        ///////////// Helpers /////////////

        // Signed in the way N.I.N.A.'s start-up does it, from a stored token the server rotates to "rt-2".
        private static async Task<FakeNina> SignedInAsync(string? email)
        {
            FakeNina nina = new FakeNina();
            nina.Settings.SetRefreshToken("rt-1");
            nina.Settings.SetUserEmail(email);
            nina.Server.Reply("POST", TokenPath, TokenReply("rt-2"));
            await nina.Observatory.Authenticator.Awake();
            Assert.That(nina.Observatory.Authenticator.IsAuthenticated, Is.True, "signed in before the switch");
            return nina;
        }

        private static IProfile SwitchToNewProfile(FakeNina nina)
        {
            IProfile oldProfile = nina.ProfileService.Object.ActiveProfile;
            nina.SwitchActiveProfile(nina.AddProfile("Profile B"));
            return oldProfile;
        }

        private static string TokenReply(string refreshToken) =>
            $$"""{"access_token": "at", "refresh_token": "{{refreshToken}}", "expires_in": 28800}""";

        private static List<FakeServerHandler.Request> TokenRequests(FakeNina nina) =>
            nina.Server.Requests.Where(request => request.Path == TokenPath).ToList();
    }
}
