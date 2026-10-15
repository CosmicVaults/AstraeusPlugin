using System;
using System.Linq;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Components {
    // The backstop for a mount outside the autopilot whose position can't be read, on the same fixture as the rest of the mount.
    public partial class MountTests {
        private const string BlindHeadline = "Mount position unknown outside the autopilot.";

        // A tracking mount with limits on and no position to read, as a driver that has lost its link to the controller reports.
        private static FakeNina BlindTrackingMount() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            // The sidereal time comes from a native library the test box doesn't have, and no test here is about the meridian.
            nina.Mount.LocalSiderealTimeNow = _ => 0;
            return nina;
        }

        // A mount blind for less than the grace is left alone, since one bad reading is a driver hiccup, not a lost mount.
        [Test]
        public async Task AMountBlindForLessThanTheGraceIsLeftAlone() {
            FakeNina nina = BlindTrackingMount();

            await nina.Mount.LateUpdate();
            nina.Clock.Advance(TimeSpan.FromSeconds(29));
            await nina.Mount.LateUpdate();

            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
        }

        // At the grace the mount can't be held to its limits, so it is stopped and the observers told once, with the reason,
        // how long, and that tracking stopped.
        [Test]
        public async Task AMountBlindForTheGraceIsStoppedAndReportedOnce() {
            FakeNina nina = BlindTrackingMount();

            await nina.Mount.LateUpdate();
            nina.Clock.Advance(TimeSpan.FromSeconds(30));
            await nina.Mount.LateUpdate();

            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: SetTrackingEnabled(false)" }));
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.Multiple(() => {
                Assert.That(alert.Message, Is.EqualTo(BlindHeadline));
                Assert.That(alert.Detail("reason"), Is.EqualTo("the mount reports no position"));
                Assert.That(alert.Detail("duration"), Is.EqualTo("30 s"));
                Assert.That(alert.Detail("tracking"), Is.EqualTo("stopped"));
                Assert.That(nina.LogMessages("ERROR"), Is.EqualTo(new[] {
                    "Mount position unknown outside the autopilot. It has been unreadable for 30 s (the mount reports no " +
                    "position), so its limits can't be enforced. Tracking stopped.",
                }));
            });

            nina.Clock.Advance(TimeSpan.FromMinutes(1));
            await nina.Mount.LateUpdate();
            Assert.That(nina.Alerts, Has.Count.EqualTo(1), "a stopped mount is resting, so the spell is over");
        }

        // A mount that won't confirm the stop is reported once with that failure, and not again while it stays blind.
        [Test]
        public async Task AMountThatWillNotStopIsReportedOnceWithTheFailure() {
            FakeNina nina = BlindTrackingMount();
            nina.MountIgnoresTrackingStops();

            await nina.Mount.LateUpdate();
            nina.Clock.Advance(TimeSpan.FromSeconds(30));
            Task late = nina.Mount.LateUpdate();
            Assert.That(await nina.AdvanceUntilAsync(late, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30)), Is.True);
            await nina.Mount.LateUpdate();

            Assert.That(nina.Alerts, Has.Count.EqualTo(1));
            Assert.That(nina.Alerts[0].Detail("tracking"), Is.EqualTo("could not be stopped"));
        }

        // A mount that dropped off while tracking may still be tracking, and can't be told to stop, so it gets the alert alone.
        [Test]
        public async Task ADisconnectedMountLastSeenTrackingGetsTheAlertAlone() {
            FakeNina nina = BlindTrackingMount();
            nina.TelescopeInfo.Coordinates = Pointing(10, 20);
            await nina.Mount.LateUpdate();
            nina.SetMount(isConnected: false);

            await nina.Mount.LateUpdate();
            nina.Clock.Advance(TimeSpan.FromSeconds(30));
            await nina.Mount.LateUpdate();

            Assert.That(nina.Commands, Is.Empty);
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Detail("reason"), Is.EqualTo("the mount is not connected"));
            Assert.That(alert.Detail("tracking"), Is.EqualTo("could not be stopped, as the mount is not connected"));
        }

        // A position that reads again ends the spell, so a later blind stretch is counted from its own start.
        [Test]
        public async Task AReadingAgainInsideTheGraceEndsTheSpell() {
            FakeNina nina = BlindTrackingMount();

            await nina.Mount.LateUpdate();
            nina.Clock.Advance(TimeSpan.FromSeconds(20));
            await nina.Mount.LateUpdate();
            nina.TelescopeInfo.Coordinates = Pointing(10, 20);
            await nina.Mount.LateUpdate();
            nina.TelescopeInfo.Coordinates = null!;
            await nina.Mount.LateUpdate();
            nina.Clock.Advance(TimeSpan.FromSeconds(20));
            await nina.Mount.LateUpdate();
            Assert.That(nina.Alerts, Is.Empty, "20 s into the second spell");

            nina.Clock.Advance(TimeSpan.FromSeconds(10));
            await nina.Mount.LateUpdate();
            Assert.That(nina.Alerts, Has.Count.EqualTo(1));
        }
    }
}