using System;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Astrometry;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Components {
    [TestFixture]
    public partial class MountTests {
        private static Coordinates Pointing(double raDegrees, double decDegrees) =>
            new Coordinates(raDegrees, decDegrees, Epoch.JNOW, Coordinates.RAType.Degrees);

        ///////////// DescribeUnknownPosition /////////////

        // With limits off there is nothing to judge, so no position is ever unknown, even on a disconnected mount.
        [Test]
        public void WithLimitsOffNoPositionIsUnknown() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(false);
            nina.SetMount(isConnected: false);

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.Null);
        }

        // A mount never seen connected can't be known to be resting, so with limits on it reads as not connected.
        [Test]
        public void AMountNeverSeenConnectedIsUnknownAsNotConnected() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: false);

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.EqualTo("the mount is not connected"));
        }

        // A mount that dropped off while parked or still can't drive into a limit, so it isn't counted as lost.
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void AMountThatDroppedOffWhileRestingIsNotUnknown(bool isParked, bool isTracking) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isParked: isParked, isTracking: isTracking);
            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.Null, "while connected");

            nina.SetMount(isConnected: false);

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.Null, "after dropping off");
        }

        // A mount that dropped off while tracking may still be moving toward a limit, so it is reported as not connected.
        [Test]
        public void AMountThatDroppedOffWhileTrackingIsUnknownAsNotConnected() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeInfo.Coordinates = Pointing(10, 20);
            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.Null, "while connected");

            nina.SetMount(isConnected: false);

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.EqualTo("the mount is not connected"));
        }

        // A parked or still mount isn't going anywhere, so it is never unknown even with no coordinates at all.
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void ARestingMountIsNeverUnknown(bool isParked, bool isTracking) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isParked: isParked, isTracking: isTracking);

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.Null);
        }

        // A tracking mount with no coordinates, or NaN in either, can't be held to its limits and says so.
        [TestCase("no coordinates")]
        [TestCase("NaN RA")]
        [TestCase("NaN Dec")]
        public void ATrackingMountWithoutAReadablePositionIsUnknown(string position) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeInfo.Coordinates = position switch {
                "NaN RA" => Pointing(double.NaN, 20),
                "NaN Dec" => Pointing(10, double.NaN),
                _ => null!,
            };

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.EqualTo("the mount reports no position"));
        }

        // A tracking mount with a finite RA and Dec can be judged, so it isn't unknown.
        [Test]
        public void ATrackingMountWithAReadablePositionIsNotUnknown() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeInfo.Coordinates = Pointing(10, 20);

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.Null);
        }

        // Missing altitude and azimuth don't make a position unreadable. Only the RA and Dec are needed, since the
        // meridian check uses those.
        [Test]
        public void NaNAltitudeAndAzimuthDoNotMakeAPositionUnreadable() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeInfo.Coordinates = Pointing(10, 20);
            nina.TelescopeInfo.Altitude = double.NaN;
            nina.TelescopeInfo.Azimuth = double.NaN;

            Assert.That(nina.Mount.DescribeUnknownPosition(), Is.Null);
        }

        ///////////// StopParkAsync /////////////

        // A disconnected mount can't be told anything, and the caller must know the park wasn't stopped.
        [Test]
        public async Task StopParkOnADisconnectedMountIsFalseAndSendsNothing() {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: false);

            bool hasStopped = await nina.Mount.StopParkAsync();

            Assert.That(hasStopped, Is.False);
            Assert.That(nina.Commands, Is.Empty);
        }

        // A mount already at park needs no stop, and sending one could wake a driver that has just settled.
        [Test]
        public async Task StopParkOnAParkedMountIsTrueWithNoStopSent() {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true, isParked: true);

            bool hasStopped = await nina.Mount.StopParkAsync();

            Assert.That(hasStopped, Is.True);
            Assert.That(nina.Commands, Is.Empty);
        }

        // Not every driver reports Slewing while it parks, so StopSlew is sent regardless, and tracking is turned off after it.
        [Test]
        public async Task StopParkSendsStopSlewEvenWhenTheDriverSaysItIsNotSlewing() {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true, isTracking: true, isSlewing: false);

            bool hasStopped = await nina.Mount.StopParkAsync();

            Assert.That(hasStopped, Is.True);
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: StopSlew", "mount: SetTrackingEnabled(false)" }));
        }

        // A mount that does report the park as a slew gets the same stop, and the result says it stopped.
        [Test]
        public async Task StopParkOnASlewingMountStopsItAndReportsTrue() {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true, isTracking: true, isSlewing: true);

            bool hasStopped = await nina.Mount.StopParkAsync();

            Assert.That(hasStopped, Is.True);
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: StopSlew", "mount: SetTrackingEnabled(false)" }));
        }

        // A StopSlew the driver refuses must not stop the tracking stop that follows, since tracking is what carries a
        // mount into a limit.
        [Test]
        public async Task AStopSlewThatThrowsStillStopsTracking() {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeMediator.Setup(mediator => mediator.StopSlew()).Throws(new InvalidOperationException("driver refused"));

            bool hasStopped = await nina.Mount.StopParkAsync();

            Assert.That(hasStopped, Is.True);
            Assert.That(nina.Commands, Is.EqualTo(new[] { "mount: SetTrackingEnabled(false)" }));
        }

        // A driver that keeps tracking after two stop requests is reported as not stopped, so a secure never closes the
        // roof over a mount it only thinks is still. Ten clock seconds, no real ones.
        [Test]
        public async Task ADriverThatIgnoresTwoStopsIsReportedAsNotStopped() {
            FakeNina nina = new FakeNina();
            nina.SetMount(isConnected: true, isTracking: true);
            nina.MountIgnoresTrackingStops();

            Task<bool> stop = nina.Mount.StopParkAsync();
            bool didFinish = await nina.AdvanceUntilAsync(stop, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));

            Assert.That(didFinish, Is.True, "the stop should give up after two settles");
            Assert.That(await stop, Is.False);
            Assert.That(nina.Commands, Is.EqualTo(new[] {
                "mount: StopSlew", "mount: SetTrackingEnabled(false)", "mount: SetTrackingEnabled(false)",
            }));
        }
    }
}