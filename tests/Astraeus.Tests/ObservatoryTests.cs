using System;
using System.Linq;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests {
    [TestFixture]
    public class ObservatoryTests {
        // 1 March: sunset 18:00, nautical dusk 19:00, astronomical dusk 20:00, and the dawns 04:00, 05:00 and 06:00 the next day.
        private static FakeNina NightWith(DateTime localNow) {
            FakeNina nina = new FakeNina();
            nina.Clock.SetUtcNow(new DateTimeOffset(localNow, TimeSpan.Zero));
            nina.SetTwilight(
                sun: new FixedRiseAndSet(set: At(1, 18), rise: At(2, 6)),
                nautical: new FixedRiseAndSet(set: At(1, 19), rise: At(2, 5)),
                astronomical: new FixedRiseAndSet(set: At(1, 20), rise: At(2, 4)));
            return nina;
        }

        private static DateTime At(int day, int hour) => new DateTime(2026, 3, day, hour, 0, 0);

        // The window is the chosen twilight's set to its rise. Before dusk and after dawn the night is over, between them it is on.
        [TestCase(1, 17, true)]
        [TestCase(1, 22, false)]
        [TestCase(2, 3, false)]
        [TestCase(2, 6, true)]
        public void TheNightRunsFromTheChosenDuskToTheChosenDawn(int day, int hour, bool isOutside) {
            FakeNina nina = NightWith(At(day, hour));
            nina.Settings.SetAutopilotOperatingWindow(OperatingWindow.Nautical);

            Assert.That(nina.Observatory.IsOutsideObservingWindow(), Is.EqualTo(isOutside));
        }

        // Each window setting reads its own twilight. At 19:30 the sun has set and nautical dusk has passed, but astronomical
        // dusk hasn't.
        [TestCase(OperatingWindow.Sunset, false)]
        [TestCase(OperatingWindow.Nautical, false)]
        [TestCase(OperatingWindow.Astronomical, true)]
        public void TheWindowSettingPicksWhichTwilightCounts(OperatingWindow window, bool isOutside) {
            FakeNina nina = NightWith(new DateTime(2026, 3, 1, 19, 30, 0));
            nina.Settings.SetAutopilotOperatingWindow(window);

            Assert.That(nina.Observatory.IsOutsideObservingWindow(), Is.EqualTo(isOutside));
        }

        // After dawn is the same twilight's rise, which the dome's roof watchdog and the secure both read.
        [TestCase(1, 22, false)]
        [TestCase(2, 6, true)]
        public void AfterDawnFollowsTheChosenTwilight(int day, int hour, bool isAfterDawn) {
            FakeNina nina = NightWith(At(day, hour));
            nina.Settings.SetAutopilotOperatingWindow(OperatingWindow.Nautical);

            Assert.That(nina.Observatory.IsAfterDawn(), Is.EqualTo(isAfterDawn));
        }

        // A day with no dawn, as in a polar summer, is not "after dawn". Guessing yes would close the roof at noon.
        [Test]
        public void ADayWithNoDawnIsNotAfterDawn() {
            FakeNina nina = new FakeNina();
            nina.SetTwilight(
                sun: new FixedRiseAndSet(set: null, rise: null),
                nautical: new FixedRiseAndSet(set: null, rise: null),
                astronomical: new FixedRiseAndSet(set: At(1, 20), rise: At(2, 4)));
            nina.Settings.SetAutopilotOperatingWindow(OperatingWindow.Nautical);

            Assert.That(nina.Observatory.IsAfterDawn(), Is.False);
        }

        ///////////// The hooks /////////////

        // Tests run signed out, where an alert is dropped before it reaches the server. The hook sees it first, so what
        // the night would have emailed can still be checked, and the same alert is logged as CRITICAL under the autopilot.
        [Test]
        public void AnAlertIsSeenByTheHookBeforeTheSignInCheckDropsIt() {
            FakeNina nina = new FakeNina();

            nina.CreateContext().ReportCritical("Roof NOT closed after parking. Check the roof manually.", new { reason = "dawn" });

            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Kind, Is.EqualTo("critical_error"));
            Assert.That(alert.Message, Is.EqualTo("Roof NOT closed after parking. Check the roof manually."));
            Assert.That(alert.Details, Is.EqualTo(new { reason = "dawn" }));
            Assert.That(nina.LogMessages("CRITICAL"), Is.EqualTo(new[] { "Roof NOT closed after parking. Check the roof manually." }));
        }

        // Every line above DEBUG is recorded with its severity and device whether or not the socket is up, so a test reads
        // the night's log without a dashboard. DEBUG stays in N.I.N.A.'s file only.
        [Test]
        public void LogLinesAreRecordedWithTheirSeverityAndDeviceAndDebugIsNot() {
            FakeNina nina = new FakeNina();

            nina.Observatory.Log("Parking scope.", "mount");
            nina.Observatory.LogWarning("Roof did not close.", "dome");
            nina.Observatory.LogError("Park failed.", "mount");
            nina.Observatory.LogDebug("Park exception detail.", "mount");

            Assert.That(nina.Logs, Is.EqualTo(new[] {
                new Observatory.LogLine("INFO", "mount", "Parking scope."),
                new Observatory.LogLine("WARNING", "dome", "Roof did not close."),
                new Observatory.LogLine("ERROR", "mount", "Park failed."),
            }));
            Assert.That(nina.LogMessages("WARNING"), Is.EqualTo(new[] { "Roof did not close." }));
        }
    }
}