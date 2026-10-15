using System.Linq;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States {
    [TestFixture]
    public class OpenDomeTests {
        // Dusk under an operated roof: the roof closed, conditions safe, the mount connected and still, and a driver that
        // opens at once.
        private static FakeNina ReadyToOpen() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(ShutterState.ShutterClosed);
            nina.SetSafety(true);
            nina.SetMount(isConnected: true);
            nina.RoofOpensAtOnce();
            return nina;
        }

        private static OpenDome Open(Context context) => new OpenDome("Open Dome Shutter", context);

        // A roof already open is simply taken as open: the resume path can find it that way hours after a secure, and the
        // session is no longer secured once the roof is open over an unparked mount to be.
        [Test]
        public async Task ARoofAlreadyOpenIsTakenAsOpenAndTheSessionIsNoLongerSecured() {
            FakeNina nina = ReadyToOpen();
            nina.SetRoof(ShutterState.ShutterOpen);
            Context context = nina.CreateContext();
            context.IsSessionSecured = true;

            AutopilotStateResult result = await StateRunner.RunAsync(Open(context));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.Empty);
            Assert.That(context.IsSessionSecured, Is.False);
            Assert.That(context.IsRoofExpectedOpen, Is.True);
        }

        // The roof is never opened while the monitor reads unsafe, and waiting that out is normal, so it doesn't count
        // towards the "cannot open" alert however many passes it takes.
        [Test]
        public async Task TheRoofIsNotOpenedWhileConditionsAreUnsafeAndItIsNotCountedAsAFailure() {
            FakeNina nina = ReadyToOpen();
            nina.SetSafety(false);
            OpenDome open = Open(nina.CreateContext());

            for (int pass = 0; pass < 3; pass++) {
                Assert.That(await StateRunner.RunAsync(open), Is.EqualTo(AutopilotStateResult.Failed));
            }

            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
            Assert.That(nina.LogMessages("WARNING"), Does.Contain("Tried to open dome shutter when unsafe"));
        }

        // Safe at dusk, the roof opens, and the night remembers it opened it, so the secure closes it later even if the
        // mode changes. The second open of a night says why it is opening again.
        [Test]
        public async Task TheRoofOpensWhenSafeAndTheNightRemembersItOpenedIt() {
            FakeNina nina = ReadyToOpen();
            Context context = nina.CreateContext();
            context.IsSessionSecured = true;
            OpenDome open = Open(context);

            AutopilotStateResult result = await StateRunner.RunAsync(open);

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "dome: OpenShutter" }));
            Assert.Multiple(() => {
                Assert.That(context.HasOpenedRoofTonight, Is.True);
                Assert.That(context.IsRoofExpectedOpen, Is.True);
                Assert.That(context.IsSessionSecured, Is.False);
                Assert.That(nina.LogMessages("INFO"), Does.Contain("Opening roof (the night is starting)."));
            });

            nina.SetRoof(ShutterState.ShutterClosed);
            await StateRunner.RunAsync(open);
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Opening roof (conditions are safe again)."));
        }

        // A roof that won't open fails the same way every pass, all night, so the third failure in a row sends one alert
        // and the rest stay as log lines.
        [Test]
        public async Task ARoofThatDoesNotOpenFailsAndTheThirdFailureInARowAlertsOnce() {
            FakeNina nina = ReadyToOpen();
            nina.RoofOpenLeavesTheShutter(ShutterState.ShutterClosed);
            OpenDome open = Open(nina.CreateContext());

            for (int pass = 1; pass <= 4; pass++) {
                Assert.That(await StateRunner.RunAsync(open), Is.EqualTo(AutopilotStateResult.Failed), $"pass {pass}");
                Assert.That(nina.Alerts, Has.Count.EqualTo(pass >= 3 ? 1 : 0), $"alerts after pass {pass}");
            }

            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Autopilot cannot open the roof."));
            Assert.That(alert.Detail("reason"), Is.EqualTo("the roof did not open"));
            Assert.That(alert.Detail("attempts"), Is.EqualTo(3));
            Assert.That(alert.Detail("roof"), Is.EqualTo("ShutterClosed"));
        }

        // A dome that isn't connected can't open, and the open fails before any park it could never use.
        [Test]
        public async Task ADisconnectedDomeFailsTheOpen() {
            FakeNina nina = ReadyToOpen();
            nina.SetRoof(null);

            AutopilotStateResult result = await StateRunner.RunAsync(Open(nina.CreateContext()));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Failed));
            Assert.That(nina.LogMessages("ERROR"), Does.Contain("Dome is not connected, so cannot open the shutter."));
        }

        // With N.I.N.A. refusing shutter moves over an unparked mount, the open can't succeed and the plugin won't park
        // under a closed roof unless told the park position is safe there. A stalled night can be recovered; bent metal can't.
        [Test]
        public async Task WithNinaRefusingShutterMovesAnUnparkedMountBlocksTheOpen() {
            FakeNina nina = ReadyToOpen();
            nina.SetNinaRefusesShutterMoveWithUnparkedMount(true);

            AutopilotStateResult result = await StateRunner.RunAsync(Open(nina.CreateContext()));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Failed));
            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.LogMessages("ERROR"),
                Has.Some.StartWith("The roof will not open: N.I.N.A. refuses shutter moves with the mount unparked. Park the mount, or turn on"));
        }

        // When the park position is safe under the closed roof, the mount is parked first so N.I.N.A. will open it.
        [Test]
        public async Task WithNinaRefusingShutterMovesTheMountIsParkedFirstWhenItMayMoveUnderTheRoof() {
            FakeNina nina = ReadyToOpen();
            nina.SetNinaRefusesShutterMoveWithUnparkedMount(true);
            nina.Settings.SetCanMountMoveUnderClosedRoof(true);
            nina.MountParksAtOnce();

            AutopilotStateResult result = await StateRunner.RunAsync(Open(nina.CreateContext()));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park", "dome: OpenShutter" }));
            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Parking the mount under the closed roof so N.I.N.A. will open it ('Mount can move under closed roof' is on)."));
        }

        // A mount already parked needs nothing before the open.
        [Test]
        public async Task WithNinaRefusingShutterMovesAParkedMountNeedsNoPark() {
            FakeNina nina = ReadyToOpen();
            nina.SetNinaRefusesShutterMoveWithUnparkedMount(true);
            nina.SetMount(isConnected: true, isParked: true);

            Assert.That(await StateRunner.RunAsync(Open(nina.CreateContext())), Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "dome: OpenShutter" }));
        }

        // A park that doesn't end at AtPark leaves the roof shut, since N.I.N.A. would refuse the open anyway.
        [Test]
        public async Task WithNinaRefusingShutterMovesAMountThatWillNotParkBlocksTheOpen() {
            FakeNina nina = ReadyToOpen();
            nina.SetNinaRefusesShutterMoveWithUnparkedMount(true);
            nina.Settings.SetCanMountMoveUnderClosedRoof(true);
            nina.MountParkAnswers(true);

            AutopilotStateResult result = await StateRunner.RunAsync(Open(nina.CreateContext()));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Failed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park" }));
            Assert.That(nina.LogMessages("ERROR"), Does.Contain(
                "Mount does not report AtPark after the park attempt. Not opening the roof, as N.I.N.A. would refuse it anyway. Check the mount."));
        }
    }
}