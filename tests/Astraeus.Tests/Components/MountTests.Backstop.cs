using System;
using System.Linq;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Components {
    // The limit backstop outside the autopilot, on the same fixture as the rest of the mount.
    public partial class MountTests {
        private const string BackstopHeadline = "Mount limit reached outside the autopilot.";

        // A mount tracking with limits on and a 30° altitude floor, at the given altitude, with a position to judge. The
        // sidereal time is pinned, since it comes from a native library the test box doesn't have.
        private static FakeNina TrackingAt(double altitudeDegrees, bool isSlewing = false) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.Settings.SetIsAltitudeLimitEnabled(true);
            nina.Settings.SetMountMinAltitudeDegrees(30);
            nina.SetMount(isConnected: true, isTracking: true, isSlewing: isSlewing);
            nina.TelescopeInfo.Coordinates = Pointing(100, 0);
            nina.TelescopeInfo.Altitude = altitudeDegrees;
            nina.TelescopeInfo.Azimuth = 180;
            nina.Mount.LocalSiderealTimeNow = _ => 0;
            return nina;
        }

        // A mount tracking below the altitude floor with nobody driving it is stopped and reported once, since the driver
        // may have no limit of its own and nothing else is watching.
        [Test]
        public async Task AMountTrackingBelowTheAltitudeLimitIsStoppedAndReportedOnce() {
            FakeNina nina = TrackingAt(20);

            await nina.Mount.LateUpdate();
            await nina.Mount.LateUpdate();

            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: SetTrackingEnabled(false)" }));
            Observatory.ReportedEvent alert = nina.Alerts.Single();
            Assert.That(alert.Message, Is.EqualTo(BackstopHeadline));
            Assert.That(alert.Detail("breach"), Does.StartWith("below altitude limit ("));
            Assert.That(alert.Detail("tracking"), Is.EqualTo("stopped"));
            Assert.That(nina.LogMessages("ERROR").Single(),
                Does.StartWith("Mount limit reached outside the autopilot. The mount is below altitude limit ("));
        }

        // A slew in progress is judged on arrival. Its target passed the check that let it start, and the path a driver
        // takes to get there is the driver's concern, so a low reading on the way is not a breach and nothing is stopped.
        [Test]
        public async Task AMountSlewingThroughALowAltitudeIsLeftToFinish() {
            FakeNina nina = TrackingAt(4.9, isSlewing: true);

            await nina.Mount.LateUpdate();

            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
        }

        // Once the slew ends the mount is judged where it arrived: inside the limits it is left alone, below them it is
        // stopped and reported like any tracking mount.
        [TestCase(70, false)]
        [TestCase(20, true)]
        public async Task AMountIsJudgedWhereItsSlewEnds(double arrivalAltitude, bool isStopped) {
            FakeNina nina = TrackingAt(4.9, isSlewing: true);
            await nina.Mount.LateUpdate();
            nina.TelescopeInfo.Slewing = false;
            nina.TelescopeInfo.Altitude = arrivalAltitude;

            await nina.Mount.LateUpdate();

            Assert.That(nina.Alerts, Has.Count.EqualTo(isStopped ? 1 : 0));
            Assert.That(nina.Commands, Is.EqualTo(isStopped ? new[] { "mount: SetTrackingEnabled(false)" } : Array.Empty<string>()));
        }
    }
}