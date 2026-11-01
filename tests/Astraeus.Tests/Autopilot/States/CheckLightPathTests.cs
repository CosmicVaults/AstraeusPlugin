using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States
{
    [TestFixture]
    public class CheckLightPathTests
    {
        // Puts a captured frame of M1 in hand with the given star count and solve, on the same context so the night's
        // run of dark frames carries from one frame to the next.
        private static void CaptureFrame(Context context, int stars, bool isSolved, bool wasSolveAttempted = true)
        {
            context.Frame = new Frame(context, new AutopilotNextResponse { TargetName = "M1" })
            {
                LastCapture = new CaptureResult
                {
                    IsSuccess = true,
                    WasPlateSolveAttempted = wasSolveAttempted,
                    IsPlateSolved = isSolved,
                    DetectedStars = stars,
                    Median = 512,
                },
            };
        }

        private static Task<AutopilotStateResult> CheckAsync(Context context) =>
            StateRunner.RunAsync(new CheckLightPath("Check Light Path", context));

        // Three frames in a row with almost no stars and no solve send one "no stars" alert naming the target and the
        // counts, and a fourth sends nothing more, so a blocked light path is said once and the night carries on.
        [Test]
        public async Task ThreeDarkFramesInARowAlertOnceAndAFourthStaysQuiet()
        {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();

            for (int frame = 1; frame <= 4; frame++)
            {
                CaptureFrame(context, stars: 10, isSolved: false);
                Assert.That(await CheckAsync(context), Is.EqualTo(AutopilotStateResult.Completed));
                Assert.That(nina.Alerts, Has.Count.EqualTo(frame >= 3 ? 1 : 0), $"alerts after frame {frame}");
            }

            Observatory.ReportedEvent alert = nina.Alerts[0];
            Assert.That(alert.Message, Is.EqualTo("Autopilot: frames show no stars. The light path may be blocked."));
            Assert.That(alert.Detail("target"), Is.EqualTo("M1"));
            Assert.That(alert.Detail("frames"), Is.EqualTo(3));
            Assert.That(alert.Detail("stars"), Is.EqualTo("10, 10, 10"));
        }

        // A frame with stars, or one that solved, ends the run, so the count starts again and a later run of dark frames
        // alerts afresh rather than being swallowed by the first alert.
        [Test]
        public async Task AFrameWithStarsEndsTheRunAndALaterRunAlertsAgain()
        {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();
            CaptureFrame(context, stars: 10, isSolved: false);
            await CheckAsync(context);
            CaptureFrame(context, stars: 10, isSolved: false);
            await CheckAsync(context);

            CaptureFrame(context, stars: 200, isSolved: true);
            await CheckAsync(context);

            Assert.That(context.Night.DarkFrameStarCounts, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
            for (int frame = 1; frame <= 3; frame++)
            {
                CaptureFrame(context, stars: 12, isSolved: false);
                await CheckAsync(context);
            }
            Assert.That(nina.Alerts, Has.Count.EqualTo(1));
        }

        // With no plate solver set up, an unsolved frame says nothing about the sky, so it is never counted as dark.
        [Test]
        public async Task WithoutASolveAttemptAFrameIsNeverDark()
        {
            FakeNina nina = new FakeNina();
            Context context = nina.CreateContext();

            for (int frame = 1; frame <= 3; frame++)
            {
                CaptureFrame(context, stars: 10, isSolved: false, wasSolveAttempted: false);
                await CheckAsync(context);
            }

            Assert.That(context.Night.DarkFrameStarCounts, Is.Empty);
            Assert.That(nina.Alerts, Is.Empty);
        }
    }
}
