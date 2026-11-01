using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot.States
{
    [TestFixture]
    public class CheckFramePointingTests
    {
        // A captured frame of M1 whose solve landed the given distance off target, or that was not solved at all,
        // under the default 2' recentre tolerance and N.I.N.A.'s pointing tolerance of zero.
        private static Context FrameSolvedOffBy(FakeNina nina, double? offsetArcmin)
        {
            Context context = nina.CreateContext();
            context.Frame = new Frame(context, new AutopilotNextResponse { TargetName = "M1", Ra = 83.6, Dec = 22.0 })
            {
                LastCapture = new CaptureResult
                {
                    IsSuccess = true,
                    IsPlateSolved = offsetArcmin != null,
                    PointingOffsetArcmin = offsetArcmin,
                },
            };
            return context;
        }

        private static Task<AutopilotStateResult> CheckAsync(Context context) =>
            StateRunner.RunAsync(new CheckFramePointing("Check Frame Pointing", context));

        // Only the frame's own solve can see a field 2° from where the mount says it is, so a miss past the tolerance asks
        // the next frame to slew and plate solve, and the log says by how much against what.
        [Test]
        public async Task ASolvePastTheToleranceAsksTheNextFrameToRecentre()
        {
            FakeNina nina = new FakeNina();
            Context context = FrameSolvedOffBy(nina, 5.0);

            AutopilotStateResult result = await CheckAsync(context);

            Assert.That(result, Is.EqualTo(AutopilotStateResult.Completed));
            Assert.That(context.IsLastFrameOffTarget, Is.True);
            Assert.That(nina.LogMessages("WARNING"), Does.Contain(
                "Frame solved 5.00' (tolerance 2.00') from M1, so the next frame will slew and plate solve before exposing."));
        }

        // A solve inside the tolerance leaves the next frame alone, and so does a frame that was never solved, since an
        // unsolved frame says nothing about where the mount pointed.
        [TestCase(1.5)]
        [TestCase(null)]
        public async Task ASolveInsideTheToleranceOrNoSolveLeavesTheNextFrameAlone(double? offsetArcmin)
        {
            FakeNina nina = new FakeNina();
            Context context = FrameSolvedOffBy(nina, offsetArcmin);

            await CheckAsync(context);

            Assert.That(context.IsLastFrameOffTarget, Is.False);
            Assert.That(nina.LogMessages("WARNING"), Is.Empty);
        }

        // Once centring has been given up on this target, a re-centre would cost minutes for nothing, so the miss is only
        // logged and the next frame is not sent back through the centring we gave up on.
        [Test]
        public async Task AfterCentringIsGivenUpTheMissIsOnlyLogged()
        {
            FakeNina nina = new FakeNina();
            Context context = FrameSolvedOffBy(nina, 5.0);
            context.Night.Centring.RecordFailure("no solver");
            context.Night.Centring.RecordFailure("no solver");

            await CheckAsync(context);

            Assert.That(context.IsLastFrameOffTarget, Is.False);
            Assert.That(nina.LogMessages("WARNING"), Is.Empty);
            Assert.That(nina.LogMessages("INFO"), Does.Contain(
                "Frame solved 5.00' (tolerance 2.00') from M1. Centring is unavailable for this target, so the next frame is not re-centred."));
        }

        // A miss just over the tolerance is printed to three decimals, so the line never shows a miss as equal to the
        // tolerance it exceeded.
        [Test]
        public async Task AMissJustOverTheTolerancePrintsThreeDecimals()
        {
            FakeNina nina = new FakeNina();
            Context context = FrameSolvedOffBy(nina, 2.004);

            await CheckAsync(context);

            Assert.That(nina.LogMessages("WARNING"), Has.Some.StartWith("Frame solved 2.004' (tolerance 2.000') from M1"));
        }
    }
}
