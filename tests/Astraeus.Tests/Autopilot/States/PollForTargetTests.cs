using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States {
    [TestFixture]
    public class PollForTargetTests {
        // The server's no-work slugs are logged in plain words, and an unknown slug is shown raw, so a newer server still says why.
        [TestCase("no_visible_targets", "No visible targets. Waiting...")]
        [TestCase("wrong_pier_side", "No targets reachable on this side of the pier. Waiting...")]
        [TestCase("no_pending_requests", "Nothing scheduled to image right now. Waiting...")]
        [TestCase("interval_not_elapsed", "Waiting for a repeat interval to elapse...")]
        [TestCase("fixed_time_imminent", "Waiting for a fixed-time window to open...")]
        [TestCase("plan_required", "Autopilot is not part of the current plan. Waiting...")]
        [TestCase("setup_incomplete", "Autopilot setup is not finished. Waiting...")]
        [TestCase("location_not_configured", "Observatory location is not set. Waiting...")]
        [TestCase("mercury_in_retrograde", "No image ready (mercury_in_retrograde). Waiting...")]
        public void ANoWorkReasonIsSaidInPlainWords(string reason, string expected) {
            Assert.That(PollForTarget.DescribeNoWork(reason), Is.EqualTo(expected));
        }

        // The wait is said the way a person reads a clock: seconds when it is short, whole minutes once it is not.
        [TestCase(60, "60 s")]
        [TestCase(119, "119 s")]
        [TestCase(120, "2 min")]
        [TestCase(900, "15 min")]
        [TestCase(3600, "60 min")]
        public void TheWaitIsSaidInSecondsOrMinutes(int seconds, string expected) {
            Assert.That(PollForTarget.DescribeWait(seconds), Is.EqualTo(expected));
        }

        // A no-work answer says when the next poll is, from the server's retry_after or the default minute, so the
        // dashboard shows how long a resubmitted target waits rather than leaving it to guess.
        [TestCase("""{"reason": "no_visible_targets", "retry_after": 900}""", 900, "No visible targets. Waiting... Next poll in 15 min.")]
        [TestCase("""{"reason": "no_visible_targets"}""", 60, "No visible targets. Waiting... Next poll in 60 s.")]
        public async Task ANoWorkAnswerSaysWhenTheNextPollIs(string reply, int delaySeconds, string line) {
            FakeNina nina = new FakeNina();
            nina.Server.Reply("GET", "/api/autopilot/next/", reply);
            Context context = nina.CreateContext();

            AutopilotStateResult result = await StateRunner.RunAsync(new PollForTarget("Poll for Target", context));

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Failed));
            Assert.That(context.NextPollDelaySeconds, Is.EqualTo(delaySeconds));
            Assert.That(nina.LogMessages("INFO"), Does.Contain(line));
        }
    }
}