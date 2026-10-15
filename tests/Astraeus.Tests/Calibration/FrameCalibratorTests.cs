using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using NINA.Image.ImageData;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Calibration {
    [TestFixture]
    public class FrameCalibratorTests {
        private static ushort[] Pixels(params int[] values) => values.Select(value => (ushort)value).ToArray();

        private static ushort[] Filled(int count, int value) => Enumerable.Repeat((ushort)value, count).ToArray();

        // The arithmetic with no plan warnings, when a test doesn't care what it added.
        private static ushort[] Calibrate(ushort[] light, ushort[]? bias = null, ushort[]? dark = null, ushort[]? flat = null,
            double? darkScalingRatio = null) =>
            FrameCalibrator.Apply(light, bias, dark, flat, darkScalingRatio, new List<string>(), out double _);

        ///////////// Apply /////////////

        // A matched dark is subtracted whole and the pedestal added, so the background sits above zero instead of clipping.
        [Test]
        public void AMatchedDarkIsSubtractedAndThePedestalAdded() {
            ushort[] result = Calibrate(Pixels(1000, 2000), dark: Pixels(100, 100));

            Assert.That(result, Is.EqualTo(Pixels(1900, 2900)));
        }

        // With no dark the bias alone comes off, which is the whole of a bias-only plan.
        [Test]
        public void ABiasAloneIsSubtracted() {
            Assert.That(Calibrate(Pixels(1000), bias: Pixels(100)), Is.EqualTo(Pixels(1900)));
        }

        // A scaled dark is the bias plus the dark current scaled by the exposure ratio, so a 600 s dark serves a 300 s light.
        [Test]
        public void AScaledDarkIsTheBiasPlusTheDarkCurrentTimesTheRatio() {
            ushort[] result = Calibrate(Pixels(1000), bias: Pixels(100), dark: Pixels(300), darkScalingRatio: 0.5);

            Assert.That(result, Is.EqualTo(Pixels(1800)));
        }

        // Scaling needs the bias to separate the dark current from the offset, so without one the dark is used whole and
        // the ratio ignored, rather than scaling the offset too.
        [Test]
        public void TheRatioIsIgnoredWithoutABias() {
            ushort[] result = Calibrate(Pixels(1000), dark: Pixels(300), darkScalingRatio: 0.5);

            Assert.That(result, Is.EqualTo(Pixels(1700)));
        }

        // The flat is normalised to the median of its sampled pixels (every sixteenth), so a pixel at half the median is
        // brightened by two and a median one is left alone.
        [Test]
        public void AFlatDividesByItsGainAboutTheMedian() {
            ushort[] flat = Filled(32, 2000);
            flat[16] = 1000;

            ushort[] result = FrameCalibrator.Apply(Filled(32, 1000), null, null, flat, null, new List<string>(), out double flatNorm);

            Assert.That(flatNorm, Is.EqualTo(2000));
            Assert.That(result[0], Is.EqualTo(2000));
            Assert.That(result[16], Is.EqualTo(3000));
        }

        // A flat that is all zero after the bias has nothing to divide by, so it is dropped with a warning rather than
        // blowing every pixel up.
        [Test]
        public void AFlatWithNoSignalIsDroppedWithAWarning() {
            List<string> warnings = new List<string>();

            ushort[] result = FrameCalibrator.Apply(Pixels(1000), null, null, Pixels(0), null, warnings, out double flatNorm);

            Assert.That(result, Is.EqualTo(Pixels(2000)));
            Assert.That(flatNorm, Is.EqualTo(1.0));
            Assert.That(warnings, Is.EqualTo(new[] { "Master flat has no signal after bias subtraction; flat correction skipped." }));
        }

        // A dead flat pixel would divide by nothing, so it is treated as unity and counted in a warning instead of going white.
        [Test]
        public void ADeadFlatPixelIsTreatedAsUnityAndCounted() {
            ushort[] flat = Filled(32, 2000);
            flat[1] = 0;
            List<string> warnings = new List<string>();

            ushort[] result = FrameCalibrator.Apply(Filled(32, 1000), null, null, flat, null, warnings, out double _);

            Assert.That(result[1], Is.EqualTo(2000));
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.StartWith("1 flat pixel(s) below "));
        }

        // The output is 16-bit, so a bright pixel saturates and an over-subtracted one floors at zero rather than wrapping.
        [TestCase(65000, 0, 65535)]
        [TestCase(100, 2000, 0)]
        public void TheResultIsClampedToSixteenBits(int light, int dark, int expected) {
            Assert.That(Calibrate(Pixels(light), dark: Pixels(dark)), Is.EqualTo(Pixels(expected)));
        }

        // The clamp truncates, since Apply has already rounded. Below zero and above 16 bits pin to the ends.
        [TestCase(-1, 0)]
        [TestCase(1234.7, 1234)]
        [TestCase(70000, 65535)]
        public void TheClampTruncatesAndPinsToTheEnds(double value, int expected) {
            Assert.That(FrameCalibrator.ClampToUShort(value), Is.EqualTo(expected));
        }

        ///////////// Stem /////////////

        // The output is named from the light's stem, so a compressed .fits.fz gets the same name as a plain .fits.
        [TestCase("LIGHT_0001.fits", "LIGHT_0001")]
        [TestCase("LIGHT_0001.fits.fz", "LIGHT_0001")]
        [TestCase("LIGHT_0001.xisf", "LIGHT_0001")]
        [TestCase(@"frames\LIGHT_0001.FITS", "LIGHT_0001")]
        public void TheStemDropsTheFolderTheExtensionAndACompressionSuffix(string path, string expected) {
            Assert.That(FrameCalibrator.Stem(path), Is.EqualTo(expected));
        }

        ///////////// AddCalibrationHeaders /////////////

        // The calibrated file records which masters made it, so a frame can be traced back to its plan.
        [Test]
        public void TheHeadersNameThePlanAndTheMastersItUsed() {
            ImageMetaData metadata = new ImageMetaData();
            CalibrationPlan plan = new CalibrationPlan("bias_flat", DarkScalingRatio: null, ShouldKeepRaw: true,
                Warnings: Array.Empty<string>(), Bias: new MasterRef(12, 1, null), Dark: null, Flat: new MasterRef(7, 2, null));

            FrameCalibrator.AddCalibrationHeaders(metadata, plan, hasFlat: true, flatNorm: 2000);

            List<string> keys = metadata.GenericHeaders.Select(header => header.Key).ToList();
            Assert.That(keys, Is.SupersetOf(new[] { "CREATOR", "CALSTRAT", "MBIAS", "MFLAT", "FLATNORM", "PEDESTAL", "ASTRDATE" }));
            Assert.That(keys, Does.Not.Contain("MDARK"));
            Assert.That(keys, Does.Not.Contain("DARKSCAL"));
            Assert.That(metadata.GenericHeaders.OfType<IntMetaDataHeader>().Single(header => header.Key == "MBIAS").Value,
                Is.EqualTo(12));
        }

        // A dark plan records the dark and its scaling ratio, and nothing about a flat it didn't have.
        [Test]
        public void ADarkPlanRecordsTheDarkAndItsRatio() {
            ImageMetaData metadata = new ImageMetaData();
            CalibrationPlan plan = new CalibrationPlan("scaled_dark", DarkScalingRatio: 0.5, ShouldKeepRaw: true,
                Warnings: Array.Empty<string>(), Bias: new MasterRef(12, 1, null), Dark: new MasterRef(3, 1, null), Flat: null);

            FrameCalibrator.AddCalibrationHeaders(metadata, plan, hasFlat: false, flatNorm: 1.0);

            List<string> keys = metadata.GenericHeaders.Select(header => header.Key).ToList();
            Assert.That(keys, Is.SupersetOf(new[] { "MBIAS", "MDARK", "DARKSCAL" }));
            Assert.That(keys, Does.Not.Contain("MFLAT"));
            Assert.That(keys, Does.Not.Contain("FLATNORM"));
        }

        ///////////// RemoveStaleOutput /////////////

        // A retried report leaves last time's calibrated file behind, and N.I.N.A. would save beside it under a numbered
        // name. The stale outputs go, whatever their format, and the light itself is never touched.
        [Test]
        public void StaleOutputsAreRemovedAndTheLightLeftAlone() {
            string folder = Path.Combine(Path.GetTempPath(), "Astraeus.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                foreach (string name in new[] { "LIGHT_0001_cal.fits", "LIGHT_0001_cal.xisf", "LIGHT_0001.fits" })
                    File.WriteAllText(Path.Combine(folder, name), "");

                FrameCalibrator.RemoveStaleOutput(folder, "LIGHT_0001_cal");

                Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName), Is.EqualTo(new[] { "LIGHT_0001.fits" }));
            } finally {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}