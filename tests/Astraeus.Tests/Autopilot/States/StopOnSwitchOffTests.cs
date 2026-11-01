using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States
{
    [TestFixture]
    public class StopOnSwitchOffTests
    {
        private static FakeNina OperatedRoofNight(ShutterState? shutter)
        {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(shutter);
            return nina;
        }

        // Runs the stop as the machine does on a switch-off: as an exit, on no token.
        private static Task<AutopilotStateResult> StopAsync(Context context) =>
            StateRunner.RunAsync(new StopOnSwitchOff("Stop (Switched Off)", context));

        // Someone switching off is usually taking manual control, so the stop ends the exposure, the guiding and the
        // tracking and nothing else, and the log says what was left unsecured and that nothing will close the roof.
        [Test]
        public async Task AManualStopStopsTheExposureGuidingAndTrackingAndWarnsWhatIsLeft()
        {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.SetGuider(isConnected: true);
            nina.SetCamera(isConnected: true);

            AutopilotStateResult result = await StopAsync(nina.CreateContext());

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "camera: AbortExposure", "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Autopilot stopped with the mount NOT parked and the roof NOT closed. Secure the observatory if you are done. " +
                "N.I.N.A.'s 'Close on unsafe' is off, so nothing will close the roof if conditions turn."));
            Assert.That(nina.Alerts, Is.Empty, "the session-end email carries the warning, not a separate alert");
        }

        // A roof the autopilot works but can't read is named like an open one rather than left out, since unread isn't closed.
        [Test]
        public async Task AManualStopNamesARoofItCannotRead()
        {
            FakeNina nina = OperatedRoofNight(null);
            nina.SetMount(isConnected: true, isTracking: true);
            Context context = nina.CreateContext();
            context.Roof.HasOpenedTonight = true;

            await StopAsync(context);

            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Autopilot stopped with the mount NOT parked and the roof state unknown (dome not connected). Secure the observatory if you are done."));
        }

        // A mount that isn't connected is named too, since it can't be confirmed parked.
        [Test]
        public async Task AManualStopNamesADisconnectedMount()
        {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: false);

            await StopAsync(nina.CreateContext());

            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Autopilot stopped with the mount not connected. Secure the observatory if you are done."));
        }

        // With the mount parked and the roof closed there is nothing to warn about.
        [Test]
        public async Task AManualStopWithEverythingParkedAndClosedIsAPlainLine()
        {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: true, isParked: true);

            await StopAsync(nina.CreateContext());

            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Autopilot stopped with the mount parked and the roof closed."));
            Assert.That(nina.LogMessages("WARNING"), Is.Empty);
        }

        // After an earlier secure that still holds, the stop says so with the time, so nobody goes out to check.
        [Test]
        public async Task AManualStopAfterASecureThatStillHoldsSaysSo()
        {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: true, isParked: true);
            Context context = nina.CreateContext();
            context.Night.IsSecured = true;
            context.Night.SecuredAtUtc = context.UtcNow;

            await StopAsync(context);

            string line = nina.LogMessages("INFO").Single(message => message.StartsWith("Autopilot stopped:"));
            Assert.That(line, Does.StartWith("Autopilot stopped: the mount was parked and the roof closed at "));
            Assert.That(line, Does.EndWith(", and both are as they were left."));
        }

        // A frame the server has as CAPTURING is reported failed on the way out, after the hardware, so the mount is never
        // left running while a web call waits.
        [Test]
        public async Task AManualStopReportsAStartedFrameFailedToTheServer()
        {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            Context context = nina.CreateContext();
            context.Frame = new Frame(context, new AutopilotNextResponse { ImageId = 8812 }) { IsStarted = true, ClientRef = "ref-1" };
            nina.Server.Reply("POST", "/api/autopilot/images/8812/complete/", "{}");

            await StopAsync(context);

            JsonElement body = Json.Parse(nina.Server.Requests.Single().Body!);
            Assert.That(body.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("reason").GetString(), Is.EqualTo("cycle aborted: the autopilot was stopped"));
            Assert.That(nina.Commands.Last(), Is.EqualTo("mount: SetTrackingEnabled(false)"), "the mount before the web call");
        }
    }
}
