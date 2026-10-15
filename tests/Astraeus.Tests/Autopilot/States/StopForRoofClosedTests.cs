using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States {
    [TestFixture]
    public class StopForRoofClosedTests {
        private const string Headline = "Roof closed while the autopilot was imaging. The mount was stopped, not parked.";

        // Mid-exposure under an operated roof that has just started closing: camera, guider and mount all busy.
        private static FakeNina ImagingAsTheRoofCloses(ShutterState? shutter = ShutterState.ShutterClosing) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(shutter);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.SetGuider(isConnected: true);
            nina.SetCamera(isConnected: true);
            return nina;
        }

        private static Context ImagingContext(FakeNina nina) {
            Context context = nina.CreateContext();
            context.IsRoofExpectedOpen = true;
            context.IsRoofUnderAutopilotControl = true;
            return context;
        }

        private static Task<AutopilotStateResult> RunAsync(Context context) =>
            StateRunner.RunAsync(new StopForRoofClosed("Stop (Roof Closed)", context));

        // Under Operate only the autopilot should move this roof, so a roof closing on its own stops the exposure, the
        // guider and the mount, in that order, and disables with an alert. The mount is stopped, never parked under a
        // moving roof.
        [Test]
        public async Task UnderOperateARoofClosingOnItsOwnStopsEverythingAndDisablesWithAnAlert() {
            FakeNina nina = ImagingAsTheRoofCloses();
            Context context = ImagingContext(nina);

            AutopilotStateResult result = await RunAsync(context);

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Failed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "camera: AbortExposure", "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.Multiple(() => {
                Assert.That(alert.Message, Is.EqualTo(Headline));
                Assert.That(alert.Detail("shutter"), Is.EqualTo("ShutterClosing"));
                Assert.That(alert.Detail("roof_mode"), Is.EqualTo("Operate"));
                Assert.That(alert.Detail("mount"), Is.EqualTo("stopped where it was, not parked"));
                Assert.That(alert.Detail("action"), Is.EqualTo("autopilot disabled (check the roof and the mount before imaging again)"));
                Assert.That(context.SessionEndReason, Is.EqualTo(
                    "The roof closed on its own while imaging (shutter ShutterClosing). The mount was stopped where it was, not parked."));
                Assert.That(context.IsRoofUnderAutopilotControl, Is.False, "the dome's watchdog takes the roof back");
                Assert.That(context.IsRoofExpectedOpen, Is.False);
            });
        }

        // A roof that isn't ours closing is treated like unsafe conditions: nothing is stopped here, and the emergency
        // secure that follows parks and waits for the roof to open again.
        [Test]
        public async Task UnderWaitForOpenTheRoofClosingHandsOverToTheEmergencySecure() {
            FakeNina nina = ImagingAsTheRoofCloses();
            nina.Settings.SetAutopilotRoofMode(RoofMode.WaitForOpen);
            Context context = ImagingContext(nina);

            AutopilotStateResult result = await RunAsync(context);

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
            Assert.That(context.SessionEndReason, Is.Null);
        }

        // A dome that dropped off reads as "not open", so it gets one reconnect first. If the roof still reads open it was
        // never closed, and the night carries on from the next poll with nothing stopped.
        [Test]
        public async Task ADomeThatDroppedAndReconnectsWithTheRoofOpenResumesImaging() {
            FakeNina nina = ImagingAsTheRoofCloses(shutter: null);
            Context context = ImagingContext(nina);
            nina.LetDomeReconnect(context, () => {
                nina.SetRoof(ShutterState.ShutterOpen);
                return true;
            });

            AutopilotStateResult result = await RunAsync(context);

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Resumed));
            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
            Assert.That(nina.LogMessages("WARNING"),
                Does.Contain("The dome dropped off and has reconnected with the roof still open, so carrying on imaging."));
        }

        // A dome that stays gone leaves the roof unknown, and unknown is not open, so under Operate it is the roof-closed stop
        // with the shutter reported as disconnected.
        [Test]
        public async Task ADomeThatStaysGoneUnderOperateIsTreatedAsAClosedRoof() {
            FakeNina nina = ImagingAsTheRoofCloses(shutter: null);
            Context context = ImagingContext(nina);
            nina.LetDomeReconnect(context, () => false);

            AutopilotStateResult result = await RunAsync(context);

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Failed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "camera: AbortExposure", "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.Alerts.Select(alert => alert.Message), Is.EqualTo(new[] { "Autopilot: Dome is not connected.", Headline }));
            Assert.That(nina.Alerts.Last().Detail("shutter"), Is.EqualTo("disconnected"));
        }

        // A mount already parked is left parked and reported so, since there is nothing to stop.
        [Test]
        public async Task AParkedMountIsLeftParked() {
            FakeNina nina = ImagingAsTheRoofCloses();
            nina.SetMount(isConnected: true, isParked: true);

            await RunAsync(ImagingContext(nina));

            Assert.That(nina.Commands, Is.EqualTo(new[] { "camera: AbortExposure", "guider: StopGuiding" }));
            Assert.That(nina.Alerts.Single().Detail("mount"), Is.EqualTo("parked"));
        }

        // The night ends here, so a cover that may move under the closed roof gets its end-of-night close.
        [Test]
        public async Task ACoverThatMayMoveUnderTheClosedRoofIsClosed() {
            FakeNina nina = ImagingAsTheRoofCloses();
            nina.Settings.SetAutopilotFlatPanelMode(FlatPanelMode.OpenAndClose);
            nina.Settings.SetShouldOperateFlatPanelWithRoofClosed(true);
            nina.SetFlatPanel(CoverState.Open);

            await RunAsync(ImagingContext(nina));

            Assert.That(nina.Commands.Last(), Is.EqualTo("flat panel: CloseCover"));
        }

        // A frame the server already has as CAPTURING is reported failed, with why, so it doesn't sit there until a timeout.
        [Test]
        public async Task AStartedFrameIsReportedFailedToTheServer() {
            FakeNina nina = ImagingAsTheRoofCloses();
            Context context = ImagingContext(nina);
            context.CurrentTarget = new AutopilotNextResponse { ImageId = 8812 };
            context.IsCurrentTargetStarted = true;
            context.CurrentClientRef = "ref-1";
            nina.Server.Reply("POST", "/api/autopilot/images/8812/complete/", "{}");

            await RunAsync(context);

            JsonElement body = Json.Parse(nina.Server.Requests.Single().Body!);
            Assert.That(body.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("reason").GetString(), Is.EqualTo("cycle aborted: the roof closed"));
            Assert.That(body.GetProperty("client_ref").GetString(), Is.EqualTo("ref-1"));
            Assert.That(context.IsCurrentTargetStarted, Is.False);
        }
    }
}