using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Components;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using Moq;
using NINA.Profile;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Components
{
    [TestFixture]
    public class ProfileManagerTests
    {
        [TestCase("{\"action\":\"switch\"}")]
        [TestCase("{\"action\":\"switch\",\"id\":42}")]
        [TestCase("{\"action\":\"switch\",\"id\":\"not-a-guid\"}")]
        public async Task ASwitchWithoutAProfileGuidIsRejectedAsMalformed(string payload)
        {
            FakeNina nina = new FakeNina();
            await StartManagerAsync(nina);

            nina.Bus.Receive(Command("switch", payload));

            Assert.That(() => nina.Bus.LastResponse(), Is.EqualTo(("rejected",
                "Malformed switch command: 'id' is missing, the wrong type or out of range")).After(2000, 20));
            nina.ProfileService.Verify(service => service.SelectProfile(It.IsAny<ProfileMeta>()), Times.Never);
        }

        [Test]
        public async Task AnUnknownProfileIsRejectedAndTheListIsSentAgain()
        {
            FakeNina nina = new FakeNina();
            await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(Guid.NewGuid()));

            Assert.That(() => nina.Bus.LastResponse(),
                Is.EqualTo(("rejected", "That profile is not in N.I.N.A.'s list")).After(2000, 20));
            Assert.That(() => nina.Bus.LastSent<ProfileListUpdate>(), Is.Not.Null.After(2000, 20));
            nina.ProfileService.Verify(service => service.SelectProfile(It.IsAny<ProfileMeta>()), Times.Never);
        }

        [Test]
        public async Task SwitchingToTheActiveProfileChangesNothing()
        {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true);
            nina.MountDisconnectsAtOnce();
            await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(FakeNina.ProfileId));

            Assert.That(() => nina.Bus.LastResponse(),
                Is.EqualTo(("accepted", "'Profile A' is already the active profile")).After(2000, 20));
            Assert.That(() => nina.Bus.LastSent<ProfileListUpdate>(), Is.Not.Null.After(2000, 20));
            Assert.That(nina.Commands, Is.Empty);
            nina.ProfileService.Verify(service => service.SelectProfile(It.IsAny<ProfileMeta>()), Times.Never);
        }

        [Test]
        public async Task ASwitchIsRefusedWhileANinaSequenceRuns()
        {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true);
            nina.MountDisconnectsAtOnce();
            nina.SetNinaSequenceRunning(true);
            ProfileMeta target = nina.AddProfile("Profile B");
            await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(target.Id));

            Assert.That(() => nina.Bus.LastResponse(), Is.EqualTo(("rejected",
                "A N.I.N.A. sequence is running, so the profile can't be changed now")).After(2000, 20));
            Assert.That(nina.Commands, Is.Empty);
            nina.ProfileService.Verify(service => service.SelectProfile(It.IsAny<ProfileMeta>()), Times.Never);
        }

        [Test]
        public async Task ASecondSwitchIsRefusedWhileOneIsInFlight()
        {
            FakeNina nina = new FakeNina();
            ProfileMeta target = nina.AddProfile("Profile B");
            using ManualResetEventSlim isSelecting = new ManualResetEventSlim();
            using ManualResetEventSlim release = new ManualResetEventSlim();
            nina.ProfileService.Setup(service => service.SelectProfile(It.IsAny<ProfileMeta>()))
                .Returns(() =>
                {
                    isSelecting.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return false;
                });
            ProfileManager manager = await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(target.Id, "1"));
            Assert.That(isSelecting.Wait(TimeSpan.FromSeconds(2)), Is.True);
            nina.Bus.Receive(SwitchTo(target.Id, "2"));

            Assert.That(() => nina.Bus.LastResponse(),
                Is.EqualTo(("rejected", "A profile switch is already in progress")).After(2000, 20));
            release.Set();
            Assert.That(() => manager.IsSwitching, Is.False.After(2000, 20));
        }

        [Test]
        public async Task ASwitchDisconnectsEveryDeviceBeforeSelectingTheProfile()
        {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true);
            nina.MountDisconnectsAtOnce();
            nina.SetCamera(isConnected: true);
            nina.CameraDisconnectsAtOnce();
            ProfileMeta target = nina.AddProfile("Profile B");
            nina.LetSelectProfileSucceed();
            await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(target.Id));

            Assert.That(() => nina.LogMessages("INFO"),
                Does.Contain("Switched N.I.N.A. profile to 'Profile B'.").After(2000, 20));
            Assert.Multiple(() =>
            {
                Assert.That(nina.Bus.LastResponse(), Is.EqualTo(("accepted", (string?)null)));
                Assert.That(nina.Commands,
                    Is.EqualTo(new[] { "mount: Disconnect", "camera: Disconnect", "profile: SelectProfile" }));
                Assert.That(SentActiveProfile(nina), Is.EqualTo(target.Id.ToString()));
            });
        }

        [Test]
        public async Task ADeviceThatStaysConnectedStopsTheSwitch()
        {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true);
            nina.MountRefusesToDisconnect();
            ProfileMeta target = nina.AddProfile("Profile B");
            ProfileManager manager = await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(target.Id));

            Assert.That(() => nina.LogMessages("ERROR"),
                Does.Contain("The Telescope Mount did not disconnect, so the profile was not changed.").After(2000, 20));
            Assert.That(() => manager.IsSwitching, Is.False.After(2000, 20));
            nina.ProfileService.Verify(service => service.SelectProfile(It.IsAny<ProfileMeta>()), Times.Never);
        }

        [Test]
        public async Task AProfileNinaCannotLoadIsAnErrorAndTheNextSwitchStillRuns()
        {
            FakeNina nina = new FakeNina();
            ProfileMeta target = nina.AddProfile("Profile B");
            nina.LetSelectProfileFail();
            ProfileManager manager = await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(target.Id, "1"));
            Assert.That(() => nina.LogMessages("ERROR"), Does.Contain(
                "N.I.N.A. could not load the 'Profile B' profile. It may be open in another N.I.N.A.").After(2000, 20));
            Assert.That(() => manager.IsSwitching, Is.False.After(2000, 20));

            nina.LetSelectProfileSucceed();
            nina.Bus.Receive(SwitchTo(target.Id, "2"));

            Assert.That(() => nina.LogMessages("INFO"),
                Does.Contain("Switched N.I.N.A. profile to 'Profile B'.").After(2000, 20));
        }

        [Test]
        public async Task APluginSwitchedOffInTheNewProfileIsSwitchedBackOn()
        {
            FakeNina nina = new FakeNina();
            ProfileMeta target = nina.AddProfile("Profile B");
            nina.LetSelectProfileSucceed();
            // The new profile's store only exists once the switch has made it.
            nina.ProfileService.Object.ProfileChanged += (_, _) => nina.Settings.SetIsPluginEnabled(false);
            await StartManagerAsync(nina);

            nina.Bus.Receive(SwitchTo(target.Id));

            Assert.That(() => nina.LogMessages("WARNING"), Does.Contain(
                "Astraeus was switched off in the 'Profile B' profile, so it has been switched on.").After(2000, 20));
            Assert.That(nina.Settings.IsPluginEnabled(), Is.True);
        }

        [Test]
        public async Task TheListIsSentOnAnyProfileChange()
        {
            FakeNina nina = new FakeNina();
            await StartManagerAsync(nina);

            nina.ProfileService.Raise(service => service.ProfileChanged += null, EventArgs.Empty);

            Assert.That(() => SentActiveProfile(nina), Is.EqualTo(FakeNina.ProfileId.ToString()).After(2000, 20));
        }

        [Test]
        public async Task TheListIsSentTwoSecondsAfterTheSocketConnects()
        {
            FakeNina nina = new FakeNina();
            await StartManagerAsync(nina);

            nina.Bus.RaiseConnected();
            Assert.That(nina.Bus.LastSent<ProfileListUpdate>(), Is.Null);
            nina.Clock.Advance(TimeSpan.FromSeconds(2));

            Assert.That(() => nina.Bus.LastSent<ProfileListUpdate>(), Is.Not.Null.After(2000, 20));
        }

        [Test]
        public async Task FetchProfilesIsAnsweredWithTheList()
        {
            FakeNina nina = new FakeNina();
            nina.Profiles[0].Description = "Imaging";
            nina.AddProfile("Profile B");
            await StartManagerAsync(nina);

            nina.Bus.Receive(Command("fetchProfiles", "{\"action\":\"fetchProfiles\"}"));

            Assert.That(() => nina.Bus.LastSent<ProfileListUpdate>(), Is.Not.Null.After(2000, 20));
            JsonElement payload = nina.Bus.LastSent<ProfileListUpdate>()!.Payload!.Value;
            JsonElement first = payload.GetProperty("profiles")[0];
            JsonElement second = payload.GetProperty("profiles")[1];
            Assert.Multiple(() =>
            {
                Assert.That(nina.Bus.LastResponse(), Is.EqualTo(("accepted", (string?)null)));
                Assert.That(first.EnumerateObject().Select(property => property.Name),
                    Is.EqualTo(new[] { "id", "name", "description", "isActive" }));
                Assert.That(first.GetProperty("id").GetString(), Is.EqualTo(FakeNina.ProfileId.ToString()));
                Assert.That(first.GetProperty("isActive").GetBoolean(), Is.True);
                Assert.That(second.GetProperty("isActive").GetBoolean(), Is.False);
                Assert.That(payload.GetProperty("activeProfile").GetString(), Is.EqualTo(FakeNina.ProfileId.ToString()));
            });
        }

        [Test]
        public async Task TheListNeverCarriesTheProfileFileLocation()
        {
            FakeNina nina = new FakeNina();
            nina.Profiles[0].Location = @"C:\Users\observer\AppData\Local\NINA\Profiles\a.profile";
            await StartManagerAsync(nina);

            nina.Bus.Receive(Command("fetchProfiles", "{\"action\":\"fetchProfiles\"}"));

            Assert.That(() => nina.Bus.LastSent<ProfileListUpdate>(), Is.Not.Null.After(2000, 20));
            string json = nina.Bus.LastSent<ProfileListUpdate>()!.Payload!.Value.GetRawText();
            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Not.Contain("location").IgnoreCase);
                Assert.That(json, Does.Not.Contain("observer"));
            });
        }

        private static async Task<ProfileManager> StartManagerAsync(FakeNina nina)
        {
            ProfileManager manager = new ProfileManager(nina.Observatory, nina.Bus);
            await manager.Awake();
            await manager.Start();
            return manager;
        }

        private static WsCommand Command(string action, string payload, string commandId = "1") =>
            new WsCommand(commandId, "profile", action, Json.Parse(payload));

        private static WsCommand SwitchTo(Guid profileId, string commandId = "1") =>
            Command("switch", $"{{\"action\":\"switch\",\"id\":\"{profileId}\"}}", commandId);

        private static string? SentActiveProfile(FakeNina nina) =>
            nina.Bus.LastSent<ProfileListUpdate>()?.Payload?.GetProperty("activeProfile").GetString();
    }
}
