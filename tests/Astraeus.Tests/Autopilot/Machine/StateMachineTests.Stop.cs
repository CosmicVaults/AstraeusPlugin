using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot
{
    // What the machine does on a fault, on the same fixture as the routing. The manual stop is a state of its own,
    // tested in StopOnSwitchOffTests.
    public partial class StateMachineTests
    {
        // Routes that end the run, for tests that never take them. Each test supplies its own entry state.
        private static MachineRoutes DeadEnds(List<string> trace) => new MachineRoutes(
            Entry: null!,
            new TransitionsTable()
                .On(AutopilotStateResult.Unsafe, new FakeState("Unsafe route", trace, AutopilotStateResult.Disabled))
                .On(AutopilotStateResult.RoofClosed, new FakeState("Roof route", trace, AutopilotStateResult.Disabled))
                .On(AutopilotStateResult.LimitReached, new FakeState("Limit route", trace, AutopilotStateResult.Disabled))
                .On(AutopilotStateResult.WindowClosed, new FakeState("Window route", trace, AutopilotStateResult.Disabled)),
            new FakeState("Fault route", trace, AutopilotStateResult.Disabled),
            new FakeState("Stop route", trace, AutopilotStateResult.Completed));

        private static FakeState Throwing(List<string> trace, string message) =>
            new FakeState("Throwing", trace, _ => Task.FromException<AutopilotStateResult>(new InvalidOperationException(message)));

        private static FakeNina OperatedRoofNight(ShutterState? shutter)
        {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(shutter);
            return nina;
        }

        ///////////// The fault path /////////////

        // A state that throws skips the normal transitions, so the machine records the cause for the session-end email and
        // runs the fault secure on a fresh token, which parks and closes with the real drivers.
        [Test]
        public async Task AFaultingStateIsSecuredByTheRealSecureAndTheCauseRecorded()
        {
            FakeNina nina = OperatedRoofNight(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.MountParksAtOnce();
            nina.RoofClosesAtOnce();
            Context context = nina.CreateContext();
            List<string> trace = new List<string>();
            MachineRoutes routes = DeadEnds(trace) with
            {
                Entry = Throwing(trace, "driver exploded"),
                SecureForFault = new SecureObservatory("Secure (Fault)", context, SecureKind.Fault, "unrecoverable fault"),
            };

            await new StateMachine(context).RunAsync(routes, CancellationToken.None).WaitAsync(Soon);

            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park", "dome: CloseShutter" }));
            Assert.That(context.Night.EndReason, Is.EqualTo("Unexpected error in the autopilot machine.\ndriver exploded"));
            Assert.That(context.Night.IsSecured, Is.True);
            Assert.That(nina.LogMessages("WARNING"), Does.Contain("Securing the observatory because of an unexpected error (driver exploded)."));
            Assert.That(nina.Alerts, Is.Empty, "the session-end email reports the fault");
        }

        // The fault secure has a backstop a minute past the secure's own timeout. One that hangs past it is reported, with
        // the budget, so a stuck secure is never silent. The backstop is armed on the clock as the secure is entered, so
        // the clock waits for the secure to start: moved earlier on a slow machine, it ran out before the backstop fired.
        [Test]
        public async Task AFaultSecureThatHangsIsReportedAfterItsBackstop()
        {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();
            List<string> trace = new List<string>();
            TaskCompletionSource secureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeState hangingSecure = new FakeState("Imaging", trace, async token =>
            {
                secureStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return AutopilotStateResult.Completed;
            });
            MachineRoutes routes = DeadEnds(trace) with { Entry = Throwing(trace, "boom"), SecureForFault = hangingSecure };

            Task run = new StateMachine(context).RunAsync(routes, CancellationToken.None);
            await secureStarted.Task.WaitAsync(Soon);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60)), Is.True);

            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Securing the observatory did not finish in time. Check the mount and roof manually."));
            Assert.That(alert.Detail("timeout"), Is.EqualTo("56 minutes"));
        }

        // A fault secure that itself throws is reported as failed with the error, since by then nothing else will secure the rig.
        [Test]
        public async Task AFaultSecureThatThrowsIsReportedAsFailed()
        {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();
            List<string> trace = new List<string>();
            MachineRoutes routes = DeadEnds(trace) with { Entry = Throwing(trace, "boom"), SecureForFault = Throwing(trace, "no mount") };

            await new StateMachine(context).RunAsync(routes, CancellationToken.None).WaitAsync(Soon);

            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Securing the observatory failed. Check the mount and roof manually.\nno mount"));
            Assert.That(alert.Detail("error"), Is.EqualTo("no mount"));
        }
    }
}
