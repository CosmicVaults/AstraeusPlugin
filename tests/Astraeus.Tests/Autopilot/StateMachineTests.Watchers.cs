using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    // The watchers' checks and the blind-spell count, on the same fixture as the routing.
    public partial class StateMachineTests {
        private static readonly DateTime Midnight = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

        private static StateMachine.BlindSpell NewSpell() =>
            new StateMachine.BlindSpell("safety", AutopilotStateResult.Unsafe, "secure as if conditions were unsafe");

        ///////////// Blind spells /////////////

        // The first blind reading starts a spell with nothing elapsed, so one bad poll can never trip a watcher by itself.
        [Test]
        public void AFirstBlindReadingStartsASpellAtZero() {
            StateMachine.BlindSpell spell = NewSpell();

            TimeSpan blindFor = spell.Observe(Midnight, out bool isNew);

            Assert.That(isNew, Is.True);
            Assert.That(blindFor, Is.EqualTo(TimeSpan.Zero));
        }

        // Readings up to the gap apart continue the spell, counted from its start, which is how 30 s of blindness adds up
        // across 5 s polls. The gap itself still counts as continuing.
        [TestCase(5)]
        [TestCase(60)]
        public void AReadingInsideTheGapContinuesTheSpell(int secondsLater) {
            StateMachine.BlindSpell spell = NewSpell();
            spell.Observe(Midnight, out bool _);

            TimeSpan blindFor = spell.Observe(Midnight.AddSeconds(secondsLater), out bool isNew);

            Assert.That(isNew, Is.False);
            Assert.That(blindFor, Is.EqualTo(TimeSpan.FromSeconds(secondsLater)));
        }

        // A silence longer than the gap means the spell ended on its own, so the next blind reading is a fresh spell at zero.
        [Test]
        public void ASilenceLongerThanTheGapStartsANewSpell() {
            StateMachine.BlindSpell spell = NewSpell();
            spell.Observe(Midnight, out bool _);

            TimeSpan blindFor = spell.Observe(Midnight.AddSeconds(61), out bool isNew);

            Assert.That(isNew, Is.True);
            Assert.That(blindFor, Is.EqualTo(TimeSpan.Zero));
        }

        // End says whether a spell was running, once, so the "can read its device again" line is logged exactly once per spell.
        [Test]
        public void EndIsTrueOnceForARunningSpellAndFalseAfter() {
            StateMachine.BlindSpell spell = NewSpell();
            Assert.That(spell.End(), Is.False, "nothing running yet");

            spell.Observe(Midnight, out bool _);

            Assert.That(spell.End(), Is.True, "a spell was running");
            Assert.That(spell.End(), Is.False, "already ended");
        }

        ///////////// The checks /////////////

        // The safety check trips Unsafe on an unsafe or unreadable monitor and never anything else, so a roof problem can't
        // be mistaken for weather.
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(null, true)]
        public void TheSafetyCheckTripsUnsafeAndNothingElse(bool? reading, bool isTripped) {
            FakeNina nina = new FakeNina();
            nina.SetSafety(reading);
            StateMachine machine = new StateMachine(nina.CreateContext());

            StateMachine.Tripped? tripped = machine.CheckSafety();

            Assert.That(tripped?.Result, Is.EqualTo(isTripped ? AutopilotStateResult.Unsafe : (AutopilotStateResult?)null));
        }

        // A roof not yet seen open can't close unexpectedly, so the roof check is clear before the night's first open.
        [Test]
        public void TheRoofCheckIsClearUntilTheRoofHasBeenSeenOpen() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(ShutterState.ShutterClosed);
            nina.SetSafety(true);
            StateMachine machine = new StateMachine(nina.CreateContext());

            Assert.That(machine.CheckRoof(), Is.Null);
        }

        // Once the roof was seen open, anything but Open trips, a dropped dome included. Safe conditions make it RoofClosed and
        // unsafe ones make it Unsafe, so a roof that closed for weather takes the weather path.
        [TestCase(ShutterState.ShutterOpen, true, null)]
        [TestCase(ShutterState.ShutterClosed, true, AutopilotStateResult.RoofClosed)]
        [TestCase(ShutterState.ShutterClosed, false, AutopilotStateResult.Unsafe)]
        [TestCase(ShutterState.ShutterClosing, true, AutopilotStateResult.RoofClosed)]
        [TestCase(null, true, AutopilotStateResult.RoofClosed)]
        public void TheRoofCheckTripsRoofClosedWhenSafeAndUnsafeWhenNot(ShutterState? shutter, bool isSafe,
            AutopilotStateResult? expected) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(shutter);
            nina.SetSafety(isSafe);
            Context context = nina.CreateContext();
            context.IsRoofExpectedOpen = true;
            StateMachine machine = new StateMachine(context);

            Assert.That(machine.CheckRoof()?.Result, Is.EqualTo(expected));
        }

        // The limit watcher asks about an unknown position before judging a breach, since the breach check reads an unknown
        // position as clear. Blind is the answer, with the reason, so the fail-safe can count it.
        [Test]
        public async Task TheLimitCheckReportsAnUnknownPositionAsBlindBeforeJudgingABreach() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMount(isConnected: true, isTracking: true);
            Context context = nina.CreateContext();
            await context.StartSessionAsync();
            context.IsStartupComplete = true;
            StateMachine machine = new StateMachine(context);

            StateMachine.WatchReading reading = machine.CheckMountLimits();

            Assert.That(reading.BlindReason, Is.EqualTo("the mount reports no position"));
            Assert.That(reading.Trip, Is.Null);
        }

        // A readable position inside the limits is clear: no trip and no blind reason.
        [Test]
        public void TheLimitCheckIsClearInsideTheLimits() {
            FakeNina nina = TrackingGermanEquatorial(hourAngleHours: 0);
            StateMachine machine = new StateMachine(nina.CreateContext());

            StateMachine.WatchReading reading = machine.CheckMountLimits();

            Assert.That(reading.Trip, Is.Null);
            Assert.That(reading.BlindReason, Is.Null);
        }

        // Past the limit the check trips LimitReached and carries the breach text, which becomes the "Mount limit reached" line.
        [Test]
        public void TheLimitCheckTripsLimitReachedWithTheBreach() {
            FakeNina nina = TrackingGermanEquatorial(hourAngleHours: 20.0 / 60);
            StateMachine machine = new StateMachine(nina.CreateContext());

            StateMachine.WatchReading reading = machine.CheckMountLimits();

            Assert.That(reading.Trip?.Result, Is.EqualTo(AutopilotStateResult.LimitReached));
            Assert.That(reading.Trip?.Breach,
                Is.EqualTo("past meridian limit (20 min west of the meridian now on pierWest, limit 5 min past the meridian)"));
        }

        // A German equatorial tracking on pierWest at RA 6 h 40 min, with the sidereal time set to put it the given hours past
        // the meridian under a 5 min limit. Meridian flips stay off, the default, so the flip waiver leaves the meridian in.
        private static FakeNina TrackingGermanEquatorial(double hourAngleHours) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMeridianLimit(maxMinutesAfterMeridian: 5);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeInfo.AlignmentMode = AlignmentMode.GermanPolar;
            nina.TelescopeInfo.SideOfPier = PierSide.pierWest;
            nina.TelescopeInfo.Coordinates = new Coordinates(100, 0, Epoch.JNOW, Coordinates.RAType.Degrees);
            nina.Mount.LocalSiderealTimeNow = _ => 100.0 / 15 + hourAngleHours;
            return nina;
        }

        ///////////// Blind spells on the machine /////////////

        // An exposure's watchers over a night under way: 22:00, an operated roof open and expected open, conditions safe,
        // the mount tracking with a position, and limits off unless a test turns them on.
        private static FakeNina ExposingNight(out Context context) {
            FakeNina nina = new FakeNina();
            nina.SetTypicalNight(new DateTime(2026, 3, 1, 22, 0, 0));
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.SetRoof(ShutterState.ShutterOpen);
            nina.SetSafety(true);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeInfo.Coordinates = new Coordinates(100, 0, Epoch.JNOW, Coordinates.RAType.Degrees);
            nina.Mount.LocalSiderealTimeNow = _ => 100.0 / 15;
            context = nina.CreateContext();
            context.IsRoofExpectedOpen = true;
            return nina;
        }

        private static Task RunExposureAsync(Context context, List<string> trace, StateBase? state = null,
            CancellationToken cancellationToken = default) =>
            new StateMachine(context).RunAsync(state ?? new ExposingState("Exposing", trace), DeadEnds(trace), cancellationToken);

        // A check that throws is a blind reading, not a fault, so a dome driver that errors for the grace trips the roof
        // watcher's fail-safe with the error quoted, instead of ending the night on the fault path.
        [Test]
        public async Task ACheckThatThrowsForTheGraceTripsItsFailSafe() {
            FakeNina nina = ExposingNight(out Context context);
            nina.DomeReadThrows("boom");
            List<string> trace = new List<string>();

            Task run = RunExposureAsync(context, trace);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)), Is.True);

            Assert.That(trace, Does.Contain("Roof route: execute"));
            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "The roof watcher cannot read its device (its check failed: boom). If that lasts 30 s the autopilot will stop " +
                "as if the roof had closed."));
            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "The roof watcher could not read the dome for 30 s (its check failed: boom), so treating the roof as closed. " +
                "Aborting current action."));
        }

        // One bad poll followed by a good one is a blip: no trip, one warning when it starts and one line when the device
        // reads again, so the log shows the blip without the night reacting to it.
        [Test]
        public async Task ABlipThenAGoodReadingInsideTheGraceGivesNoTripOneWarningAndOneRecoveryLine() {
            FakeNina nina = ExposingNight(out Context context);
            List<string> trace = new List<string>();
            using CancellationTokenSource stop = new CancellationTokenSource();

            Task run = RunExposureAsync(context, trace, cancellationToken: stop.Token);
            nina.DomeReadThrows("boom");
            await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            nina.DomeReadsAgain();
            await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(65));

            Assert.That(run.IsCompleted, Is.False, "still exposing");
            Assert.That(trace, Does.Not.Contain("Roof route: execute"));
            Assert.That(nina.LogMessages("WARNING").Count(line => line.StartsWith("The roof watcher cannot read its device")), Is.EqualTo(1));
            Assert.That(nina.LogMessages("INFO").Count(line => line == "The roof watcher can read its device again."), Is.EqualTo(1));
            stop.Cancel();
            await run.WaitAsync(Soon);
        }

        // Each watcher that can go blind trips its own fail-safe, never another's: the window as closed, the roof as closed,
        // the limits as reached. The safety watcher can't go blind, since the monitor reads a failed read as unsafe.
        [TestCase("window", "Window route")]
        [TestCase("roof", "Roof route")]
        [TestCase("limits", "Limit route")]
        public async Task EachWatcherThatCanGoBlindTripsItsOwnFailSafe(string watcher, string route) {
            FakeNina nina = ExposingNight(out Context context);
            switch (watcher) {
                case "window":
                    nina.NightCalculationThrows("boom");
                    break;
                case "roof":
                    nina.DomeReadThrows("boom");
                    break;
                default:
                    nina.Settings.SetIsMountLimitEnabled(true);
                    nina.TelescopeInfo.Coordinates = null!;
                    await context.StartSessionAsync();
                    context.IsStartupComplete = true;
                    break;
            }
            List<string> trace = new List<string>();

            Task run = RunExposureAsync(context, trace);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)), Is.True);

            Assert.That(trace.Where(line => line.EndsWith(" route: execute")), Is.EqualTo(new[] { $"{route}: execute" }));
        }

        // The spell belongs to the watcher, not the state, so a blind stretch that started in one state carries into the
        // next inside the gap and trips there, rather than giving each state a fresh 30 s.
        [Test]
        public async Task ASpellCarriesAcrossConsecutiveWatchedStatesInsideTheGap() {
            FakeNina nina = ExposingNight(out Context context);
            nina.DomeReadThrows("boom");
            List<string> trace = new List<string>();
            ExposingState first = new ExposingState("First", trace, TimeSpan.FromSeconds(20), nina.Clock);
            first.OnCompleted(new ExposingState("Second", trace));

            Task run = RunExposureAsync(context, trace, first);
            Assert.That(await nina.AdvanceUntilAsync(run, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(40)), Is.True,
                "tripped 10 s into the second state, 30 s into the spell");

            Assert.That(trace, Does.Contain("Second: execute"));
            Assert.That(trace, Does.Contain("Roof route: execute"));
            Assert.That(nina.LogMessages("WARNING").Count(line => line.StartsWith("The roof watcher cannot read its device")), Is.EqualTo(1));
        }

        // A watcher's first check runs as the state starts, so a roof already closing aborts even a state that finishes
        // at once, like the disk check, which would otherwise walk the night on under a closing roof.
        [Test]
        public async Task ARoofClosingAbortsEvenAStateThatFinishesAtOnce() {
            FakeNina nina = ExposingNight(out Context context);
            nina.SetRoof(ShutterState.ShutterClosing);
            List<string> trace = new List<string>();

            await RunExposureAsync(context, trace, new CheckDiskSpace("Check Disk Space", context)).WaitAsync(Soon);

            Assert.That(trace, Does.Contain("Roof route: execute"));
        }
    }
}