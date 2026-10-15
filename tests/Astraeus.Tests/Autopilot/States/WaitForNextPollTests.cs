using System;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States {
    [TestFixture]
    public class WaitForNextPollTests {
        // A cap on waits that should end at once, so a regression fails instead of hanging the run.
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        // The wait between polls runs on the clock, so a test moves it along instead of sitting through it, and it ends
        // on the second it should.
        [Test]
        public async Task TheWaitEndsWhenItsSecondsPassOnTheClock() {
            FakeNina nina = new FakeNina();
            WaitForNextPoll wait = new WaitForNextPoll("Wait", nina.CreateContext(), fixedSeconds: 30);
            await wait.OnEntryAsync(CancellationToken.None);
            Task<AutopilotStateResult> run = wait.ExecuteAsync(CancellationToken.None);

            nina.Clock.Advance(TimeSpan.FromSeconds(29));
            Assert.That(run.IsCompleted, Is.False, "a second early");
            nina.Clock.Advance(TimeSpan.FromSeconds(1));

            Assert.That(await run.WaitAsync(Soon), Is.EqualTo(AutopilotStateResult.Completed));
        }

        // A negative RetryAfter from a server bug is clamped to no wait rather than faulting the machine.
        [Test]
        public async Task ANegativeWaitIsNoWait() {
            FakeNina nina = new FakeNina();
            WaitForNextPoll wait = new WaitForNextPoll("Wait", nina.CreateContext(), fixedSeconds: -5);
            await wait.OnEntryAsync(CancellationToken.None);

            AutopilotStateResult result = await wait.ExecuteAsync(CancellationToken.None).WaitAsync(Soon);

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
        }
    }
}