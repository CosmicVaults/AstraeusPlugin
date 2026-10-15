using System.Linq;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Calibration {
    /// <summary>
    /// Ensure calibration plan is parsed correctly. 
    /// </summary>
    [TestFixture]
    public class CalibrationPlanTests {
        private const string FullBlock = """
            {
                "strategy": "scaled_dark",
                "dark_scaling_ratio": 2.5,
                "keep_raw": false,
                "warnings": ["dark is 3 months old", "flat is for another filter"],
                "masters": {
                    "bias": {"id": 4, "revision": 1},
                    "dark": {"id": 12, "revision": 3, "checksum_sha256": "abc123"},
                    "flat": {"id": 7}
                }
            }
            """;

        // Every field of a full block reads through. This is the test that catches a renamed field in the server's
        // serializer, so it checks them all at once.
        [Test]
        public void APlanReadsEveryField() {
            CalibrationPlan? plan = CalibrationPlan.Parse(JObject.Parse(FullBlock));

            Assert.That(plan, Is.Not.Null);
            Assert.Multiple(() => {
                Assert.That(plan!.Strategy, Is.EqualTo("scaled_dark"));
                Assert.That(plan.DarkScalingRatio, Is.EqualTo(2.5));
                Assert.That(plan.ShouldKeepRaw, Is.False);
                Assert.That(plan.Warnings, Is.EqualTo(new[] { "dark is 3 months old", "flat is for another filter" }));
                Assert.That(plan.Bias, Is.EqualTo(new MasterRef(4, 1, null)));
                Assert.That(plan.Dark, Is.EqualTo(new MasterRef(12, 3, "abc123")));
                Assert.That(plan.Flat, Is.EqualTo(new MasterRef(7, 0, null)));
            });
        }

        // A sparse block takes safe defaults, and keep raw defaults to on, since a calibrated-only frame can't be
        // redone.
        [Test]
        public void MissingFieldsTakeTheirDefaults() {
            CalibrationPlan? plan = CalibrationPlan.Parse(JObject.Parse("""{"masters": {"flat": {"id": 7}}}"""));

            Assert.That(plan, Is.Not.Null);
            Assert.Multiple(() => {
                Assert.That(plan!.Strategy, Is.EqualTo("none"));
                Assert.That(plan.DarkScalingRatio, Is.Null);
                Assert.That(plan.ShouldKeepRaw, Is.True);
                Assert.That(plan.Warnings, Is.Empty);
            });
        }

        // A block with a strategy but no usable master is no plan, so the calibrator is never handed nothing to
        // apply.
        [TestCase("""{"strategy": "none", "masters": {}}""")]
        [TestCase("""{"strategy": "none"}""")]
        [TestCase("""{"masters": {"dark": {"revision": 2}}}""")]
        public void ABlockThatNamesNoMasterIsNoPlan(string json) {
            Assert.That(CalibrationPlan.Parse(JObject.Parse(json)), Is.Null);
        }

        // No block, or a block that isn't an object, means don't calibrate, with no exception on the registration
        // path.
        [Test]
        public void AMissingOrNonObjectBlockIsNoPlan() {
            Assert.That(CalibrationPlan.Parse(null), Is.Null);
            Assert.That(CalibrationPlan.Parse(new JValue("none")), Is.Null);
        }

        // Bias, then dark, then flat is the physics. The calibrator walks this list, so the order must hold and
        // missing roles must be skipped.
        [Test]
        public void RolesComeInApplicationOrderAndSkipMissingMasters() {
            CalibrationPlan? plan = CalibrationPlan.Parse(
                JObject.Parse("""{"masters": {"flat": {"id": 7}, "dark": {"id": 12}}}"""));

            Assert.That(plan!.Roles.Select(role => role.Role), Is.EqualTo(new[] { "dark", "flat" }));
        }

        // Warnings go straight into the log, so a stray null or empty string must not become a blank line.
        [Test]
        public void WarningsSkipEmptyAndNullEntries() {
            CalibrationPlan? plan = CalibrationPlan.Parse(
                JObject.Parse("""{"warnings": ["old dark", "", null], "masters": {"flat": {"id": 7}}}"""));

            Assert.That(plan!.Warnings, Is.EqualTo(new[] { "old dark" }));
        }

        // A warnings value of the wrong shape is treated as no warnings rather than failing the plan.
        [Test]
        public void WarningsThatAreNotAnArrayAreIgnored() {
            CalibrationPlan? plan = CalibrationPlan.Parse(
                JObject.Parse("""{"warnings": "old dark", "masters": {"flat": {"id": 7}}}"""));

            Assert.That(plan!.Warnings, Is.Empty);
        }

        // Pins the log line's shape with scaling and calibrated-only, so the night's log keeps reading the same.
        [Test]
        public void DescribeNamesTheStrategyScalingMastersAndRawHandling() {
            CalibrationPlan? plan = CalibrationPlan.Parse(JObject.Parse(FullBlock));

            Assert.That(plan!.Describe(), Is.EqualTo("scaled_dark x2.5 (bias #4, dark #12, flat #7), calibrated only"));
        }

        // Without a scaling ratio the line drops the x factor rather than printing a placeholder.
        [Test]
        public void DescribeLeavesOutScalingWhenThereIsNone() {
            CalibrationPlan? plan = CalibrationPlan.Parse(JObject.Parse("""{"masters": {"flat": {"id": 7}}}"""));
            Assert.That(plan!.Describe(), Is.EqualTo("none (flat #7), keep raw"));
        }
    }
}