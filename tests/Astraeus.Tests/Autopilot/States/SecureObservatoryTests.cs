using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States {
    [TestFixture]
    public class SecureObservatoryTests {
        // An imaging night under an operated roof: roof open, mount tracking, guider connected, a flat panel with its cover
        // open under OpenAndClose, and drivers that park and close at once.
        private static FakeNina ImagingNight() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.Settings.SetAutopilotFlatPanelMode(FlatPanelMode.OpenAndClose);
            nina.SetRoof(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.SetGuider(isConnected: true);
            nina.SetFlatPanel(CoverState.Open);
            nina.MountParksAtOnce();
            nina.RoofClosesAtOnce();
            return nina;
        }

        private static SecureObservatory Emergency(Context context) =>
            new SecureObservatory("Secure (Unsafe)", context, isEmergency: true);

        private static SecureObservatory WindowClosed(Context context) =>
            new SecureObservatory("Secure (Window Closed)", context, "the observing window has closed");

        private static SecureObservatory Fault(Context context) =>
            new SecureObservatory("Secure (Fault)", context, "unrecoverable fault", isFault: true);

        // The state as the machine runs it: entry, execute, exit.
        private static async Task<AutopilotStateResult> RunAsync(SecureObservatory secure, CancellationToken token = default) {
            await secure.OnEntryAsync(token);
            try {
                return await secure.ExecuteAsync(token);
            } finally {
                await secure.OnExitAsync();
            }
        }

        ///////////// The sequence /////////////

        // Unsafe conditions mean the night may come back, so the emergency secure stops guiding, parks, closes the roof and
        // leaves the flat panel alone, in that order, and the session reads secured with the time it happened.
        [Test]
        public async Task AnEmergencySecureStopsGuidingParksAndClosesTheRoofAndLeavesThePanel() {
            FakeNina nina = ImagingNight();
            Context context = nina.CreateContext();
            context.IsRoofExpectedOpen = true;
            context.IsRoofUnderAutopilotControl = true;

            AutopilotStateResult result = await RunAsync(Emergency(context));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "dome: CloseShutter" }));
            Assert.Multiple(() => {
                Assert.That(context.IsSessionSecured, Is.True);
                Assert.That(context.SessionSecuredAtUtc, Is.EqualTo(context.UtcNow));
                Assert.That(context.IsRoofExpectedOpen, Is.False);
                Assert.That(context.IsRoofUnderAutopilotControl, Is.False);
                Assert.That(context.SessionEndReason, Is.Null);
                Assert.That(nina.Alerts, Is.Empty);
            });
        }

        // At the end of the night the panel closes too. A cover that can't move under a closed roof is closed while the roof
        // is still open, before it.
        [Test]
        public async Task AnEndOfNightSecureClosesThePanelBeforeTheRoofWhenTheCoverCannotMoveUnderIt() {
            FakeNina nina = ImagingNight();

            await RunAsync(WindowClosed(nina.CreateContext()));

            Assert.That(nina.Commands,
                Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "flat panel: CloseCover", "dome: CloseShutter" }));
        }

        // A cover that may move under a closed roof is closed after it, so the roof is never held open for the panel.
        [Test]
        public async Task AnEndOfNightSecureClosesThePanelAfterTheRoofWhenTheCoverMayMoveUnderIt() {
            FakeNina nina = ImagingNight();
            nina.Settings.SetShouldOperateFlatPanelWithRoofClosed(true);

            await RunAsync(WindowClosed(nina.CreateContext()));

            Assert.That(nina.Commands,
                Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "dome: CloseShutter", "flat panel: CloseCover" }));
        }

        // A fault secure records why the night ended, so the session-end email can say so.
        [Test]
        public async Task AFaultSecureRecordsItsReasonForTheSessionEndEmail() {
            FakeNina nina = ImagingNight();
            Context context = nina.CreateContext();

            await RunAsync(Fault(context));

            Assert.That(context.SessionEndReason, Is.EqualTo("unrecoverable fault"));
            Assert.That(context.IsSessionSecured, Is.True);
        }

        ///////////// The roof /////////////

        // N.I.N.A. answers a close with "done" whatever the shutter did, so the close is judged by the shutter and retried,
        // and a roof that never reads Closed gets the alert after the third try. The session is not secured.
        [Test]
        public async Task ARoofThatNeverReadsClosedIsRetriedTwiceThenAlerted() {
            FakeNina nina = ImagingNight();
            nina.RoofClosesButStillReadsOpen();
            Context context = nina.CreateContext();

            Task<AutopilotStateResult> run = RunAsync(Emergency(context));
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5)), Is.True);

            Assert.That(nina.Commands, Is.EqualTo(new[] {
                "guider: StopGuiding", "mount: Park", "dome: CloseShutter", "dome: CloseShutter", "dome: CloseShutter",
            }));
            Assert.That(nina.LogMessages("WARNING"),
                Does.Contain("Roof did not close (attempt 1 of 3, shutter ShutterOpen). Trying again in 30s."));
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Roof NOT closed after parking. Check the roof manually."));
            Assert.That(alert.Detail("shutter"), Is.EqualTo("ShutterOpen"));
            Assert.That(alert.Detail("attempts"), Is.EqualTo(3));
            Assert.That(context.IsSessionSecured, Is.False);
        }

        // A park that fails leaves the roof open, since closing over an unparked mount risks a collision. The alert names the
        // unparked mount, and the mount is stopped where it is.
        [Test]
        public async Task AParkThatFailsLeavesTheRoofOpenWithAnAlertAndTheMountStopped() {
            FakeNina nina = ImagingNight();
            nina.MountParkAnswers(false);
            Context context = nina.CreateContext();

            await RunAsync(Emergency(context));

            Assert.That(nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "mount: SetTrackingEnabled(false)" }));
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Roof NOT closed because the mount is not parked. Secure the observatory manually."));
            Assert.That(alert.Detail("mount"), Is.EqualTo("not parked"));
            Assert.That(alert.Detail("roof"), Is.EqualTo("ShutterOpen"));
            Assert.That(context.IsSessionSecured, Is.False);
        }

        // A park refused because the roof already reads closed has nothing left to do: the roof stays as it is, the mount is
        // not stopped a second time, and the refusal's alert is the only one.
        [Test]
        public async Task ARefusedParkUnderAClosedRoofLeavesTheRoofWithNoSecondStopAndNoSecondAlert() {
            FakeNina nina = ImagingNight();
            nina.SetRoof(ShutterState.ShutterClosed);
            Context context = nina.CreateContext();

            await RunAsync(Emergency(context));

            Assert.That(nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.Alerts.Select(alert => alert.Message), Is.EqualTo(new[] {
                "Mount NOT parked because the roof is not open. The mount was stopped where it was.",
            }));
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Roof is already closed (conditions unsafe)."));
            Assert.That(context.IsSessionSecured, Is.False);
        }

        // A park refused under a roof caught closing leaves that roof where it is, since closing it over the unparked mount
        // risks a collision, and says so in the log without a second alert.
        [Test]
        public async Task ARefusedParkUnderAClosingRoofLeavesTheRoofWhereItIs() {
            FakeNina nina = ImagingNight();
            nina.SetRoof(ShutterState.ShutterClosing);

            await RunAsync(Emergency(nina.CreateContext()));

            Assert.That(nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(nina.Alerts, Has.Count.EqualTo(1));
            Assert.That(nina.LogMessages("WARNING"),
                Does.Contain("Roof left ShutterClosing, since closing it over the unparked mount risks a collision."));
        }

        // A roof the autopilot doesn't operate is left to whoever does, and the night still counts as secured once the
        // mount is parked.
        [Test]
        public async Task ARoofThatIsNotOursIsLeftAloneAndTheNightCountsAsSecured() {
            FakeNina nina = ImagingNight();
            nina.Settings.SetAutopilotRoofMode(RoofMode.WaitForOpen);
            Context context = nina.CreateContext();

            await RunAsync(Emergency(context));

            Assert.That(nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park" }));
            Assert.That(context.IsSessionSecured, Is.True);
        }

        // A roof this autopilot opened tonight is closed by it even if the mode has changed since, so a mode change mid-night
        // can't leave the roof open.
        [Test]
        public async Task ARoofTheAutopilotOpenedTonightIsClosedEvenAfterTheModeChanged() {
            FakeNina nina = ImagingNight();
            nina.Settings.SetAutopilotRoofMode(RoofMode.WaitForOpen);
            Context context = nina.CreateContext();
            context.HasOpenedRoofTonight = true;

            await RunAsync(Emergency(context));

            Assert.That(nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "dome: CloseShutter" }));
            Assert.That(context.IsSessionSecured, Is.True);
        }

        // With the mount parked and the dome gone, a roof the autopilot opened tonight is an alert, since nothing else will
        // close it. One it never opened is a warning, since its state was never ours to know.
        [TestCase(true, "Roof NOT closed because the dome is not connected. Secure the observatory manually.")]
        [TestCase(false, null)]
        public async Task ADomeThatIsGoneIsAnAlertOnlyForARoofTheAutopilotOpened(bool hasOpenedRoofTonight, string? alert) {
            FakeNina nina = ImagingNight();
            nina.SetRoof(null);
            nina.SetMount(isConnected: true, isParked: true);
            Context context = nina.CreateContext();
            context.HasOpenedRoofTonight = hasOpenedRoofTonight;

            await RunAsync(Emergency(context));

            Assert.That(nina.Alerts.Select(reported => reported.Message), Is.EqualTo(alert is null ? Array.Empty<string>() : new[] { alert }));
            if (alert is null) {
                Assert.That(nina.LogMessages("WARNING"),
                    Does.Contain("Dome is not connected: the roof cannot be closed, and its state cannot be confirmed."));
            }
            Assert.That(context.IsSessionSecured, Is.False, "unknown is never closed");
        }

        ///////////// Its own clock /////////////

        // A started secure must finish, so it runs on its own clock and ignores the machine's token. Switching the autopilot
        // off mid-park still ends with the mount parked and the roof closed.
        [Test]
        public async Task ASwitchOffMidParkStillEndsParkedAndClosed() {
            FakeNina nina = ImagingNight();
            nina.MountParksSlowly();
            Context context = nina.CreateContext();
            using CancellationTokenSource machine = new CancellationTokenSource();

            Task<AutopilotStateResult> run = RunAsync(Emergency(context), machine.Token);
            await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            machine.Cancel();
            nina.FinishPark();

            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(await run, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "dome: CloseShutter" }));
            Assert.That(context.IsSessionSecured, Is.True);
        }

        // A secure that runs out of its 55 minutes reports it, with the rig's state, and stops a mount that may have had its
        // park cut off, with the extra StopSlew. The roof is not touched over the unparked mount.
        [Test]
        public async Task ASecureThatRunsOutOfTimeAlertsAndStopsTheMount() {
            FakeNina nina = ImagingNight();
            nina.MountParksSlowly();
            Context context = nina.CreateContext();

            Task<AutopilotStateResult> run = RunAsync(Emergency(context));
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60)), Is.True);

            Assert.That(await run, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.Commands, Is.EqualTo(new[] {
                "guider: StopGuiding", "mount: Park", "mount: StopSlew", "mount: SetTrackingEnabled(false)",
            }));
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.Multiple(() => {
                Assert.That(alert.Message, Is.EqualTo("Securing the observatory did not finish in time. Check the mount and roof manually."));
                Assert.That(alert.Detail("timeout"), Is.EqualTo("55 minutes"));
                Assert.That(alert.Detail("mount"), Is.EqualTo("not parked"));
                Assert.That(alert.Detail("roof"), Is.EqualTo("ShutterOpen"));
                Assert.That(alert.Detail("flat_panel"), Is.EqualTo("Open"));
            });
            Assert.That(context.IsSessionSecured, Is.False);
        }

        ///////////// The session-end email /////////////

        // The email's "secured" is read again when it goes, not copied from the secure, so a mount unparked by hand since, or
        // a dome that has dropped off, reads as not secured.
        [TestCase(true, true, "yes", "closed")]
        [TestCase(false, true, "no", "closed")]
        [TestCase(true, false, "no", "unknown (dome not connected)")]
        public async Task TheSessionEndEmailReadsSecuredFromTheDevicesNow(bool isStillParked, bool isDomeStillConnected,
            string secured, string roof) {
            FakeNina nina = ImagingNight();
            Context context = nina.CreateContext();
            await context.StartSessionAsync();
            await RunAsync(Emergency(context));
            if (!isStillParked) nina.TelescopeInfo.AtPark = false;
            if (!isDomeStillConnected) nina.SetRoof(null);

            context.ReportSessionEnd();

            Observatory.ReportedEvent email = nina.Alerts.Single(alert => alert.Kind == "autopilot_stopped");
            Assert.That(email.Message, Is.EqualTo("Autopilot session stopped."));
            Assert.That(email.Detail("secured"), Is.EqualTo(secured));
            Assert.That(email.Detail("roof"), Is.EqualTo(roof));
        }
    }
}