using System.Text.Json;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Components {
    [TestFixture]
    public class SettingsTests {
        private const string RoofOptionKey = "Astraeus.ParkMountToOpenRoof";

        // The wire name is part of the server contract and must not follow the C# rename.
        [Test]
        public async Task TheRoofOptionIsSentAsParkMountToOpenRoof() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetCanMountMoveUnderClosedRoof(true);
            Settings settings = new Settings(nina.Observatory, nina.Bus);

            await settings.PushAutopilotSettingsAsync();

            SettingsUpdate? update = nina.Bus.LastSent<SettingsUpdate>();
            Assert.That(update, Is.Not.Null);
            JsonElement data = update!.Payload!.Value.GetProperty("data");
            Assert.That(update.Device, Is.EqualTo("autopilot"));
            Assert.That(data.GetProperty("park_mount_to_open_roof").GetBoolean(), Is.True);
        }

        // A settings update from the dashboard under the wire name reaches the stored setting through the started
        // component.
        [Test]
        public async Task AnInboundParkMountToOpenRoofSetsTheOption() {
            FakeNina nina = new FakeNina();
            Settings settings = new Settings(nina.Observatory, nina.Bus);
            await settings.Awake();
            await settings.Start();

            nina.Bus.Receive(AutopilotSettingsCommand("""{"park_mount_to_open_roof": true}"""));

            // The handler runs on an async void path, so wait for the effect rather than assume it has finished.
            Assert.That(() => nina.Settings.CanMountMoveUnderClosedRoof(), Is.True.After(2000, 20));
            Assert.That(nina.StoredSetting(RoofOptionKey), Is.EqualTo("true"));
        }

        // A refused update must change nothing and must send the current settings back, so the dashboard shows what
        // N.I.N.A. holds rather than the value it tried to set.
        [Test]
        public async Task AnInboundValueOfTheWrongTypeIsIgnoredAndTheCurrentSettingsSentBack() {
            FakeNina nina = new FakeNina();
            Settings settings = new Settings(nina.Observatory, nina.Bus);
            await settings.Awake();
            await settings.Start();

            nina.Bus.Receive(AutopilotSettingsCommand("""{"park_mount_to_open_roof": "yes"}"""));

            Assert.That(() => nina.Bus.LastSent<SettingsUpdate>(), Is.Not.Null.After(2000, 20));
            Assert.That(nina.Settings.CanMountMoveUnderClosedRoof(), Is.False);
            JsonElement data = nina.Bus.LastSent<SettingsUpdate>()!.Payload!.Value.GetProperty("data");
            Assert.That(data.GetProperty("park_mount_to_open_roof").GetBoolean(), Is.False);
        }

        private static WsCommand AutopilotSettingsCommand(string dataJson) =>
            new WsCommand("1", "settings", "settingsChanged", Json.Parse($$"""{"data": {{dataJson}}}"""), "autopilot");
    }
}