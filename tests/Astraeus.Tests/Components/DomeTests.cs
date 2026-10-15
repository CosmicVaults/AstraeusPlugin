using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using Moq;
using NINA.Equipment.Interfaces;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Components {
    [TestFixture]
    public class DomeTests {
        // A cap on waits that should end at once, so a regression fails instead of hanging the run.
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        // A dome started as the plugin starts it, so it has found the safety monitor, at 22:00 on a typical night.
        private static async Task<FakeNina> WatchingDomeAsync(ShutterState? shutter, bool? isSafe) {
            FakeNina nina = new FakeNina();
            nina.SetTypicalNight(new DateTime(2026, 3, 1, 22, 0, 0));
            nina.SetRoof(shutter);
            nina.SetSafety(isSafe);
            await nina.Dome.Start();
            return nina;
        }

        private static async Task PassAsync(FakeNina nina, int passes) {
            for (int pass = 0; pass < passes; pass++) await nina.Dome.LateUpdate();
        }

        ///////////// The shutter move /////////////

        // N.I.N.A. waits as long as the driver says "closing", so a stalled roof is given five clock minutes and then
        // reported, rather than holding the secure for the rest of the night.
        [Test]
        public async Task AStalledCloseIsGivenUpAfterFiveMinutesOnTheClock() {
            FakeNina nina = new FakeNina();
            nina.SetRoof(ShutterState.ShutterOpen);
            nina.DomeMediator.Setup(mediator => mediator.CloseShutter(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(async token => {
                    await Task.Delay(Timeout.Infinite, token);
                    return true;
                });

            Task<bool> closing = nina.Dome.CloseShutterAsync(CancellationToken.None);
            nina.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            Assert.That(closing.IsCompleted, Is.False, "a second early");
            nina.Clock.Advance(TimeSpan.FromSeconds(1));

            Assert.That(await closing.WaitAsync(Soon), Is.False);
            Assert.That(nina.LogMessages("ERROR"), Is.EqualTo(new[] { "Roof did not finish closing within 5 minutes." }));
        }

        ///////////// The roof watchdog /////////////

        // Outside the autopilot nothing closes the roof, so a roof open in unsafe conditions is an alert, once per breach,
        // and never a close: the plugin only moves the roof while the autopilot is working it.
        [Test]
        public async Task AnOpenRoofWhileUnsafeIsReportedOncePerBreachAndNeverClosed() {
            FakeNina nina = await WatchingDomeAsync(ShutterState.ShutterOpen, isSafe: false);

            await PassAsync(nina, 3);
            Assert.That(nina.Alerts, Has.Count.EqualTo(1), "once while the breach lasts");
            Observatory.ReportedEvent alert = nina.Alerts[0];
            Assert.Multiple(() => {
                Assert.That(alert.Message, Is.EqualTo("Roof is open and conditions are unsafe, and the autopilot is not watching it."));
                Assert.That(alert.Detail("shutter"), Is.EqualTo("ShutterOpen"));
                Assert.That(alert.Detail("mount"), Is.EqualTo("disconnected"));
                Assert.That(alert.Detail("action"), Is.EqualTo("none (Astraeus only closes the roof while the autopilot is working it)"));
                Assert.That(nina.LogMessages("CRITICAL"), Is.EqualTo(new[] {
                    "Roof is open and conditions are unsafe, and the autopilot is not watching it. Shutter ShutterOpen, " +
                    "mount disconnected, N.I.N.A. 'Close on unsafe' is off. Astraeus will not close it.",
                }));
            });

            nina.SetRoof(ShutterState.ShutterClosed);
            await PassAsync(nina, 1);
            nina.SetRoof(ShutterState.ShutterOpen);
            await PassAsync(nina, 1);
            Assert.That(nina.Alerts, Has.Count.EqualTo(2), "again for a new breach");
            Assert.That(nina.Commands, Is.Empty);
        }

        // A roof still opening is already letting the weather in, so it counts as open.
        [Test]
        public async Task AnOpeningRoofCountsAsOpen() {
            FakeNina nina = await WatchingDomeAsync(ShutterState.ShutterOpening, isSafe: false);

            await PassAsync(nina, 1);

            Assert.That(nina.Alerts.Single().Detail("shutter"), Is.EqualTo("ShutterOpening"));
        }

        // An open roof in safe conditions before dawn is a night in progress, not a breach.
        [Test]
        public async Task AnOpenRoofWhileSafeBeforeDawnIsNotReported() {
            FakeNina nina = await WatchingDomeAsync(ShutterState.ShutterOpen, isSafe: true);

            await PassAsync(nina, 3);

            Assert.That(nina.Alerts, Is.Empty);
        }

        // A roof still open after dawn is reported once per morning, whatever the weather.
        [Test]
        public async Task AnOpenRoofAfterDawnIsReportedOnce() {
            FakeNina nina = await WatchingDomeAsync(ShutterState.ShutterOpen, isSafe: true);
            nina.Clock.SetUtcNow(new DateTimeOffset(new DateTime(2026, 3, 2, 7, 0, 0), TimeSpan.Zero));

            await PassAsync(nina, 3);

            Assert.That(nina.Alerts.Select(alert => alert.Message),
                Is.EqualTo(new[] { "Roof is still open after dawn, and the autopilot is not watching it." }));
        }

        // A dome that isn't connected can't be watched, and that is not a breach either.
        [Test]
        public async Task ADisconnectedDomeIsNotWatched() {
            FakeNina nina = await WatchingDomeAsync(null, isSafe: false);

            await PassAsync(nina, 3);

            Assert.That(nina.Alerts, Is.Empty);
        }
    }
}