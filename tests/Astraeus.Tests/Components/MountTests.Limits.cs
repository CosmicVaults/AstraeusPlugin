using System;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyTelescope;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Components {
    // The limit maths, on the same fixture as the rest of the mount.
    public partial class MountTests {
        // A German equatorial, connected and tracking on the given side, with mount limits on and a 5 min meridian limit.
        private static FakeNina GermanEquatorial(PierSide side, double raDegrees = 100, double decDegrees = 0) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsMountLimitEnabled(true);
            nina.SetMeridianLimit(maxMinutesAfterMeridian: 5);
            nina.SetMount(isConnected: true, isTracking: true);
            nina.TelescopeInfo.AlignmentMode = AlignmentMode.GermanPolar;
            nina.TelescopeInfo.SideOfPier = side;
            nina.TelescopeInfo.Coordinates = Pointing(raDegrees, decDegrees);
            return nina;
        }

        ///////////// Hour angles /////////////

        // An hour angle runs 0 to 24 westward. Past 12 it is really east, so the maths wants it signed, with 12 itself still west.
        [TestCase(0, 0)]
        [TestCase(6, 6)]
        [TestCase(12, 12)]
        [TestCase(13, -11)]
        [TestCase(23.5, -0.5)]
        public void AnHourAngleBecomesSignedHoursPastTheMeridian(double hourAngle, double expected) {
            Assert.That(Mount.HoursPastMeridian(hourAngle), Is.EqualTo(expected).Within(1e-9));
        }

        // Azimuths from a driver can arrive negative or past 360, and the horizon lookup wants one turn.
        [TestCase(0, 0)]
        [TestCase(359.5, 359.5)]
        [TestCase(360, 0)]
        [TestCase(-90, 270)]
        [TestCase(725, 5)]
        public void AnAzimuthIsBroughtIntoOneTurn(double azimuth, double expected) {
            Assert.That(Mount.NormalizeAzimuth(azimuth), Is.EqualTo(expected).Within(1e-9));
        }

        // The breach text says when it happens. Under half a minute ahead reads as now, so a look-ahead sample never says "in 0 min".
        [TestCase(0, "now")]
        [TestCase(20, "now")]
        [TestCase(100, "in 2 min")]
        [TestCase(300, "in 5 min")]
        public void TheWhenLabelRoundsToWholeMinutesAndCallsTheFirstHalfMinuteNow(int secondsAhead, string expected) {
            Assert.That(Mount.WhenLabel(TimeSpan.FromSeconds(secondsAhead)), Is.EqualTo(expected));
        }

        ///////////// The meridian limit /////////////

        // The limit mirrors about the meridian: pierWest breaches west of +L, pierEast east of -L, and an unknown side gets the
        // pierWest rule.
        [TestCase(PierSide.pierWest, 5.0 / 60)]
        [TestCase(PierSide.pierEast, -5.0 / 60)]
        [TestCase(PierSide.pierUnknown, 5.0 / 60)]
        public void TheMeridianThresholdMirrorsForTheEastSide(PierSide side, double expectedHours) {
            FakeNina nina = GermanEquatorial(side);

            Assert.That(nina.Mount.MeridianThresholdHours(side), Is.EqualTo(expectedHours).Within(1e-9));
        }

        // A pause before the meridian puts the west limit east of it, and the east side's mirror never crosses west, so it
        // clamps at zero.
        [Test]
        public void APauseBeforeTheMeridianMovesTheWestLimitEastAndClampsTheEastLimitAtZero() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest);
            nina.SetMeridianLimit(maxMinutesAfterMeridian: 5, pauseMinutesBeforeMeridian: 10);

            Assert.That(nina.Mount.MeridianThresholdHours(PierSide.pierWest), Is.EqualTo(-10.0 / 60).Within(1e-9));
            Assert.That(nina.Mount.MeridianThresholdHours(PierSide.pierEast), Is.EqualTo(0).Within(1e-9));
        }

        // Looking east (pierWest), the mount may track up to the limit past the meridian and no further, and the text names the limit.
        [TestCase(0.0, null)]
        [TestCase(3.0 / 60, null)]
        [TestCase(6.0 / 60, "past meridian limit (6 min west of the meridian now on pierWest, limit 5 min past the meridian)")]
        public void OnPierWestTheBreachIsWestOfTheLimit(double hoursPastMeridian, string? expected) {
            FakeNina nina = GermanEquatorial(PierSide.pierWest);

            Assert.That(nina.Mount.DescribeMeridianBreach(hoursPastMeridian, PierSide.pierWest, "now"), Is.EqualTo(expected));
        }

        // Looking west (pierEast), the mount is fine far past the meridian and breaches only east of the mirrored limit.
        [TestCase(0.5, null)]
        [TestCase(-3.0 / 60, null)]
        [TestCase(-6.0 / 60, "past meridian limit (6 min east of the meridian now on pierEast, limit 5 min east of the meridian)")]
        public void OnPierEastTheBreachIsEastOfTheMirroredLimit(double hoursPastMeridian, string? expected) {
            FakeNina nina = GermanEquatorial(PierSide.pierEast);

            Assert.That(nina.Mount.DescribeMeridianBreach(hoursPastMeridian, PierSide.pierEast, "now"), Is.EqualTo(expected));
        }

        // A GEM that doesn't report its side gets the pierWest rule, the cautious one, and the text leaves the side out.
        [Test]
        public void AnUnknownSideIsJudgedLikePierWestWithoutNamingASide() {
            FakeNina nina = GermanEquatorial(PierSide.pierUnknown);

            Assert.That(nina.Mount.DescribeMeridianBreach(6.0 / 60, PierSide.pierUnknown, "now"),
                Is.EqualTo("past meridian limit (6 min west of the meridian now, limit 5 min past the meridian)"));
        }

        // With a pause before the meridian the limit is east of it, and the text quotes the pause rather than a limit.
        [Test]
        public void APauseBeforeTheMeridianIsQuotedInTheBreach() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest);
            nina.SetMeridianLimit(maxMinutesAfterMeridian: 5, pauseMinutesBeforeMeridian: 10);

            Assert.That(nina.Mount.DescribeMeridianBreach(-6.0 / 60, PierSide.pierWest, "now"),
                Is.EqualTo("past meridian limit (6 min east of the meridian now on pierWest, pause 10 min before the meridian)"));
        }

        // Only a German equatorial has a pier to swing into, so a fork or alt-az has no meridian rule, and nor does a GEM with
        // limits off.
        [TestCase(AlignmentMode.Polar, true)]
        [TestCase(AlignmentMode.AltAz, true)]
        [TestCase(AlignmentMode.GermanPolar, false)]
        public void OnlyAGermanEquatorialWithLimitsOnHasAMeridianRule(AlignmentMode mode, bool isLimitEnabled) {
            FakeNina nina = GermanEquatorial(PierSide.pierWest);
            nina.TelescopeInfo.AlignmentMode = mode;
            nina.Settings.SetIsMountLimitEnabled(isLimitEnabled);

            Assert.That(nina.Mount.DescribeMeridianBreach(3.0, PierSide.pierWest, "now"), Is.Null);
        }

        ///////////// The altitude floors /////////////

        // Below the altitude limit is a breach that names the altitude and the limit, and nothing waives it.
        [TestCase(15.0, null)]
        [TestCase(5.0, "below altitude limit (5.00° now, minimum 10.0°)")]
        public void BelowTheAltitudeLimitIsABreach(double altitude, string? expected) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsAltitudeLimitEnabled(true);
            nina.Settings.SetMountMinAltitudeDegrees(10);

            Assert.That(nina.Mount.DescribeAltitudeBreach(altitude, 180, "now"), Is.EqualTo(expected));
        }

        // With the altitude limit off, a low altitude is nobody's business unless a horizon says otherwise.
        [Test]
        public void WithTheAltitudeLimitOffALowAltitudeIsNoBreach() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsAltitudeLimitEnabled(false);

            Assert.That(nina.Mount.DescribeAltitudeBreach(5, 180, "now"), Is.Null);
        }

        // The custom horizon is read at the pointing's azimuth, so a tree at 90° doesn't block the sky at 270°.
        [TestCase(90.0, 15.0, "below custom horizon (15.00° now at azimuth 90°, horizon 20.0°)")]
        [TestCase(90.0, 25.0, null)]
        [TestCase(270.0, 15.0, null)]
        public void BelowTheCustomHorizonAtThatAzimuthIsABreach(double azimuth, double altitude, string? expected) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsCustomHorizonLimitEnabled(true);
            nina.SetCustomHorizon("0 10\n90 20\n180 10\n270 10\n360 10");

            Assert.That(nina.Mount.DescribeAltitudeBreach(altitude, azimuth, "now"), Is.EqualTo(expected));
        }

        // The horizon only counts when its setting is on, so an observer who loaded one for framing isn't limited by it.
        [Test]
        public void TheCustomHorizonIsIgnoredWhenItsSettingIsOff() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsCustomHorizonLimitEnabled(false);
            nina.SetCustomHorizon("0 40\n360 40");

            Assert.That(nina.Mount.DescribeAltitudeBreach(15, 90, "now"), Is.Null);
        }

        ///////////// Reading the live position /////////////

        // A position is readable with a finite RA and Dec. Altitude and azimuth are optional in ASCOM, so NaN there doesn't
        // count against it.
        [TestCase("none", false)]
        [TestCase("NaN RA", false)]
        [TestCase("NaN Dec", false)]
        [TestCase("finite", true)]
        [TestCase("finite with NaN altitude and azimuth", true)]
        public void APositionIsReadableWithAFiniteRaAndDec(string position, bool isReadable) {
            TelescopeInfo info = new TelescopeInfo {
                Coordinates = position switch {
                    "NaN RA" => Pointing(double.NaN, 20),
                    "NaN Dec" => Pointing(10, double.NaN),
                    "none" => null!,
                    _ => Pointing(10, 20),
                },
                Altitude = position.Contains("NaN altitude") ? double.NaN : 45,
                Azimuth = position.Contains("NaN altitude") ? double.NaN : 180,
            };

            Assert.That(Mount.IsPositionReadable(info), Is.EqualTo(isReadable));
        }

        // The live check feeds the mount's own RA and the sidereal time into the meridian rule. A fixed sidereal time of 7 h
        // puts a mount at RA 100° (6 h 40 min) 20 min west, well past a 5 min limit.
        [Test]
        public void ALivePositionPastTheMeridianLimitIsABreach() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest, raDegrees: 100);
            nina.Mount.LocalSiderealTimeNow = _ => 7.0;

            Assert.That(nina.Mount.DescribePointingBreach(shouldIncludeMeridian: true),
                Is.EqualTo("past meridian limit (20 min west of the meridian now on pierWest, limit 5 min past the meridian)"));
        }

        // With a flip lined up the caller asks to leave the meridian out, and the same position is clear.
        [Test]
        public void TheMeridianIsLeftOutWhenTheCallerSaysAFlipIsComing() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest, raDegrees: 100);
            nina.Mount.LocalSiderealTimeNow = _ => 7.0;

            Assert.That(nina.Mount.DescribePointingBreach(shouldIncludeMeridian: false), Is.Null);
        }

        // A NaN RA on a GEM is unreadable, not a breach, on either side. One bad poll must never park the mount and end the night.
        [TestCase(PierSide.pierEast)]
        [TestCase(PierSide.pierWest)]
        public void ANaNRaIsNoBreachOnEitherSide(PierSide side) {
            FakeNina nina = GermanEquatorial(side, raDegrees: double.NaN);
            nina.Mount.LocalSiderealTimeNow = _ => 7.0;

            Assert.That(nina.Mount.DescribePointingBreach(shouldIncludeMeridian: true), Is.Null);
        }

        // No coordinates at all is the same: unreadable, and left to the unknown-position watcher.
        [Test]
        public void NoCoordinatesIsNoBreach() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest);
            nina.TelescopeInfo.Coordinates = null!;
            nina.Mount.LocalSiderealTimeNow = _ => 7.0;

            Assert.That(nina.Mount.DescribePointingBreach(shouldIncludeMeridian: true), Is.Null);
        }

        // NaN altitude and azimuth with a good RA and Dec still get the meridian check, since the hour angle needs neither.
        [Test]
        public void NaNAltitudeAndAzimuthStillGetTheMeridianCheck() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest, raDegrees: 100);
            nina.TelescopeInfo.Altitude = double.NaN;
            nina.TelescopeInfo.Azimuth = double.NaN;
            nina.Mount.LocalSiderealTimeNow = _ => 7.0;

            Assert.That(nina.Mount.DescribePointingBreach(shouldIncludeMeridian: true),
                Is.EqualTo("past meridian limit (20 min west of the meridian now on pierWest, limit 5 min past the meridian)"));
        }

        // The altitude floors come first. A mount below the limit says so even when it is also past the meridian.
        [Test]
        public void AnAltitudeBreachIsReportedBeforeAMeridianBreach() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest, raDegrees: 100);
            nina.Settings.SetIsAltitudeLimitEnabled(true);
            nina.Settings.SetMountMinAltitudeDegrees(10);
            nina.TelescopeInfo.Altitude = 5;
            nina.TelescopeInfo.Azimuth = 180;
            nina.Mount.LocalSiderealTimeNow = _ => 7.0;

            Assert.That(nina.Mount.DescribePointingBreach(shouldIncludeMeridian: true),
                Is.EqualTo("below altitude limit (5.00° now, minimum 10.0°)"));
        }

        // The altitude is read to two decimals, so a reading a hundredth under the floor doesn't print as equal to it,
        // which is what a night ending on "30.0° now, minimum 30.0°" looked like.
        [Test]
        public void AReadingJustUnderTheFloorShowsItsSecondDecimal() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsAltitudeLimitEnabled(true);
            nina.Settings.SetMountMinAltitudeDegrees(30);

            Assert.That(nina.Mount.DescribeAltitudeBreach(29.96, 180, "now"),
                Is.EqualTo("below altitude limit (29.96° now, minimum 30.0°)"));
        }

        // A prediction passes a margin: inside it the target is named as too close rather than below, and clear of it
        // there is no breach. The live check passes no margin, so only the floor itself counts there.
        [TestCase(29.96, 1.0, "below altitude limit (29.96° now, minimum 30.0°)")]
        [TestCase(30.50, 1.0, "within 1.0° of the altitude limit (30.50° now, minimum 30.0°)")]
        [TestCase(31.50, 1.0, null)]
        [TestCase(30.50, 0.0, null)]
        public void APredictionDemandsClearanceAboveTheAltitudeFloor(double altitude, double margin, string? expected) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsAltitudeLimitEnabled(true);
            nina.Settings.SetMountMinAltitudeDegrees(30);

            Assert.That(nina.Mount.DescribeAltitudeBreach(altitude, 180, "now", margin), Is.EqualTo(expected));
        }

        // The same margin applies above the custom horizon at the pointing's azimuth.
        [TestCase(20.50, "within 1.0° of the custom horizon (20.50° now at azimuth 90°, horizon 20.0°)")]
        [TestCase(21.50, null)]
        public void APredictionDemandsClearanceAboveTheCustomHorizon(double altitude, string? expected) {
            FakeNina nina = new FakeNina();
            nina.Settings.SetIsCustomHorizonLimitEnabled(true);
            nina.SetCustomHorizon("0 10\n90 20\n180 10\n270 10\n360 10");

            Assert.That(nina.Mount.DescribeAltitudeBreach(altitude, 90, "now", 1.0), Is.EqualTo(expected));
        }

        // A parked mount never breaches, because parking is where every remedy ends.
        [Test]
        public void AParkedMountNeverBreaches() {
            FakeNina nina = GermanEquatorial(PierSide.pierWest, raDegrees: 100);
            nina.TelescopeInfo.AtPark = true;
            nina.Mount.LocalSiderealTimeNow = _ => 7.0;

            Assert.That(nina.Mount.DescribeCurrentPositionBreach(), Is.Null);
            Assert.That(nina.Mount.IsCurrentPositionWithinLimits(), Is.True);
        }
    }
}