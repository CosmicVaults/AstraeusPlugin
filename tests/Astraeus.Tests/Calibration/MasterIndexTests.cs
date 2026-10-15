using System;
using System.IO;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Calibration {
    [TestFixture]
    public class MasterIndexTests {
        private string _mastersFolder = "";

        [SetUp]
        public void CreateMastersFolder() {
            _mastersFolder = Path.Combine(Path.GetTempPath(), "Astraeus.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_mastersFolder);
        }

        [TearDown]
        public void DeleteMastersFolder() {
            try {
                Directory.Delete(_mastersFolder, recursive: true);
            } catch (IOException) {
                // A file a failed test left open shouldn't fail the next test too.
            }
        }

        ///////////// ResolveMastersFolder /////////////

        // Masters go to the output folder when the observer set one, so the scan folder stays input only.
        [Test]
        public void TheOutputFolderWinsWhenBothAreSet() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetCalibrationScanFolder(@"D:\Calibration\Scan");
            nina.Settings.SetCalibrationMastersOutputFolder(@"D:\Calibration\Masters");

            Assert.That(MasterIndex.ResolveMastersFolder(nina.Settings), Is.EqualTo(@"D:\Calibration\Masters"));
        }

        // Without an output folder the scan folder is used, so an observer with one folder still gets masters.
        [Test]
        public void TheScanFolderIsUsedWhenNoOutputFolderIsSet() {
            FakeNina nina = new FakeNina();
            nina.Settings.SetCalibrationScanFolder(@"D:\Calibration\Scan");

            Assert.That(MasterIndex.ResolveMastersFolder(nina.Settings), Is.EqualTo(@"D:\Calibration\Scan"));
        }

        // With neither set there is nowhere to write, and the caller must not invent a folder.
        [Test]
        public void NoFolderIsNullWhenNeitherIsSet() {
            FakeNina nina = new FakeNina();

            Assert.That(MasterIndex.ResolveMastersFolder(nina.Settings), Is.Null);
        }

        ///////////// Record and FindLocalPath /////////////

        // The whole point of the index: a master this PC uploaded is found again by the id and revision the server names.
        [Test]
        public void ARecordedMasterIsFoundByItsIdAndRevision() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string masterPath = WriteMaster("master-dark.fits");
            index.Record(masterFrameId: 12, revision: 3, checksumSha256: "abc123", masterPath);

            string? found = index.FindLocalPath(new MasterRef(12, 3, "abc123"), out string? missingReason);

            Assert.That(found, Is.EqualTo(masterPath));
            Assert.That(missingReason, Is.Null);
        }

        // A master the server names that this PC never generated is reported, not guessed at, since there is no download.
        [Test]
        public void AMasterNeverRecordedIsMissingBecauseItWasNotGeneratedHere() {
            MasterIndex index = new MasterIndex(_mastersFolder);

            string? found = index.FindLocalPath(new MasterRef(12, 3, null), out string? missingReason);

            Assert.That(found, Is.Null);
            Assert.That(missingReason, Is.EqualTo("it was not generated on this PC"));
        }

        // A newer revision on the server means the file here is stale, so the old revision must not be handed out.
        [Test]
        public void AMasterAtAnotherRevisionIsNotFound() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            index.Record(12, 3, null, WriteMaster("master-dark.fits"));

            string? found = index.FindLocalPath(new MasterRef(12, 4, null), out string? missingReason);

            Assert.That(found, Is.Null);
            Assert.That(missingReason, Is.EqualTo("it was not generated on this PC"));
        }

        // Re-recording an id replaces the entry and hands back the old file, so the generator can delete it.
        [Test]
        public void RecordingTheSameIdAgainReplacesTheEntryAndReturnsTheOldFile() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string oldPath = WriteMaster("master-dark-r3.fits");
            string newPath = WriteMaster("master-dark-r4.fits");

            string? firstReplaced = index.Record(12, 3, null, oldPath);
            string? secondReplaced = index.Record(12, 4, null, newPath);

            Assert.That(firstReplaced, Is.Null);
            Assert.That(secondReplaced, Is.EqualTo(oldPath));
            Assert.That(index.FindLocalPath(new MasterRef(12, 3, null), out string? _), Is.Null);
            Assert.That(index.FindLocalPath(new MasterRef(12, 4, null), out string? _), Is.EqualTo(newPath));
        }

        // The index lives on disk, so a master recorded before a restart is still found after one.
        [Test]
        public void TheIndexSurvivesANewInstanceOverTheSameFolder() {
            string masterPath = WriteMaster("master-dark.fits");
            new MasterIndex(_mastersFolder).Record(12, 3, null, masterPath);

            string? found = new MasterIndex(_mastersFolder).FindLocalPath(new MasterRef(12, 3, null), out string? _);

            Assert.That(found, Is.EqualTo(masterPath));
        }

        // A master the observer deleted can't be applied. The reason goes to the server, so it names the file but never the folder.
        [Test]
        public void ADeletedFileIsMissingWithoutNamingTheFolder() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string masterPath = WriteMaster("master-dark.fits");
            index.Record(12, 3, null, masterPath);
            File.Delete(masterPath);

            string? found = index.FindLocalPath(new MasterRef(12, 3, null), out string? missingReason);

            Assert.That(found, Is.Null);
            Assert.That(missingReason, Is.EqualTo("its file master-dark.fits is no longer in the masters folder"));
            Assert.That(missingReason, Does.Not.Contain(_mastersFolder));
        }

        // A file edited since upload is no longer the master the server knows, so it is refused rather than applied blind.
        [Test]
        public void AFileWhoseLengthChangedIsMissingAsChanged() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string masterPath = WriteMaster("master-dark.fits");
            index.Record(12, 3, null, masterPath);
            File.WriteAllBytes(masterPath, new byte[32]);

            string? found = index.FindLocalPath(new MasterRef(12, 3, null), out string? missingReason);

            Assert.That(found, Is.Null);
            Assert.That(missingReason, Is.EqualTo("its file master-dark.fits has changed since it was uploaded"));
        }

        // Same length is not proof: a rewrite that keeps the size still moves the write time, and that counts too.
        [Test]
        public void AFileWhoseWriteTimeChangedIsMissingAsChanged() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string masterPath = WriteMaster("master-dark.fits");
            index.Record(12, 3, null, masterPath);
            File.SetLastWriteTimeUtc(masterPath, File.GetLastWriteTimeUtc(masterPath).AddMinutes(1));

            string? found = index.FindLocalPath(new MasterRef(12, 3, null), out string? missingReason);

            Assert.That(found, Is.Null);
            Assert.That(missingReason, Does.Contain("has changed since it was uploaded"));
        }

        // When both sides have a checksum they must agree, or the server's plan was made for a different file.
        [Test]
        public void AChecksumThatDisagreesWithTheServersIsMissingAsNotMatching() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            index.Record(12, 3, "abc123", WriteMaster("master-dark.fits"));

            string? found = index.FindLocalPath(new MasterRef(12, 3, "def456"), out string? missingReason);

            Assert.That(found, Is.Null);
            Assert.That(missingReason, Is.EqualTo("its file master-dark.fits does not match the server's copy"));
        }

        // Hex digests differ only in case between tools, so case must not make a matching master look wrong.
        [Test]
        public void ChecksumsAreComparedIgnoringCase() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string masterPath = WriteMaster("master-dark.fits");
            index.Record(12, 3, "ABC123", masterPath);

            Assert.That(index.FindLocalPath(new MasterRef(12, 3, "abc123"), out string? _), Is.EqualTo(masterPath));
        }

        // A checksum on one side only can't be checked, and a master from an older server or upload is still usable.
        [TestCase("abc123", null)]
        [TestCase(null, "abc123")]
        public void AChecksumOnOneSideOnlyIsNotChecked(string? recorded, string? named) {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string masterPath = WriteMaster("master-dark.fits");
            index.Record(12, 3, recorded, masterPath);

            Assert.That(index.FindLocalPath(new MasterRef(12, 3, named), out string? _), Is.EqualTo(masterPath));
        }

        // Masters sit in subfolders by type, and the index keeps the path relative to the masters folder, so it must round-trip.
        [Test]
        public void AMasterInASubfolderIsFound() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            string masterPath = WriteMaster(Path.Combine("darks", "master-dark.fits"));
            index.Record(12, 3, null, masterPath);

            Assert.That(index.FindLocalPath(new MasterRef(12, 3, null), out string? _), Is.EqualTo(masterPath));
        }

        // The index is written to a temporary file and swapped in, so a crash mid-write can't leave a half-written index.
        [Test]
        public void TheIndexIsWrittenInPlaceWithNoTemporaryFileLeftBehind() {
            MasterIndex index = new MasterIndex(_mastersFolder);
            index.Record(12, 3, null, WriteMaster("master-dark.fits"));
            index.Record(13, 1, null, WriteMaster("master-flat.fits"));

            Assert.That(File.Exists(Path.Combine(_mastersFolder, "astraeus-master-index.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(_mastersFolder, "astraeus-master-index.json.tmp")), Is.False);
        }

        private string WriteMaster(string relativePath) {
            string path = Path.Combine(_mastersFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[16]);
            return path;
        }
    }
}