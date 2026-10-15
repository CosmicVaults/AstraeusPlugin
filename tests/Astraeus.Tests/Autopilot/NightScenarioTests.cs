using System;
using System.Linq;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    /// <summary>
    /// Whole nights on the real machine: an exposure under way, something goes wrong, and the full ordered sequence of
    /// device commands and alerts that follows, through the real secure, wait, open and stop states.
    /// </summary>
    [TestFixture]
    public class NightScenarioTests {
        // The watchers poll every five seconds, so a tick is one chance for them to notice.
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(Context.SafetyWatchIntervalSeconds);
        private static readonly TimeSpan Poll = TimeSpan.FromSeconds(Context.SafetyPollIntervalSeconds);

        // Unsafe conditions mid-exposure: the exposure is aborted, the mount parked and the roof closed, then the night
        // waits. When conditions clear it reopens the roof and goes back to asking for a target, with no alert sent,
        // since this is the night working as designed.
        [Test]
        public async Task UnsafeWhileExposingSecuresWaitsReopensAndResumes() {
            NightScenario night = new NightScenario();
            await night.StartAsync();

            night.Nina.SetSafety(false);
            Assert.That(await night.AdvanceUntilAsync(() => night.Context.AutopilotStatus == AutopilotStatus.WaitingSafety,
                Tick, TimeSpan.FromMinutes(1)), Is.True, "secured and waiting");
            Assert.That(night.Nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "dome: CloseShutter" }));
            Assert.That(night.Context.IsSessionSecured, Is.True);
            Assert.That(night.Nina.LogMessages("WARNING"), Has.Some.StartWith("Safety conditions changed ("));

            night.Nina.SetSafety(true);
            Assert.That(await night.AdvanceUntilAsync(() => night.HasAskedForATarget, Poll, TimeSpan.FromMinutes(5)), Is.True, "resumed");
            Assert.That(night.Nina.Commands, Is.EqualTo(new[] {
                "guider: StopGuiding", "mount: Park", "dome: CloseShutter", "dome: OpenShutter",
            }));
            Assert.That(night.Context.IsSessionSecured, Is.False);
            Assert.That(night.CriticalAlerts, Is.Empty);
            await night.SwitchOffAsync();
        }

        // Under Wait-for-open the roof is somebody else's. One that stops reading open mid-exposure is handled like unsafe
        // conditions: the park is refused under it, with the mount stopped and one alert, and the night waits for safe
        // conditions and for the roof to open again.
        [Test]
        public async Task UnderWaitForOpenARoofClosingStopsThenSecuresAndWaitsForTheRoof() {
            NightScenario night = new NightScenario(RoofMode.WaitForOpen);
            await night.StartAsync();

            night.Nina.SetRoof(ShutterState.ShutterClosing);
            Assert.That(await night.AdvanceUntilAsync(() => night.Context.AutopilotStatus == AutopilotStatus.WaitingRoof,
                Tick, TimeSpan.FromMinutes(5)), Is.True, "waiting for the roof");

            Assert.That(night.Nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(night.Nina.LogMessages("WARNING"),
                Does.Contain("The roof is no longer open (shutter ShutterClosing). Aborting current action."));
            Observatory.ReportedEvent alert = night.CriticalAlerts.Single();
            Assert.That(alert.Message, Is.EqualTo("Mount NOT parked because the roof is not open. The mount was stopped where it was."));
            Assert.That(alert.Detail("reason"), Is.EqualTo("the roof stopped reading open"));
            Assert.That(alert.Detail("action"), Is.EqualTo("waiting for safe conditions and for the roof to open again"));
            await night.SwitchOffAsync();
        }

        // Under Wait-for-open, conditions turn unsafe while the roof's own controller is already closing it. The unsafe
        // secure must not park under the closing roof: the mount is stopped where it is, one alert says why, and the night waits.
        [Test]
        public async Task UnsafeWhileAnIndependentRoofIsClosingStopsTheMountWithoutParking() {
            NightScenario night = new NightScenario(RoofMode.WaitForOpen);
            await night.StartAsync();

            night.Nina.SetSafety(false);
            night.Nina.SetRoof(ShutterState.ShutterClosing);
            Assert.That(await night.AdvanceUntilAsync(() => night.Context.AutopilotStatus == AutopilotStatus.WaitingSafety,
                Tick, TimeSpan.FromMinutes(5)), Is.True, "secured and waiting");

            Assert.That(night.Nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(night.Nina.Commands, Does.Not.Contain("mount: Park"), "no park under a closing roof");
            Assert.That(night.Nina.LogMessages("WARNING"), Has.Some.StartWith("Safety conditions changed ("));
            Observatory.ReportedEvent alert = night.CriticalAlerts.Single();
            Assert.Multiple(() => {
                Assert.That(alert.Message, Is.EqualTo("Mount NOT parked because the roof is not open. The mount was stopped where it was."));
                Assert.That(alert.Detail("mount"), Is.EqualTo("stopped"));
                Assert.That(alert.Detail("roof"), Is.EqualTo("ShutterClosing"));
                Assert.That(alert.Detail("action"), Is.EqualTo("waiting for safe conditions and for the roof to open again"));
            });
            Assert.That(night.Context.IsSessionSecured, Is.False);
            await night.SwitchOffAsync();
        }

        // Under Operate nothing else should move this roof, so one closing mid-exposure stops the exposure, the guider and
        // the mount where they are, closes a cover that may move under the closed roof, alerts and disables for the night.
        [Test]
        public async Task UnderOperateARoofClosingStopsEverythingAndDisables() {
            NightScenario night = new NightScenario();
            night.Nina.Settings.SetAutopilotFlatPanelMode(FlatPanelMode.OpenAndClose);
            night.Nina.Settings.SetShouldOperateFlatPanelWithRoofClosed(true);
            night.Nina.SetFlatPanel(CoverState.Open);
            await night.StartAsync();

            night.Nina.SetRoof(ShutterState.ShutterClosing);
            Assert.That(await night.AdvanceUntilAsync(() => night.Run.IsCompleted, Tick, TimeSpan.FromMinutes(1)), Is.True, "disabled");

            Assert.That(night.Nina.Commands, Is.EqualTo(new[] {
                "camera: AbortExposure", "guider: StopGuiding", "mount: SetTrackingEnabled(false)", "flat panel: CloseCover",
            }));
            Assert.That(night.CriticalAlerts.Single().Message,
                Is.EqualTo("Roof closed while the autopilot was imaging. The mount was stopped, not parked."));
            Assert.That(night.Context.AutopilotStatus, Is.EqualTo(AutopilotStatus.Disabled));
            Assert.That(night.Context.SessionEndReason, Does.StartWith("The roof closed on its own while imaging"));
        }

        // Dawn: the window closes, the night is secured with the panel closed before the roof, the shutdown states run, the
        // session's end is reported as secured, and the machine settles into waiting for dusk.
        [Test]
        public async Task DawnSecuresClosesThePanelReportsTheSessionAndWaitsForDusk() {
            NightScenario night = new NightScenario();
            night.Nina.Settings.SetAutopilotFlatPanelMode(FlatPanelMode.OpenAndClose);
            night.Nina.SetFlatPanel(CoverState.Open);
            await night.StartAsync();

            night.Nina.Clock.SetUtcNow(new DateTimeOffset(new DateTime(2026, 3, 2, 6, 30, 0), TimeSpan.Zero));
            Assert.That(await night.AdvanceUntilAsync(() => night.Context.AutopilotStatus == AutopilotStatus.WaitingForNight,
                Tick, TimeSpan.FromMinutes(2)), Is.True, "waiting for dusk");

            Assert.That(night.Nina.Commands, Is.EqualTo(new[] {
                "guider: StopGuiding", "mount: Park", "flat panel: CloseCover", "dome: CloseShutter",
            }));
            Assert.That(night.Nina.LogMessages("WARNING"), Does.Contain("The observing window has closed. Aborting current action."));
            Assert.That(night.Context.IsSessionSecured, Is.True);
            Observatory.ReportedEvent email = night.Nina.Alerts.Single(alert => alert.Kind == "autopilot_stopped");
            Assert.That(email.Message, Is.EqualTo("Autopilot session stopped."));
            Assert.That(email.Detail("secured"), Is.EqualTo("yes"));
            Assert.That(night.CriticalAlerts, Is.Empty);
            await night.SwitchOffAsync();
        }

        // A fault mid-exposure parks and closes through the fault secure, and the run ends there: the autopilot switches
        // itself off and reports the fault when it tears down. The dawn shutdown never runs, so there is no
        // session-stopped email.
        [Test]
        public async Task AFaultSecuresAndEndsTheRunWithoutTheDawnShutdown() {
            NightScenario night = new NightScenario();
            await night.StartAsync(new FakeState("Exposing", night.Trace,
                _ => Task.FromException<AutopilotStateResult>(new InvalidOperationException("camera driver crashed"))));

            Assert.That(await night.AdvanceUntilAsync(() => night.Run.IsCompleted, Tick, TimeSpan.FromMinutes(1)), Is.True, "disabled");

            Assert.That(night.Nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding", "mount: Park", "dome: CloseShutter" }));
            Assert.That(night.Context.SessionEndReason, Is.EqualTo("Unexpected error in the autopilot machine.\ncamera driver crashed"));
            Assert.That(night.Run.IsCompleted, Is.True, "the run ends with the secure");
            Assert.That(night.Nina.Alerts.Any(alert => alert.Kind == "autopilot_stopped"), Is.False, "no dawn shutdown");
        }

        // Switching the autopilot off mid-exposure is someone taking over, so the exposure, guiding and tracking stop and
        // the roof and mount are left as found, with the log saying what that leaves.
        [Test]
        public async Task AManualSwitchOffMidExposureLeavesTheObservatoryAsFound() {
            NightScenario night = new NightScenario();
            await night.StartAsync();

            await night.SwitchOffAsync();

            Assert.That(night.Nina.Commands, Is.EqualTo(new[] { "camera: AbortExposure", "guider: StopGuiding", "mount: SetTrackingEnabled(false)" }));
            Assert.That(night.Nina.LogMessages("WARNING"), Does.Contain(
                "Autopilot stopped with the mount NOT parked and the roof NOT closed. Secure the observatory if you are done. " +
                "N.I.N.A.'s 'Close on unsafe' is off, so nothing will close the roof if conditions turn."));
            Assert.That(night.Context.IsSessionSecured, Is.False);
            Assert.That(night.CriticalAlerts, Is.Empty);
        }

        // A mount that tracks below the altitude limit mid-exposure means the limit checks got it wrong, so it is treated
        // as a fault: tracking stopped at once, then park, close and disable, with the breach quoted in the session's reason.
        [Test]
        public async Task AnAltitudeLimitCrossedWhileExposingStopsSecuresAndDisables() {
            NightScenario night = new NightScenario();
            night.Nina.Settings.SetIsMountLimitEnabled(true);
            night.Nina.Settings.SetIsAltitudeLimitEnabled(true);
            night.Nina.Settings.SetMountMinAltitudeDegrees(30);
            night.Nina.TelescopeInfo.Altitude = 45;
            night.Nina.TelescopeInfo.Azimuth = 180;
            await night.StartAsync();

            night.Nina.TelescopeInfo.Altitude = 20;
            Assert.That(await night.AdvanceUntilAsync(() => night.Run.IsCompleted, Tick, TimeSpan.FromMinutes(2)), Is.True, "disabled");

            Assert.That(night.Nina.Commands, Is.EqualTo(new[] {
                "guider: StopGuiding", "mount: SetTrackingEnabled(false)", "guider: StopGuiding", "mount: Park", "dome: CloseShutter",
            }));
            Assert.That(night.Context.LastLimitBreach, Does.StartWith("Mount limit reached on the current target: below altitude limit ("));
            Assert.That(night.Context.SessionEndReason, Is.EqualTo(night.Context.LastLimitBreach));
            Assert.That(night.Nina.LogMessages("ERROR"), Has.Some.EndWith(
                " during Exposing. Parking, closing the roof and disabling the autopilot."));
            Assert.That(night.Context.AutopilotStatus, Is.EqualTo(AutopilotStatus.Disabled));
        }

        // A tracking mount whose position can't be read for 30 s can't be held to its limits, so the limit watcher's
        // fail-safe trips with the reason: stop, park, close and disable, and the session-end email carries it.
        [Test]
        public async Task AMountOutOfReachForThirtySecondsIsTreatedAsALimitBreach() {
            NightScenario night = new NightScenario();
            night.Nina.Settings.SetIsMountLimitEnabled(true);
            night.Nina.TelescopeInfo.Coordinates = null!;
            await night.StartAsync();

            Assert.That(await night.AdvanceUntilAsync(() => night.Run.IsCompleted, Tick, TimeSpan.FromMinutes(2)), Is.True, "disabled");

            Assert.That(night.Nina.LogMessages("WARNING"), Does.Contain(
                "The mount limit watcher cannot read its device (the mount reports no position). If that lasts 30 s the " +
                "autopilot will stop, park, close the roof and switch off."));
            Assert.That(night.Context.LastLimitBreach,
                Is.EqualTo("Mount position unknown on the current target for 30 s (the mount reports no position)"));
            Assert.That(night.Nina.Commands, Is.EqualTo(new[] {
                "guider: StopGuiding", "mount: SetTrackingEnabled(false)", "guider: StopGuiding", "mount: Park", "dome: CloseShutter",
            }));

            night.Context.ReportSessionEnd();
            Observatory.ReportedEvent email = night.Nina.Alerts.Single(alert => alert.Kind == "autopilot_failed");
            Assert.That(email.Message, Is.EqualTo(night.Context.LastLimitBreach));
            Assert.That(email.Detail("reason"), Is.EqualTo(night.Context.LastLimitBreach));
        }

        // A mount that drops off while tracking can't be held to its limits, so after 30 s the limit watcher's fail-safe
        // trips. It can't be parked or stopped, so the roof is left open over it with an alert, the night ends, and the
        // session-end email carries the reason and says not secured.
        [Test]
        public async Task AMountThatDisconnectsWhileTrackingIsTreatedAsALimitBreachAfterThirtySeconds() {
            NightScenario night = new NightScenario();
            night.Nina.Settings.SetIsMountLimitEnabled(true);
            await night.StartAsync();

            night.Nina.SetMount(isConnected: false);
            Assert.That(await night.AdvanceUntilAsync(() => night.Run.IsCompleted, Tick, TimeSpan.FromMinutes(2)), Is.True, "disabled");

            Assert.That(night.Nina.LogMessages("WARNING"), Does.Contain(
                "The mount limit watcher cannot read its device (the mount is not connected). If that lasts 30 s the " +
                "autopilot will stop, park, close the roof and switch off."));
            Assert.That(night.Context.LastLimitBreach,
                Is.EqualTo("Mount position unknown on the current target for 30 s (the mount is not connected)"));
            Assert.That(night.Nina.Commands, Is.EqualTo(new[] { "guider: StopGuiding" }));
            Assert.That(night.CriticalAlerts.Select(alert => alert.Message), Is.EqualTo(new[] {
                "Roof NOT closed because the mount is not parked. Secure the observatory manually.",
            }));
            Assert.That(night.Context.AutopilotStatus, Is.EqualTo(AutopilotStatus.Disabled));

            night.Context.ReportSessionEnd();
            Observatory.ReportedEvent email = night.Nina.Alerts.Single(alert => alert.Kind == "autopilot_failed");
            Assert.That(email.Message, Is.EqualTo(night.Context.LastLimitBreach));
            Assert.That(email.Detail("secured"), Is.EqualTo("no"));
        }
    }
}