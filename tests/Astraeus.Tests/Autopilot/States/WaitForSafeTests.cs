using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States {
    [TestFixture]
    public class WaitForSafeTests {
        private static readonly TimeSpan Poll = TimeSpan.FromSeconds(Context.SafetyPollIntervalSeconds);

        // Waiting out weather under an operated roof: the roof closed, the mount parked, the monitor reading unsafe, and
        // no settle unless a test asks for one.
        private static FakeNina UnsafeNight() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetAutopilotRoofMode(RoofMode.Operate);
            nina.Settings.SetSafeSettleSeconds(0);
            nina.SetRoof(ShutterState.ShutterClosed);
            nina.SetMount(isConnected: true, isParked: true);
            nina.SetSafety(false);
            return nina;
        }

        private static WaitForSafe Wait(Context context) => new WaitForSafe("Wait for Safe Conditions", context);

        // Reaching the wait means every device connected, so from here a disconnected device is a fault, not someone
        // powering up late, and under Operate the roof is the autopilot's to work.
        [TestCase(RoofMode.Operate, true)]
        [TestCase(RoofMode.WaitForOpen, false)]
        public async Task EntryMarksStartupCompleteAndTakesTheRoofOnlyUnderOperate(RoofMode mode, bool isRoofOurs) {
            FakeNina nina = UnsafeNight();
            nina.Settings.SetAutopilotRoofMode(mode);
            Context context = nina.CreateContext();
            context.IsRoofExpectedOpen = true;

            await Wait(context).OnEntryAsync(CancellationToken.None);

            Assert.That(context.IsStartupComplete, Is.True);
            Assert.That(context.IsRoofUnderAutopilotControl, Is.EqualTo(isRoofOurs));
            Assert.That(context.IsRoofExpectedOpen, Is.False);
        }

        // The wait polls on the clock, says once that it is waiting, and ends on the first Safe reading when there is no settle.
        [Test]
        public async Task TheWaitEndsOnTheFirstSafePollWhenThereIsNoSettle() {
            FakeNina nina = UnsafeNight();
            Context context = nina.CreateContext();

            Task<AutopilotStateResult> run = StateRunner.RunAsync(Wait(context));
            Assert.That(await nina.AdvanceUntilAsync(run, Poll, Poll * 3), Is.False, "still unsafe");
            Assert.That(context.AutopilotStatus, Is.EqualTo(AutopilotStatus.WaitingSafety));
            Assert.That(nina.LogMessages("INFO").Count(line => line == "Waiting for conditions to become safe"), Is.EqualTo(1));
            nina.SetSafety(true);

            Assert.That(await nina.AdvanceUntilAsync(run, Poll, Poll * 2), Is.True);
            Assert.That(await run, Is.EqualTo(AutopilotStateResult.Completed));
        }

        // A monitor that flaps in marginal weather would open, unpark and slew on one Safe reading. Nothing resumes until it
        // has read Safe for the whole settle, and an unsafe reading part-way starts the settle over.
        [Test]
        public async Task ASettleHoldsTheResumeUntilConditionsHaveBeenSafeForTheWholeOfIt() {
            FakeNina nina = UnsafeNight();
            nina.Settings.SetSafeSettleSeconds(120);
            Context context = nina.CreateContext();
            TimeSpan step = TimeSpan.FromSeconds(10);

            Task<AutopilotStateResult> run = StateRunner.RunAsync(Wait(context));
            nina.SetSafety(true);
            Assert.That(await nina.AdvanceUntilAsync(run, step, Poll), Is.False);
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Conditions safe, so settling for 120 s before resuming."));
            Assert.That(context.SafeSinceUtc, Is.Not.Null, "the dashboard counts the settle from this");
            Assert.That(await nina.AdvanceUntilAsync(run, step, TimeSpan.FromSeconds(60)), Is.False, "half way through the settle");

            nina.SetSafety(false);
            Assert.That(await nina.AdvanceUntilAsync(run, step, step), Is.False);
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Conditions turned unsafe again during the settle, so the wait starts over."));
            Assert.That(context.SafeSinceUtc, Is.Null);

            nina.SetSafety(true);
            Assert.That(await nina.AdvanceUntilAsync(run, step, Poll), Is.False, "a new settle has started");
            Assert.That(await nina.AdvanceUntilAsync(run, step, TimeSpan.FromSeconds(130)), Is.True);
            Assert.That(await run, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(context.SafeSinceUtc, Is.Null, "cleared on exit so the card stops counting");
        }

        // A roof the secure couldn't close gets another go every five minutes while it stays unsafe, as log lines only,
        // since the secure already sent the alarm.
        [Test]
        public async Task AnOpenRoofIsReclosedWhileUnsafeOnAFiveMinuteCooldown() {
            FakeNina nina = UnsafeNight();
            nina.SetRoof(ShutterState.ShutterOpen);
            nina.RoofClosesButStillReadsOpen();

            Task<AutopilotStateResult> run = StateRunner.RunAsync(Wait(nina.CreateContext()));
            Assert.That(await nina.AdvanceUntilAsync(run, Poll, TimeSpan.FromMinutes(12)), Is.False, "still unsafe");

            Assert.That(nina.Commands.Count(command => command == "dome: CloseShutter"), Is.EqualTo(3), "at 0, 5 and 10 minutes");
            Assert.That(nina.LogMessages("WARNING"), Does.Contain("Roof is open while conditions are unsafe, so trying to close it."));
            Assert.That(nina.LogMessages("WARNING"), Does.Contain("Roof is still not closed (shutter ShutterOpen). Trying again in 5 minutes."));
            Assert.That(nina.Alerts, Is.Empty);
        }

        // The reclose only closes over a parked mount, so an unparked mount leaves the roof open with a warning.
        [Test]
        public async Task AnOpenRoofIsNotReclosedOverAnUnparkedMount() {
            FakeNina nina = UnsafeNight();
            nina.SetRoof(ShutterState.ShutterOpen);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.RoofClosesAtOnce();

            Task<AutopilotStateResult> run = StateRunner.RunAsync(Wait(nina.CreateContext()));
            await nina.AdvanceUntilAsync(run, Poll, Poll);

            Assert.That(nina.Commands, Is.Empty);
            Assert.That(nina.LogMessages("WARNING"), Does.Contain("Not closing the roof (conditions unsafe): the mount is not confirmed parked."));
        }

        // A roof that does close is logged closed and left alone from then on.
        [Test]
        public async Task AnOpenRoofThatClosesIsLoggedClosedAndNotTriedAgain() {
            FakeNina nina = UnsafeNight();
            nina.SetRoof(ShutterState.ShutterOpen);
            nina.RoofClosesAtOnce();

            Task<AutopilotStateResult> run = StateRunner.RunAsync(Wait(nina.CreateContext()));
            await nina.AdvanceUntilAsync(run, Poll, TimeSpan.FromMinutes(12));

            Assert.That(nina.Commands, Is.EqualTo(new[] { "dome: CloseShutter" }));
            Assert.That(nina.LogMessages("INFO"), Does.Contain("Roof closed."));
        }

        // A roof the autopilot doesn't operate is never reclosed by it, open or not.
        [Test]
        public async Task ARoofThatIsNotOursIsNotReclosed() {
            FakeNina nina = UnsafeNight();
            nina.Settings.SetAutopilotRoofMode(RoofMode.WaitForOpen);
            nina.SetRoof(ShutterState.ShutterOpen);
            nina.RoofClosesAtOnce();

            Task<AutopilotStateResult> run = StateRunner.RunAsync(Wait(nina.CreateContext()));
            await nina.AdvanceUntilAsync(run, Poll, TimeSpan.FromMinutes(6));

            Assert.That(nina.Commands, Is.Empty);
        }

        // A dropped safety monitor reads unsafe and would hold the night here, so it gets the autopilot's one reconnect.
        [Test]
        public async Task ADroppedSafetyMonitorGetsOneReconnect() {
            FakeNina nina = UnsafeNight();
            nina.SetSafety(null);
            Context context = nina.CreateContext();
            nina.LetSafetyMonitorReconnect(context, () => {
                nina.SetSafety(true);
                return true;
            });

            AutopilotStateResult result = await StateRunner.RunAsync(Wait(context));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(nina.LogMessages("INFO"), Has.Some.EndWith(" reconnected."));
        }
    }
}