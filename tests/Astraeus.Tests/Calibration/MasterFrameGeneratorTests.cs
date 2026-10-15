using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Calibration {
    [TestFixture]
    public class MasterFrameGeneratorTests {
        // A frame as the scan describes it: one camera at gain 100, offset 10, bin 1, 4096 square unless a test says otherwise.
        private static MasterFrameDescriptor Frame(MasterFrameType type, double exposure = double.NaN,
            double temperature = double.NaN, string? filter = null, string camera = "Cam", int width = 4096) =>
            new MasterFrameDescriptor {
                FilePath = $"{type}.fits",
                FrameType = type,
                CameraName = camera,
                Gain = 100,
                Offset = 10,
                Width = width,
                Height = 4096,
                ExposureTimeSeconds = exposure,
                TemperatureCelsius = temperature,
                FilterName = filter,
            };

        private static MasterFrameGenerator.FrameGroup GroupOf(MasterFrameDescriptor frame) =>
            new MasterFrameGenerator.FrameGroup(frame.FrameType, frame, new List<MasterFrameDescriptor> { frame });

        private static int[] GroupSizes(params MasterFrameDescriptor[] frames) =>
            MasterFrameGenerator.BuildGroups(frames.ToList()).Select(group => group.Frames.Count).ToArray();

        ///////////// Sorting the folder /////////////

        // IMAGETYP is free text across capture programs, so the sort goes by the words in it, with a dark-flat recognised
        // before the plain flat and dark rules can claim it. Anything else, lights included, is left out.
        [TestCase("BIAS", MasterFrameType.Bias)]
        [TestCase("Bias Frame", MasterFrameType.Bias)]
        [TestCase("OFFSET", MasterFrameType.Bias)]
        [TestCase("DARK", MasterFrameType.Dark)]
        [TestCase(" dark ", MasterFrameType.Dark)]
        [TestCase("FLAT", MasterFrameType.Flat)]
        [TestCase("Flat Field", MasterFrameType.Flat)]
        [TestCase("FLATDARK", MasterFrameType.DarkFlat)]
        [TestCase("DARKFLAT", MasterFrameType.DarkFlat)]
        [TestCase("DARK_FLAT", MasterFrameType.DarkFlat)]
        [TestCase("LIGHT", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void TheImageTypeHeaderSortsAFrameByItsWords(string? imageType, MasterFrameType? expected) {
            Assert.That(MasterFrameGenerator.ClassifyFrameType(imageType), Is.EqualTo(expected));
        }

        // Only FITS and XISF are raw calibration frames, and a file the generator named itself is skipped so a re-run
        // doesn't stack last time's masters into this time's.
        [TestCase("dark_001.fits", true)]
        [TestCase("dark_001.FIT", true)]
        [TestCase("dark_001.xisf", true)]
        [TestCase("dark_001.fits.fz", true)]
        [TestCase("dark_001.tif", false)]
        [TestCase("notes.txt", false)]
        [TestCase("master_dark.fits", false)]
        [TestCase("MASTER_bias.xisf", false)]
        public void ARawCalibrationFileIsFitsOrXisfAndNotAMaster(string name, bool isRaw) {
            Assert.That(MasterFrameGenerator.IsRawCalibrationFile(name), Is.EqualTo(isRaw));
        }

        ///////////// Grouping /////////////

        // Darks stack only with darks of the same exposure and temperature, since those set the dark current. The
        // temperature is matched to the whole degree, so -10 and -10.4 share a master.
        [Test]
        public void DarksSplitByExposureAndTemperatureAndJoinOtherwise() {
            int[] sizes = GroupSizes(
                Frame(MasterFrameType.Dark, exposure: 300, temperature: -10),
                Frame(MasterFrameType.Dark, exposure: 300, temperature: -10.4),
                Frame(MasterFrameType.Dark, exposure: 60, temperature: -10),
                Frame(MasterFrameType.Dark, exposure: 300, temperature: -20));

            Assert.That(sizes, Is.EqualTo(new[] { 2, 1, 1 }));
        }

        // Flats are per filter and don't care about temperature, so a cooler flat still serves the same filter.
        [Test]
        public void FlatsSplitByFilterAndNotByTemperature() {
            int[] sizes = GroupSizes(
                Frame(MasterFrameType.Flat, exposure: 2.5, temperature: -10, filter: "L"),
                Frame(MasterFrameType.Flat, exposure: 2.5, temperature: -20, filter: "L"),
                Frame(MasterFrameType.Flat, exposure: 2.5, temperature: -10, filter: "Ha"));

            Assert.That(sizes, Is.EqualTo(new[] { 2, 1 }));
        }

        // A bias has no exposure or filter worth the name, so frames differing only in those stack together.
        [Test]
        public void BiasFramesIgnoreExposureAndFilter() {
            int[] sizes = GroupSizes(
                Frame(MasterFrameType.Bias, exposure: 0.001, temperature: -10, filter: "L"),
                Frame(MasterFrameType.Bias, temperature: -10));

            Assert.That(sizes, Is.EqualTo(new[] { 2 }));
        }

        // Frames of different sizes can't be added pixel for pixel, so they never share a stack even when all else matches.
        [Test]
        public void FramesOfDifferentSizesNeverShareAStack() {
            int[] sizes = GroupSizes(
                Frame(MasterFrameType.Dark, exposure: 300, temperature: -10, width: 4096),
                Frame(MasterFrameType.Dark, exposure: 300, temperature: -10, width: 4095));

            Assert.That(sizes, Is.EqualTo(new[] { 1, 1 }));
        }

        // The camera name is matched regardless of case, since drivers are not consistent about it between sessions.
        [Test]
        public void TheCameraNameIsMatchedRegardlessOfCase() {
            int[] sizes = GroupSizes(
                Frame(MasterFrameType.Dark, exposure: 300, temperature: -10, camera: "Cam"),
                Frame(MasterFrameType.Dark, exposure: 300, temperature: -10, camera: "CAM"));

            Assert.That(sizes, Is.EqualTo(new[] { 2 }));
        }

        // A dark and a dark-flat with the same settings are still different masters, so the type is part of the key.
        [Test]
        public void FrameTypesNeverMix() {
            int[] sizes = GroupSizes(
                Frame(MasterFrameType.Dark, exposure: 2.5, temperature: -10),
                Frame(MasterFrameType.DarkFlat, exposure: 2.5, temperature: -10));

            Assert.That(sizes, Is.EqualTo(new[] { 1, 1 }));
        }

        ///////////// Stacking /////////////

        // One pixel through the stack, each frame contributing the given count.
        private static int Stacked(params int[] countPerFrame) {
            List<ushort[]> frames = countPerFrame.Select(count => new[] { (ushort)count }).ToList();
            return MasterFrameGenerator.SigmaClipMeanStack(frames, length: 1, CancellationToken.None)[0];
        }

        // Two frames can't show which one is the outlier, so they are averaged as they are.
        [Test]
        public void FewerThanThreeFramesAreAveragedWithoutClipping() {
            Assert.That(Stacked(100, 300), Is.EqualTo(200));
        }

        // A hot pixel or a satellite in one frame of a dozen is more than three sigma out, so it is dropped and the master
        // keeps the quiet value.
        [Test]
        public void AnOutlierInOneFrameOfTwelveIsClipped() {
            int[] counts = Enumerable.Repeat(100, 11).Append(5000).ToArray();

            Assert.That(Stacked(counts), Is.EqualTo(100));
        }

        // The mean is rounded to the nearest count, not truncated, so a master isn't biased low by half a count everywhere.
        [TestCase(new[] { 100, 100, 101 }, 100)]
        [TestCase(new[] { 100, 101, 101 }, 101)]
        public void TheMeanIsRoundedToTheNearestCount(int[] counts, int expected) {
            Assert.That(Stacked(counts), Is.EqualTo(expected));
        }

        // Every frame of a group is held in memory at once, so the budget caps how many of a given size are stacked.
        [TestCase(4096, 4096, 64)]
        [TestCase(8192, 8192, 16)]
        public void TheMemoryBudgetCapsTheFramesStackedBySize(int width, int height, int maxFrames) {
            Assert.That(MasterFrameGenerator.MaxFramesWithinMemoryBudget(width, height), Is.EqualTo(maxFrames));
        }

        ///////////// Naming /////////////

        // The master's file name carries what matched it: gain and offset, binning, exposure and temperature for darks,
        // the filter for flats. Characters a file system or the server might choke on become underscores.
        [TestCase(MasterFrameType.Dark, "master_dark_g100_o10_bin1x1_2.5s_-10C")]
        [TestCase(MasterFrameType.Bias, "master_bias_g100_o10_bin1x1_-10C")]
        [TestCase(MasterFrameType.Flat, "master_flat_Ha_3nm_bin1x1_2.5s")]
        [TestCase(MasterFrameType.DarkFlat, "master_darkflat_g100_o10_bin1x1_2.5s_-10C")]
        public void TheMasterIsNamedByWhatMatchesIt(MasterFrameType type, string expected) {
            MasterFrameDescriptor frame = Frame(type, exposure: 2.5, temperature: -10.4, filter: "Ha 3nm");

            Assert.That(MasterFrameGenerator.BuildMasterFileName(GroupOf(frame)), Is.EqualTo(expected));
        }

        // A header the camera didn't write shows as NA rather than a made-up number.
        [Test]
        public void AMissingExposureOrTemperatureIsNamedNA() {
            MasterFrameDescriptor frame = Frame(MasterFrameType.Dark);

            Assert.That(MasterFrameGenerator.BuildMasterFileName(GroupOf(frame)),
                Is.EqualTo("master_dark_g100_o10_bin1x1_NAs_NAC"));
        }

        // The progress line says which master is being stacked, in the terms the observer thinks in.
        [TestCase(MasterFrameType.Dark, "dark (2.5s, -10C, gain 100)")]
        [TestCase(MasterFrameType.Bias, "bias (gain 100, 4096x4096, -10C)")]
        [TestCase(MasterFrameType.Flat, "flat (filter Ha 3nm, 4096x4096, 2.5s)")]
        [TestCase(MasterFrameType.DarkFlat, "dark-flat (2.5s, -10C, gain 100)")]
        public void TheProgressLineDescribesTheGroup(MasterFrameType type, string expected) {
            MasterFrameDescriptor frame = Frame(type, exposure: 2.5, temperature: -10.4, filter: "Ha 3nm");

            Assert.That(MasterFrameGenerator.Describe(GroupOf(frame)), Is.EqualTo(expected));
        }
    }
}