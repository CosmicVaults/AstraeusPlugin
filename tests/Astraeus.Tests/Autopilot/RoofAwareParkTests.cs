using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using Moq;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    [TestFixture]
    public class RoofAwareParkTests {
        private const string Reason = "conditions unsafe";

        // A cap on waits that should end at once, so a regression fails instead of hanging the run.
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        // A night under an operated roof with the mount tracking, which is how most secures find things.
        private static FakeNina TrackingUnderAnOperatedRoof(ShutterState? shutter) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.SetRoof(shutter);
            return nina;
        }

        private static RoofAwarePark Park(FakeNina nina) => new RoofAwarePark(nina.CreateContext(), isEmergency: true);

        // The night under way with auto-connect on and a dome configured, so a dropped dome gets the autopilot's one
        // reconnect, answered by the given connect.
        private static void DomeReconnects(FakeNina nina, Context context, Func<bool> connect) {
            context.IsStartupComplete = true;
            nina.Settings.SetShouldAutoConnectEquipment(true);
            nina.Dome.SetDefaultDevice("Dome 1", "test");
            nina.Dome.ConnectDevice = _ => Task.FromResult(connect());
        }

        ///////////// IsRoofClearanceRequired /////////////

        // The roof check applies whenever a roof is in play: Operate, Wait-for-open, or No roof after this autopilot opened one
        // tonight. "Mount can move under closed roof" switches it off in every mode, since it says the park is safe regardless.
        [TestCase(RoofMode.Operate, false, false, true)]
        [TestCase(RoofMode.WaitForOpen, false, false, true)]
        [TestCase(RoofMode.Ignore, false, false, false)]
        [TestCase(RoofMode.Ignore, true, false, true)]
        [TestCase(RoofMode.Operate, false, true, false)]
        [TestCase(RoofMode.WaitForOpen, false, true, false)]
        [TestCase(RoofMode.Ignore, true, true, false)]
        public void TheRoofCheckAppliesUnlessThereIsNoRoofOrTheMountMayMoveUnderIt(RoofMode mode, bool hasOpenedRoofTonight,
            bool canMoveUnderClosedRoof, bool isRequired) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(mode);
            nina.Settings.SetCanMountMoveUnderClosedRoof(canMoveUnderClosedRoof);
            Context context = nina.CreateContext();
            context.HasOpenedRoofTonight = hasOpenedRoofTonight;

            RoofAwarePark park = new RoofAwarePark(context, isEmergency: false);

            Assert.That(park.IsRoofClearanceRequired, Is.EqualTo(isRequired));
        }

        ///////////// Before the park /////////////

        // A mount already parked needs no park and no roof check, so a secure at dawn after an earlier park is quiet.
        [Test]
        public async Task AParkedMountNeedsNoParkAndNoRoofCheck() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: true, isParked: true);

            RoofAwarePark.Outcome outcome = await Park(nina).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
        }

        // A mount that isn't connected can't be parked or stopped, so the secure learns that at once rather than after a wait.
        [Test]
        public async Task ADisconnectedMountIsNotParked() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: false);

            RoofAwarePark.Outcome outcome = await Park(nina).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.NotParked));
            Assert.That(nina.LogMessages("WARNING"),
                Is.EqualTo(new[] { "Cannot park scope (conditions unsafe). The mount is not connected." }));
        }

        // The park slews the mount a long way, so it is refused under any roof that doesn't read Open: closing, closed, in
        // error, unreported, or a dome that isn't connected. The mount is stopped where it is, and one alert says why.
        [TestCase(ShutterState.ShutterClosing, "Mount NOT parked because the roof is not open. The mount was stopped where it was.")]
        [TestCase(ShutterState.ShutterClosed, "Mount NOT parked because the roof is not open. The mount was stopped where it was.")]
        [TestCase(ShutterState.ShutterError, "Mount NOT parked because the roof is not open. The mount was stopped where it was.")]
        [TestCase(ShutterState.ShutterNone, "Mount NOT parked because the roof is not open. The mount was stopped where it was.")]
        [TestCase(null, "Mount NOT parked because the roof state is unknown (dome not connected). The mount was stopped where it was.")]
        public async Task AParkUnderARoofNotReadingOpenIsRefusedWithTheMountStopped(ShutterState? shutter, string headline) {
            FakeNina nina = TrackingUnderAnOperatedRoof(shutter);

            RoofAwarePark.Outcome outcome = await Park(nina).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.RoofNotOpen));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.Alerts.Select(alert => alert.Message), Is.EqualTo(new[] { headline }));
            Assert.That(nina.LogMessages("WARNING"), Does.Contain("Mount is not parked. Tracking stopped so it stays where it is."));
        }

        // The alert carries what someone needs to act on: the mount's state, the roof's reading and the mode, and whether the
        // night is waiting to resume or has ended, which is the difference between "wait" and "go out there".
        [TestCase(true, "waiting for safe conditions and for the roof to open again")]
        [TestCase(false, "the night has ended; park the mount by hand once the roof is open")]
        public async Task TheRefusalAlertDescribesTheRigAndWhatHappensNext(bool isEmergency, string action) {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterClosed);

            await new RoofAwarePark(nina.CreateContext(), isEmergency).RunAsync(Reason, CancellationToken.None);

            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.Multiple(() => {
                Assert.That(alert.Detail("reason"), Is.EqualTo(Reason));
                Assert.That(alert.Detail("mount"), Is.EqualTo("stopped"));
                Assert.That(alert.Detail("roof"), Is.EqualTo("ShutterClosed"));
                Assert.That(alert.Detail("roof_mode"), Is.EqualTo("Operate"));
                Assert.That(alert.Detail("nina_close_on_unsafe"), Is.EqualTo("off"));
                Assert.That(alert.Detail("action"), Is.EqualTo(action));
            });
        }

        // A closed roof over a mount that is already still, as at dawn after an earlier refusal, is a warning in the log and
        // nothing more: there is nothing to stop and nobody to wake.
        [Test]
        public async Task AClosedRoofOverARestingMountIsAWarningOnly() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: true, isTracking: false);

            RoofAwarePark.Outcome outcome = await Park(nina).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.RoofNotOpen));
            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
            Assert.That(nina.LogMessages("WARNING"),
                Is.EqualTo(new[] { "Not parking, because the roof is closed (conditions unsafe). The mount was already still." }));
        }

        // A roof caught opening gets time to finish, polled on the clock, and the park goes ahead once it reads Open.
        [Test]
        public async Task ARoofCaughtOpeningIsWaitedForAndThenParkedUnder() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpening);
            nina.MountParksAtOnce();

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, CancellationToken.None);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), Is.False, "still opening");
            nina.SetRoof(ShutterState.ShutterOpen);

            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await run, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park" }));
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Roof is opening, so waiting for it to finish before parking."));
        }

        // A roof still opening after two minutes is not going to open, so the park is refused rather than waited on all night.
        [Test]
        public async Task ARoofStillOpeningAfterTwoMinutesIsRefused() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpening);

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, CancellationToken.None);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3)), Is.True);

            Assert.That(await run, Is.EqualTo(RoofAwarePark.Outcome.RoofNotOpen));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.Alerts.Single().Detail("roof"), Is.EqualTo("ShutterOpening"));
        }

        // N.I.N.A. may park the mount itself just before it closes the roof, so a mount found parked at the refusal counts
        // as parked, with no stop and no alert.
        [Test]
        public async Task AMountFoundParkedAtTheRefusalIsParked() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpening);

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, CancellationToken.None);
            await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            nina.SetMount(isConnected: true, isParked: true);
            nina.SetRoof(ShutterState.ShutterClosed);

            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await run, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
        }

        ///////////// The park /////////////

        // Under a roof that reads Open the park runs, and AtPark decides it worked, not the driver's answer.
        [Test]
        public async Task UnderAnOpenRoofTheParkRunsAndAtParkConfirmsIt() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountParksAtOnce();

            RoofAwarePark.Outcome outcome = await Park(nina).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park" }));
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Scope parked in 0.0s (conditions unsafe)."));
            Assert.That(nina.Alerts, Is.Empty);
        }

        // With no roof there is no roof check, so a pier with nothing overhead parks whatever the dome reads.
        [Test]
        public async Task WithNoRoofTheParkRunsWithoutARoofCheck() {
            FakeNina nina = TrackingUnderAnOperatedRoof(null);
            nina.Settings.SetAutopilotRoofMode(RoofMode.Ignore);
            nina.MountParksAtOnce();

            Assert.That(await Park(nina).RunAsync(Reason, CancellationToken.None), Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park" }));
        }

        // "Mount can move under closed roof" says the park position is safe with the roof shut, so the park runs as it did
        // before the roof check existed.
        [Test]
        public async Task WithTheMountAllowedUnderAClosedRoofTheParkRunsAsBefore() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterClosed);
            nina.Settings.SetCanMountMoveUnderClosedRoof(true);
            nina.MountParksAtOnce();

            Assert.That(await Park(nina).RunAsync(Reason, CancellationToken.None), Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park" }));
            Assert.That(nina.Alerts, Is.Empty);
        }

        // The driver's answer is not trusted either way: a park that says it worked without AtPark is not parked, and one that
        // says it failed while the mount reads AtPark is. The roof may only move over a mount that is really at park.
        [TestCase(true, false, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        public async Task AtParkDecidesNotTheDriversAnswer(bool answer, bool isAtPark, bool isParked) {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountParkAnswers(answer, isAtPark);
            RoofAwarePark.Outcome expected = isParked ? RoofAwarePark.Outcome.Parked : RoofAwarePark.Outcome.NotParked;

            Assert.That(await Park(nina).RunAsync(Reason, CancellationToken.None), Is.EqualTo(expected));
            Assert.That(nina.Alerts, Is.Empty, "not parked is the secure's problem, not a roof alert");
        }

        // A driver that throws on park is reported as not parked and logged, so the secure goes on to stop the mount instead
        // of faulting out with the roof still open.
        [Test]
        public async Task AParkThatThrowsIsNotParkedAndLogged() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.TelescopeMediator
                .Setup(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("driver busy"));

            Assert.That(await Park(nina).RunAsync(Reason, CancellationToken.None), Is.EqualTo(RoofAwarePark.Outcome.NotParked));
            Assert.That(nina.LogMessages("ERROR"), Is.EqualTo(new[] { "Failed to park scope: driver busy" }));
        }

        ///////////// During the park /////////////

        // A roof that stops reading open mid-park gets the mount stopped first and the park cancelled second, since cancelling
        // only stops N.I.N.A. waiting on the park. The alert says the park stopped part-way.
        [Test]
        public async Task ARoofThatStopsReadingOpenMidParkStopsTheMountAndAlerts() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountParksSlowly();

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, CancellationToken.None);
            nina.SetRoof(ShutterState.ShutterClosing);

            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), Is.True);
            Assert.That(await run, Is.EqualTo(RoofAwarePark.Outcome.RoofNotOpen));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park", "mount: StopSlew", "mount: SetTrackingEnabled(false)" }));
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Park stopped part-way because the roof stopped reading open. Check the mount and roof."));
            Assert.That(alert.Detail("mount"), Is.EqualTo("stopped"));
        }

        // A roof that stops reading open with the mount already at park is no emergency: the park is left to finish and counts.
        [Test]
        public async Task ARoofThatStopsReadingOpenWithTheMountAtParkIsParked() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountParksSlowly();

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, CancellationToken.None);
            nina.TelescopeInfo.AtPark = true;
            nina.SetRoof(ShutterState.ShutterClosing);
            await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
            nina.FinishPark();

            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await run, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park" }));
            Assert.That(nina.Alerts, Is.Empty);
        }

        // N.I.N.A.'s own park can return before the mount reaches the position, so a park call that comes back with the mount
        // still slewing is given time to finish rather than being stopped part-way.
        [Test]
        public async Task AParkStillUnderWayWhenTheCallReturnsIsGivenTimeToFinish() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountParkAnswers(true, isSlewing: true);

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, CancellationToken.None);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)), Is.False, "still slewing");
            nina.SetMount(isConnected: true, isParked: true);

            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await run, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.LogMessages("INFO"),
                Does.Contain("The park call returned with the mount still moving, so waiting for it to reach park."));
        }

        // That wait is still under the roof watch: if the roof stops reading open before the mount gets there, the mount is
        // stopped and the alert says the park stopped part-way.
        [Test]
        public async Task AParkStillUnderWayIsStoppedIfTheRoofStopsReadingOpen() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountParkAnswers(true, isSlewing: true);

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, CancellationToken.None);
            nina.SetRoof(ShutterState.ShutterClosing);

            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(await run, Is.EqualTo(RoofAwarePark.Outcome.RoofNotOpen));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park", "mount: StopSlew", "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.Alerts.Single().Message, Does.StartWith("Park stopped part-way"));
        }

        // The secure's clock can run out mid-park. The park is reported as not finished and the cancellation goes up to the
        // secure, which stops the mount. No roof alert, since the roof did nothing wrong.
        [Test]
        public async Task AParkCutOffByTheSecuresClockIsNotParkedAndTheCancellationGoesUp() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountParksSlowly();
            using CancellationTokenSource secureClock = new CancellationTokenSource();

            Task<RoofAwarePark.Outcome> run = Park(nina).RunAsync(Reason, secureClock.Token);
            await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
            secureClock.Cancel();

            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => run.WaitAsync(Soon));
            Assert.That(nina.LogMessages("WARNING"), Has.Some.StartWith("Park did not finish after "));
            Assert.That(nina.Alerts, Is.Empty);
        }

        ///////////// The retry /////////////

        // A roof that reads open again after a refusal, like one that finished opening while the mount was being stopped, gets
        // one more go, and a park that then works leaves no alert behind.
        [Test]
        public async Task ARoofThatReadsOpenAgainGetsOneMoreGoAndNoAlert() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterClosed);
            nina.MountParksAtOnce();
            nina.CommandIssued += command => {
                if (command == "mount: SetTrackingEnabled(false)") nina.SetRoof(ShutterState.ShutterOpen);
            };

            RoofAwarePark.Outcome outcome = await Park(nina).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: SetTrackingEnabled(false)", "mount: Park" }));
            Assert.That(nina.Alerts, Is.Empty);
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Roof reads open again, so trying the park once more (conditions unsafe)."));
        }

        // A dome that dropped off gets the autopilot's one reconnect before the roof is judged. One that comes back with the
        // roof open is parked under as if it had never gone.
        [Test]
        public async Task ADomeThatReconnectsWithTheRoofOpenIsParkedUnder() {
            FakeNina nina = TrackingUnderAnOperatedRoof(null);
            Context context = nina.CreateContext();
            DomeReconnects(nina, context, () => {
                nina.SetRoof(ShutterState.ShutterOpen);
                return true;
            });
            nina.MountParksAtOnce();

            RoofAwarePark.Outcome outcome = await new RoofAwarePark(context, isEmergency: true).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.Parked));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: Park" }));
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Dome reconnected."));
            Assert.That(nina.Alerts, Is.Empty);
        }

        // A dome that stays gone after its reconnect leaves the roof unknown, and unknown is never open, so the park is
        // refused with the "dome not connected" alert on top of the device-lost one.
        [Test]
        public async Task ADomeStillGoneAfterItsReconnectIsRefusedAsUnknown() {
            FakeNina nina = TrackingUnderAnOperatedRoof(null);
            Context context = nina.CreateContext();
            DomeReconnects(nina, context, () => false);

            RoofAwarePark.Outcome outcome = await new RoofAwarePark(context, isEmergency: true).RunAsync(Reason, CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(RoofAwarePark.Outcome.RoofNotOpen));
            Assert.That(nina.Alerts.Select(alert => alert.Message), Is.EqualTo(new[] {
                "Autopilot: Dome is not connected.",
                "Mount NOT parked because the roof state is unknown (dome not connected). The mount was stopped where it was.",
            }));
        }

        ///////////// StopUnparkedMountAsync /////////////

        // A mount the secure couldn't park is stopped where it is. Only a secure that ran out of time, and may have cut a park
        // off, sends the extra StopSlew, since a park in progress doesn't always report Slewing.
        [TestCase(false, new[] { "mount: SetTrackingEnabled(false)" })]
        [TestCase(true, new[] { "mount: StopSlew", "mount: SetTrackingEnabled(false)" })]
        public async Task AnUnparkedMountIsStoppedWithAnExtraStopSlewOnlyWhenOutOfTime(bool isOutOfTime, string[] commands) {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);

            await Park(nina).StopUnparkedMountAsync(isOutOfTime);

            Assert.That(nina.Commands, Is.EqualTo(commands));
            Assert.That(nina.LogMessages("WARNING"), Is.EqualTo(new[] { "Mount is not parked. Tracking stopped so it stays where it is." }));
        }

        // A disconnected mount can't be stopped, and the stop says nothing rather than faulting the secure.
        [Test]
        public async Task StoppingADisconnectedMountDoesNothing() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: false);

            await Park(nina).StopUnparkedMountAsync(isOutOfTime: false);

            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.LogMessages("WARNING"), Is.Empty);
            Assert.That(nina.LogMessages("ERROR"), Is.Empty);
        }

        // A mount that won't confirm it stopped is an error line, since the roof must not close over a mount still moving.
        [Test]
        public async Task AMountThatWillNotConfirmItsStopIsAnError() {
            FakeNina nina = TrackingUnderAnOperatedRoof(ShutterState.ShutterOpen);
            nina.MountIgnoresTrackingStops();

            Task stop = Park(nina).StopUnparkedMountAsync(isOutOfTime: false);
            Assert.That(await nina.AdvanceUntilAsync(stop, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30)), Is.True);

            Assert.That(nina.LogMessages("ERROR"), Is.EqualTo(new[] { "Mount is not parked and did not confirm it stopped. Check the mount." }));
        }
    }
}