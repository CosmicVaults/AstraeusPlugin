using System;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    [TestFixture]
    public class ContextTests {
        ///////////// IsRoofClosedOrNotOurs /////////////

        // A roof the autopilot operates only counts as closed when it reads Closed. A dome that has dropped off leaves
        // it unknown, and unknown is never secured.
        [Test]
        public void AnOperatedRoofWithTheDomeDisconnectedIsNotClosed() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(null);

            Assert.That(nina.CreateContext().IsRoofClosedOrNotOurs(), Is.False);
        }

        // Closed is the one reading that counts. Open, moving and error all leave a roof the autopilot operates not closed.
        [TestCase(ShutterState.ShutterClosed, true)]
        [TestCase(ShutterState.ShutterOpen, false)]
        [TestCase(ShutterState.ShutterClosing, false)]
        [TestCase(ShutterState.ShutterOpening, false)]
        [TestCase(ShutterState.ShutterError, false)]
        public void AnOperatedRoofCountsAsClosedOnlyWhenItReadsClosed(ShutterState shutter, bool isClosed) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(shutter);

            Assert.That(nina.CreateContext().IsRoofClosedOrNotOurs(), Is.EqualTo(isClosed));
        }

        // A roof the autopilot doesn't operate is whoever's business it is, so it never blocks "secured", whatever the
        // dome reads.
        [TestCase(RoofMode.WaitForOpen)]
        [TestCase(RoofMode.Ignore)]
        public void ARoofThatIsNotOursCountsAsClosedWhateverItReads(RoofMode mode) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(mode);
            nina.SetRoof(ShutterState.ShutterOpen);

            Assert.That(nina.CreateContext().IsRoofClosedOrNotOurs(), Is.True);
        }

        // A roof this autopilot opened tonight is its responsibility even after the mode changed, so it has to read
        // Closed like an operated one.
        [TestCase(null, false)]
        [TestCase(ShutterState.ShutterOpen, false)]
        [TestCase(ShutterState.ShutterClosed, true)]
        public void ARoofTheAutopilotOpenedTonightMustReadClosed(ShutterState? shutter, bool isClosed) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.WaitForOpen);
            nina.SetRoof(shutter);
            Context context = nina.CreateContext();
            context.HasOpenedRoofTonight = true;

            Assert.That(context.IsRoofClosedOrNotOurs(), Is.EqualTo(isClosed));
        }

        ///////////// IsStillSecured /////////////

        // The session-end email reports secured only if the secure happened and still holds: mount connected and parked,
        // roof closed or not ours.
        [Test]
        public void StillSecuredNeedsTheSecureToHaveHappenedAndToStillHold() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetMount(isConnected: true, isParked: true);
            nina.SetRoof(ShutterState.ShutterClosed);
            Context context = nina.CreateContext();

            Assert.That(context.IsStillSecured, Is.False, "before any secure");
            context.IsSessionSecured = true;
            Assert.That(context.IsStillSecured, Is.True, "after the secure");
        }

        // A mount that dropped off or was unparked by hand after the secure is not secured any more, since unknown and
        // moving never are.
        [Test]
        public void StillSecuredIsLostWhenTheMountDropsOffOrUnparks() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Ignore);
            nina.SetMount(isConnected: true, isParked: true);
            Context context = nina.CreateContext();
            context.IsSessionSecured = true;

            nina.SetMount(isConnected: true, isParked: false);
            Assert.That(context.IsStillSecured, Is.False, "unparked");
            nina.SetMount(isConnected: false, isParked: true);
            Assert.That(context.IsStillSecured, Is.False, "disconnected");
        }

        // A roof the autopilot operates that no longer reads Closed, or whose dome has dropped, un-secures the session too.
        [TestCase(ShutterState.ShutterOpen)]
        [TestCase(null)]
        public void StillSecuredIsLostWhenAnOperatedRoofStopsReadingClosed(ShutterState? shutter) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetMount(isConnected: true, isParked: true);
            nina.SetRoof(ShutterState.ShutterClosed);
            Context context = nina.CreateContext();
            context.IsSessionSecured = true;

            nina.SetRoof(shutter);

            Assert.That(context.IsStillSecured, Is.False);
        }

        ///////////// DescribeUnknownMountPosition /////////////

        // Before tonight's connect chain has run, a mount that can't be read is normal, not lost, so the limit watcher
        // must not count it.
        [Test]
        public void AnUnknownPositionIsNotReportedBeforeStartupIsComplete() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);

            Assert.That(nina.CreateContext().DescribeUnknownMountPosition(), Is.Null);
        }

        // Start-up complete but no session open is between nights, when nothing is watching, so again nothing is reported.
        [Test]
        public void AnUnknownPositionIsNotReportedBetweenNights() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            Context context = nina.CreateContext();
            context.IsStartupComplete = true;

            Assert.That(context.DescribeUnknownMountPosition(), Is.Null);
        }

        // Once the night is under way the mount's own answer comes through, so the watcher can start its 30 s count.
        [Test]
        public async Task AnUnknownPositionIsReportedOnceTheNightIsUnderWay() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            Context context = nina.CreateContext();
            await context.StartSessionAsync();
            context.IsStartupComplete = true;

            Assert.That(context.DescribeUnknownMountPosition(), Is.EqualTo("the mount reports no position"));
        }

        ///////////// IsRoofClosedUnexpectedly /////////////

        // With no roof there is nothing to read, so the roof watcher must never trip, whatever a dome might report.
        [Test]
        public void WithNoRoofTheRoofIsNeverClosedUnexpectedly() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Ignore);
            nina.SetRoof(ShutterState.ShutterClosed);
            Context context = nina.CreateContext();
            context.IsRoofExpectedOpen = true;

            Assert.That(context.IsRoofClosedUnexpectedly(), Is.False);
        }

        // A roof that hasn't been seen open yet can't close unexpectedly, or the watcher would trip before the night started.
        [Test]
        public void ARoofNotYetSeenOpenIsNotClosedUnexpectedly() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(ShutterState.ShutterClosed);

            Assert.That(nina.CreateContext().IsRoofClosedUnexpectedly(), Is.False);
        }

        // Once the roof was seen open, anything but Open counts as closed, a dropped dome included, since the roof can't
        // be confirmed open.
        [TestCase(ShutterState.ShutterOpen, false)]
        [TestCase(ShutterState.ShutterClosed, true)]
        [TestCase(ShutterState.ShutterClosing, true)]
        [TestCase(null, true)]
        public void ARoofSeenOpenIsClosedUnexpectedlyWhenItStopsReadingOpen(ShutterState? shutter, bool isClosedUnexpectedly) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.WaitForOpen);
            nina.SetRoof(shutter);
            Context context = nina.CreateContext();
            context.IsRoofExpectedOpen = true;

            Assert.That(context.IsRoofClosedUnexpectedly(), Is.EqualTo(isClosedUnexpectedly));
        }

        ///////////// The clock /////////////

        // The server's set-up margin is judged on the plugin's clock. At the margin the window is still open, a second
        // past it the target goes back to the server rather than up the retry ladder.
        [Test]
        public void TheServerWindowExpiresWhenTheMarginPassesOnTheClock() {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();
            context.TargetAssignedAtUtc = context.UtcNow;

            nina.Clock.Advance(TimeSpan.FromSeconds(Context.LimitLookAheadMarginSeconds));
            Assert.That(context.HasServerWindowExpired, Is.False, "at the margin");
            nina.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(context.HasServerWindowExpired, Is.True, "past the margin");
        }

        // A session's start is stamped from the clock, so a night's length in the session-end email can be tested
        // without sitting through a night.
        [Test]
        public async Task ASessionStartsAtTheClocksTime() {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();

            await context.StartSessionAsync();

            Assert.That(context.SessionStartedUtc, Is.EqualTo(context.UtcNow));
        }

        ///////////// IsSafe /////////////

        // Fail closed: a safety monitor that isn't connected, or reads unsafe, is unsafe. Only a connected Safe reading is safe.
        [TestCase(null, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        public void OnlyAConnectedSafeReadingIsSafe(bool? reading, bool isSafe) {
            FakeNina nina = new FakeNina();
            nina.SetSafety(reading);

            Assert.That(nina.CreateContext().IsSafe(), Is.EqualTo(isSafe));
        }
    }
}