using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    // What the machine does on a manual stop and on a fault, on the same fixture as the routing.
    public partial class StateMachineTests {
        // Five routes that end the run, for tests that never take them.
        private static StateMachine.AbortRoutes DeadEnds(List<string> trace) => new StateMachine.AbortRoutes(
            new FakeState("Unsafe route", trace, AutopilotStateResult.Disabled),
            new FakeState("Limit route", trace, AutopilotStateResult.Disabled),
            new FakeState("Window route", trace, AutopilotStateResult.Disabled),
            new FakeState("Roof route", trace, AutopilotStateResult.Disabled),
            new FakeState("Fault route", trace, AutopilotStateResult.Disabled));

        // A state that runs until the machine is stopped, like an exposure.
        private static FakeState Imaging(List<string> trace) => new FakeState("Imaging", trace, async token => {
            await Task.Delay(Timeout.Infinite, token);
            return AutopilotStateResult.Completed;
        });

        private static FakeState Throwing(List<string> trace, string message) =>
            new FakeState("Throwing", trace, _ => Task.FromException<AutopilotStateResult>(new InvalidOperationException(message)));

        // Switches the autopilot off while the state runs, then waits for the machine to wind down.
        private static async Task StopAsync(FakeNina nina, Context context, StateMachine.AbortRoutes? routes = null) {
            List<string> trace = new List<string>();
            using CancellationTokenSource machine = new CancellationTokenSource();
            Task run = new StateMachine(context).RunAsync(Imaging(trace), routes ?? DeadEnds(trace), machine.Token);
            await Task.Delay(1);
            machine.Cancel();
            await run.WaitAsync(Soon);
        }

        private static FakeNina OperatedRoofNight(ShutterState? shutter) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(shutter);
            return nina;
        }

        ///////////// The manual stop /////////////

        // Someone switching off is usually taking manual control, so the stop ends the exposure, the guiding and the
        // tracking and nothing else, and the log says what was left unsecured and that nothing will close the roof.
        [Test]
        public async Task AManualStopStopsTheExposureGuidingAndTrackingAndWarnsWhatIsLeft() {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.SetGuider(isConnected: true);
            nina.SetCamera(isConnected: true);

            await StopAsync(nina, nina.CreateContext());

            Assert.That(nina.Commands, Is.EqualTo(new[] { "camera: AbortExposure", "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Autopilot stopped with the mount NOT parked and the roof NOT closed. Secure the observatory if you are done. " +
                "N.I.N.A.'s 'Close on unsafe' is off, so nothing will close the roof if conditions turn."));
            Assert.That(nina.Alerts, Is.Empty, "the session-end email carries the warning, not a separate alert");
        }

        // A roof the autopilot works but can't read is named like an open one rather than left out, since unread isn't closed.
        [Test]
        public async Task AManualStopNamesARoofItCannotRead() {
            FakeNina nina = OperatedRoofNight(null);
            nina.SetMount(isConnected: true, isTracking: true);
            Context context = nina.CreateContext();
            context.HasOpenedRoofTonight = true;

            await StopAsync(nina, context);

            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Autopilot stopped with the mount NOT parked and the roof state unknown (dome not connected). Secure the observatory if you are done."));
        }

        // A mount that isn't connected is named too, since it can't be confirmed parked.
        [Test]
        public async Task AManualStopNamesADisconnectedMount() {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: false);

            await StopAsync(nina, nina.CreateContext());

            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Autopilot stopped with the mount not connected. Secure the observatory if you are done."));
        }

        // With the mount parked and the roof closed there is nothing to warn about.
        [Test]
        public async Task AManualStopWithEverythingParkedAndClosedIsAPlainLine() {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: true, isParked: true);

            await StopAsync(nina, nina.CreateContext());

            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Autopilot stopped with the mount parked and the roof closed."));
            Assert.That(nina.LogMessages("WARNING"), Is.Empty);
        }

        // After an earlier secure that still holds, the stop says so with the time, so nobody goes out to check.
        [Test]
        public async Task AManualStopAfterASecureThatStillHoldsSaysSo() {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: true, isParked: true);
            Context context = nina.CreateContext();
            context.IsSessionSecured = true;
            context.SessionSecuredAtUtc = context.UtcNow;

            await StopAsync(nina, context);

            string line = nina.LogMessages("INFO").Single(message => message.StartsWith("Autopilot stopped:"));
            Assert.That(line, Does.StartWith("Autopilot stopped: the mount was parked and the roof closed at "));
            Assert.That(line, Does.EndWith(", and both are as they were left."));
        }

        // A frame the server has as CAPTURING is reported failed on the way out, after the hardware, so the mount is never
        // left running while a web call waits.
        [Test]
        public async Task AManualStopReportsAStartedFrameFailedToTheServer() {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            Context context = nina.CreateContext();
            context.CurrentTarget = new AutopilotNextResponse { ImageId = 8812 };
            context.IsCurrentTargetStarted = true;
            context.CurrentClientRef = "ref-1";
            nina.Server.Reply("POST", "/api/autopilot/images/8812/complete/", "{}");

            await StopAsync(nina, context);

            JsonElement body = Json.Parse(nina.Server.Requests.Single().Body!);
            Assert.That(body.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("reason").GetString(), Is.EqualTo("cycle aborted: the autopilot was stopped"));
            Assert.That(nina.Commands.Last(), Is.EqualTo("mount: SetTrackingEnabled(false)"), "the mount before the web call");
        }

        ///////////// The fault path /////////////

        // A state that throws skips the normal transitions, so the machine records the cause for the session-end email and
        // runs the fault secure on a fresh token, which parks and closes with the real drivers.
        [Test]
        public async Task AFaultingStateIsSecuredByTheRealSecureAndTheCauseRecorded() {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.MountParksAtOnce();
            nina.RoofClosesAtOnce();
            Context context = nina.CreateContext();
            List<string> trace = new List<string>();
            StateMachine.AbortRoutes routes = DeadEnds(trace) with {
                Fault = new SecureObservatory("Secure (Fault)", context, "unrecoverable fault", isFault: true),
            };

            await new StateMachine(context).RunAsync(Throwing(trace, "driver exploded"), routes, CancellationToken.None).WaitAsync(Soon);

            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park", "dome: CloseShutter" }));
            Assert.That(context.SessionEndReason, Is.EqualTo("Unexpected error in the autopilot machine.\ndriver exploded"));
            Assert.That(context.IsSessionSecured, Is.True);
            Assert.That(nina.LogMessages("WARNING"), Does.Contain("Securing the observatory because of an unexpected error (driver exploded)."));
            Assert.That(nina.Alerts, Is.Empty, "the session-end email reports the fault");
        }

        // The fault secure has a backstop a minute past the secure's own timeout. One that hangs past it is reported, with
        // the budget, so a stuck secure is never silent.
        [Test]
        public async Task AFaultSecureThatHangsIsReportedAfterItsBackstop() {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();
            List<string> trace = new List<string>();
            StateMachine.AbortRoutes routes = DeadEnds(trace) with { Fault = Imaging(trace) };

            Task run = new StateMachine(context).RunAsync(Throwing(trace, "boom"), routes, CancellationToken.None);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60)), Is.True);

            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Securing the observatory did not finish in time. Check the mount and roof manually."));
            Assert.That(alert.Detail("timeout"), Is.EqualTo("56 minutes"));
        }

        // A fault secure that itself throws is reported as failed with the error, since by then nothing else will secure the rig.
        [Test]
        public async Task AFaultSecureThatThrowsIsReportedAsFailed() {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();
            List<string> trace = new List<string>();
            StateMachine.AbortRoutes routes = DeadEnds(trace) with { Fault = Throwing(trace, "no mount") };

            await new StateMachine(context).RunAsync(Throwing(trace, "boom"), routes, CancellationToken.None).WaitAsync(Soon);

            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Securing the observatory failed. Check the mount and roof manually.\nno mount"));
            Assert.That(alert.Detail("error"), Is.EqualTo("no mount"));
        }
    }
}