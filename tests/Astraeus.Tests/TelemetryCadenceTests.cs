using System;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests {
    [TestFixture]
    public class TelemetryCadenceTests {
        // A cap on waits that should end at once, so a regression fails instead of hanging the run.
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        private FakeTimeProvider _clock = new();

        [SetUp]
        public void NewClock() => _clock = new FakeTimeProvider();

        private TelemetryCadence Cadence(FakeWebSocketBus bus) => new TelemetryCadence(bus, _clock);

        ///////////// Presence /////////////

        // Nobody is watching until the server says so, so a fresh plugin publishes nothing and ticks at the default.
        [Test]
        public void NobodyIsWatchingUntilTheServerSaysSo() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());

            Assert.Multiple(() => {
                Assert.That(cadence.Subscribers, Is.Zero);
                Assert.That(cadence.FeedSubscribers, Is.Zero);
                Assert.That(cadence.ShouldPublish, Is.False);
                Assert.That(cadence.HasFeedSubscribers, Is.False);
                Assert.That(cadence.CurrentInterval, Is.EqualTo(TimeSpan.FromSeconds(2)));
            });
        }

        // The server's presence message is what turns telemetry on and sets its pace.
        [Test]
        public void APresenceMessageSetsTheCountsAndTheTick() {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);

            bus.Receive(Presence("""{"subscribers": 3, "feedSubscribers": 1, "tickRateMs": 500}"""));

            Assert.Multiple(() => {
                Assert.That(cadence.Subscribers, Is.EqualTo(3));
                Assert.That(cadence.FeedSubscribers, Is.EqualTo(1));
                Assert.That(cadence.ShouldPublish, Is.True);
                Assert.That(cadence.HasFeedSubscribers, Is.True);
                Assert.That(cadence.CurrentInterval, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
            });
        }

        // An older server sends one count, and treating everyone as a webcam viewer keeps the feed working with it.
        [Test]
        public void WithoutAFeedCountEveryViewerCountsAsAFeedViewer() {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);

            bus.Receive(Presence("""{"subscribers": 2}"""));

            Assert.That(cadence.FeedSubscribers, Is.EqualTo(2));
        }

        // A server bug sending a negative count must not read as "somebody is watching".
        [Test]
        public void ANegativeCountReadsAsZero() {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);

            bus.Receive(Presence("""{"subscribers": -1, "feedSubscribers": -5}"""));

            Assert.That(cadence.Subscribers, Is.Zero);
            Assert.That(cadence.HasSubscribers, Is.False);
        }

        // A tick the server gets wrong must neither flood the loop nor wedge it, so it is held between 100 ms and 60 s.
        [TestCase(10, 100)]
        [TestCase(600000, 60000)]
        public void TheTickIsClampedToASaneRange(int tickRateMs, int expectedMs) {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);

            bus.Receive(Presence($$"""{"subscribers": 1, "tickRateMs": {{tickRateMs}}}"""));

            Assert.That(cadence.CurrentInterval, Is.EqualTo(TimeSpan.FromMilliseconds(expectedMs)));
        }

        // A tick that isn't a number is ignored rather than resetting the pace, so a malformed keepalive changes nothing.
        [Test]
        public void ATickThatIsNotANumberKeepsThePreviousTick() {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);
            bus.Receive(Presence("""{"subscribers": 1, "tickRateMs": 500}"""));

            bus.Receive(Presence("""{"subscribers": 1, "tickRateMs": "fast"}"""));

            Assert.That(cadence.CurrentInterval, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        }

        // While something moves the screen must keep up, but busy may only speed the tick up, never slow it down.
        [TestCase(2000, 1000)]
        [TestCase(500, 500)]
        public void BusyShortensTheTickToOneSecondButNeverLengthensIt(int tickRateMs, int expectedMs) {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);
            bus.Receive(Presence($$"""{"subscribers": 1, "tickRateMs": {{tickRateMs}}}"""));

            cadence.IsBusy = true;

            Assert.That(cadence.CurrentInterval, Is.EqualTo(TimeSpan.FromMilliseconds(expectedMs)));
        }

        // Only the server's presence message counts, so a command for another device or action can't change who is watching.
        [TestCase("mount", "presence")]
        [TestCase("system", "ping")]
        public void CommandsForAnythingElseAreIgnored(string device, string action) {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);

            bus.Receive(new WsCommand("1", device, action, Json.Parse("""{"subscribers": 3}""")));

            Assert.That(cadence.Subscribers, Is.Zero);
        }

        ///////////// Connection /////////////

        // A dropped socket must not leave the plugin capturing for viewers it has lost, so the counts reset to nobody.
        [Test]
        public void ADisconnectResetsTheCountsAndTheTick() {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);
            bus.Receive(Presence("""{"subscribers": 3, "feedSubscribers": 1, "tickRateMs": 500}"""));

            bus.RaiseDisconnected();

            Assert.Multiple(() => {
                Assert.That(cadence.Subscribers, Is.Zero);
                Assert.That(cadence.FeedSubscribers, Is.Zero);
                Assert.That(cadence.CurrentInterval, Is.EqualTo(TimeSpan.FromSeconds(2)));
            });
        }

        // A reconnect leaves the counts alone, since the server's snapshot follows within a round trip and they were reset on the drop.
        [Test]
        public void AConnectLeavesTheCountsAlone() {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);
            bus.Receive(Presence("""{"subscribers": 3}"""));

            bus.RaiseConnected();

            Assert.That(cadence.Subscribers, Is.EqualTo(3));
        }

        ///////////// The wake-up /////////////

        // Every loop waits on Changed, so a Signal must release all of them at once and arm a fresh one for the next wait.
        [Test]
        public void SignalCompletesTheCurrentChangedAndArmsANewOne() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());
            Task before = cadence.Changed;

            cadence.Signal();

            Assert.That(before.IsCompletedSuccessfully, Is.True);
            Assert.That(cadence.Changed.IsCompleted, Is.False);
        }

        // Presence, connect and disconnect all have to wake the loops, or a viewer arriving would sit out a 60 s idle sleep.
        [Test]
        public void PresenceConnectAndDisconnectAllSignal() {
            FakeWebSocketBus bus = new FakeWebSocketBus();
            TelemetryCadence cadence = Cadence(bus);

            Task onPresence = cadence.Changed;
            bus.Receive(Presence("""{"subscribers": 1}"""));
            Task onConnect = cadence.Changed;
            bus.RaiseConnected();
            Task onDisconnect = cadence.Changed;
            bus.RaiseDisconnected();

            Assert.Multiple(() => {
                Assert.That(onPresence.IsCompleted, Is.True);
                Assert.That(onConnect.IsCompleted, Is.True);
                Assert.That(onDisconnect.IsCompleted, Is.True);
            });
        }

        // A condition that already holds must return at once, so a loop never sleeps when there is work to do.
        [Test]
        public async Task WaitUntilReturnsAtOnceWhenTheConditionAlreadyHolds() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());

            bool isMet = await cadence.WaitUntilAsync(() => true, TimeSpan.FromMinutes(10), CancellationToken.None);

            Assert.That(isMet, Is.True);
        }

        // A timeout that is already up reports the condition as not met without waiting.
        [Test]
        public async Task WaitUntilReportsFalseWhenTheTimeoutIsAlreadyUp() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());

            bool isMet = await cadence.WaitUntilAsync(() => false, TimeSpan.Zero, CancellationToken.None);

            Assert.That(isMet, Is.False);
        }

        // The wait re-checks the condition on every signal, which is how a viewer arriving ends an idle wait early.
        [Test]
        public async Task WaitUntilEndsWhenASignalMakesTheConditionTrue() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());
            bool isReady = false;
            Task<bool> wait = cadence.WaitUntilAsync(() => isReady, TimeSpan.FromMinutes(10), CancellationToken.None);

            isReady = true;
            cadence.Signal();

            Assert.That(await wait.WaitAsync(Soon), Is.True);
        }

        // A zero delay returns without touching the timer, so a loop asked to wait for nothing doesn't lose a tick.
        [Test]
        public async Task WaitReturnsAtOnceForAZeroDelay() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());

            await cadence.WaitAsync(() => TimeSpan.Zero, CancellationToken.None).WaitAsync(Soon);
        }

        // A long idle sleep must end as soon as presence changes, not when the sleep would have.
        [Test]
        public async Task WaitIsReleasedByASignal() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());
            Task wait = cadence.WaitAsync(() => TimeSpan.FromMinutes(10), CancellationToken.None);

            cadence.Signal();

            await wait.WaitAsync(Soon);
        }

        // Shutdown cancels the loops' token, and a wait must throw on it rather than report a timeout and carry on.
        [Test]
        public void WaitThrowsWhenCancelled() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(
                () => cadence.WaitAsync(() => TimeSpan.FromMinutes(10), cancellation.Token));
        }

        // With nobody signalling, the wait ends when the clock passes the timeout and reports the condition as not met.
        [Test]
        public async Task WaitUntilReportsFalseWhenTheTimeoutRunsOut() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());
            Task<bool> wait = cadence.WaitUntilAsync(() => false, TimeSpan.FromMinutes(1), CancellationToken.None);

            _clock.Advance(TimeSpan.FromMinutes(1));

            Assert.That(await wait.WaitAsync(Soon), Is.False);
        }

        // An idle sleep ends when its delay runs out on the clock and not a second before, so the loop isn't woken for nothing.
        [Test]
        public async Task WaitEndsWhenTheDelayRunsOutOnTheClock() {
            TelemetryCadence cadence = Cadence(new FakeWebSocketBus());
            Task wait = cadence.WaitAsync(() => TimeSpan.FromSeconds(60), CancellationToken.None);

            _clock.Advance(TimeSpan.FromSeconds(59));
            Assert.That(wait.IsCompleted, Is.False, "a second early");
            _clock.Advance(TimeSpan.FromSeconds(1));

            await wait.WaitAsync(Soon);
        }

        private static WsCommand Presence(string payloadJson) =>
            new WsCommand("1", "system", "presence", Json.Parse(payloadJson));
    }
}