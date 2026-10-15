using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Imaging;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Imaging {
    [TestFixture]
    public class FitsHeaderPatcherTests {
        private const int CardSize = 80;
        private const int BlockSize = 2880;

        private static readonly FrameWcs Wcs = new FrameWcs(
            CrVal1: 83.822, CrVal2: -5.391, CrPix1: 2048, CrPix2: 1024,
            Cd11: -0.0005, Cd12: 0, Cd21: 0, Cd22: 0.0005);

        private string _folder = "";
        private readonly List<string> _log = new();
        private readonly List<string> _warnings = new();

        [SetUp]
        public void CreateFolder() {
            _folder = Path.Combine(Path.GetTempPath(), "Astraeus.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _log.Clear();
            _warnings.Clear();
        }

        [TearDown]
        public void DeleteFolder() {
            try {
                Directory.Delete(_folder, recursive: true);
            } catch (IOException) {
                // A file a failed test left open shouldn't fail the next test too.
            }
        }

        private FitsHeaderPatcher Patcher() => new FitsHeaderPatcher(_log.Add, _warnings.Add);

        // A header block of these cards, space-padded to 2880 bytes as the standard requires.
        private static byte[] HeaderBlock(params string[] cards) {
            string text = string.Concat(cards.Select(card => card.PadRight(CardSize)));
            return Encoding.ASCII.GetBytes(text.PadRight(BlockSize));
        }

        // A data block with a pattern a test can tell from spaces and zeros.
        private static byte[] DataBlock() => Enumerable.Range(0, BlockSize).Select(i => (byte)(i % 251)).ToArray();

        // A minimal FITS file: three header cards, END, and one block of data.
        private string WriteFits(string name) {
            string path = Path.Combine(_folder, name);
            byte[] header = HeaderBlock("SIMPLE  =                    T", "BITPIX  =                   16",
                "NAXIS   =                    0", "END");
            File.WriteAllBytes(path, header.Concat(DataBlock()).ToArray());
            return path;
        }

        // The header cards of a file, up to and including END.
        private static List<string> CardsOf(byte[] bytes) {
            List<string> cards = new List<string>();
            for (int offset = 0; offset + CardSize <= bytes.Length; offset += CardSize) {
                string card = Encoding.ASCII.GetString(bytes, offset, CardSize);
                cards.Add(card);
                if (card.StartsWith("END ", StringComparison.Ordinal)) break;
            }
            return cards;
        }

        private static string KeywordOf(string card) => card[..8].TrimEnd();

        ///////////// IsPatchable /////////////

        // Only a plain FITS file can take the patch. XISF has its own header, and a compressed .fz can't be edited in place.
        [TestCase("frame.fits", true)]
        [TestCase("frame.FIT", true)]
        [TestCase("frame.fts", true)]
        [TestCase("frame.xisf", false)]
        [TestCase("frame.fits.fz", false)]
        [TestCase("frame.tif", false)]
        public void OnlyAPlainFitsFileIsPatchable(string name, bool isPatchable) {
            Assert.That(FitsHeaderPatcher.IsPatchable(name), Is.EqualTo(isPatchable));
        }

        ///////////// FindEndCardIndex /////////////

        // END is found by its keyword alone, so a keyword that merely starts with END, like ENDTIME, doesn't end the header
        // early and lose every card after it.
        [Test]
        public void TheEndCardIsFoundByItsKeywordAndNotByAKeywordStartingWithEnd() {
            byte[] header = HeaderBlock("SIMPLE  =                    T", "BITPIX  =                   16",
                "ENDTIME = '01:00'", "END");

            Assert.That(FitsHeaderPatcher.FindEndCardIndex(header), Is.EqualTo(3));
        }

        // A header with no END can't be patched safely, and the caller needs a clear no rather than a guess.
        [Test]
        public void AHeaderWithoutAnEndCardReportsMinusOne() {
            byte[] header = HeaderBlock("SIMPLE  =                    T", "BITPIX  =                   16");

            Assert.That(FitsHeaderPatcher.FindEndCardIndex(header), Is.EqualTo(-1));
        }

        ///////////// BuildWcsCards /////////////

        // The ten WCS cards are fixed-format FITS: 80 columns, the keyword in the first eight, a text value quoted and a
        // number right-aligned to column 30 in the invariant culture, so a solver downstream reads them.
        [Test]
        public void TheWcsCardsAreEightyColumnsInTheFixedFormat() {
            string[] cards = FitsHeaderPatcher.BuildWcsCards(Wcs);

            Assert.That(cards, Has.Length.EqualTo(10));
            Assert.That(cards, Has.All.Length.EqualTo(CardSize));
            Assert.That(cards.Select(KeywordOf), Is.EqualTo(new[] {
                "CTYPE1", "CTYPE2", "CRVAL1", "CRVAL2", "CRPIX1", "CRPIX2", "CD1_1", "CD1_2", "CD2_1", "CD2_2",
            }));
            Assert.That(cards[0], Is.EqualTo("CTYPE1  = 'RA---TAN'           / Coordinate projection type".PadRight(CardSize)));
            Assert.That(cards[2],
                Is.EqualTo(("CRVAL1  = " + "83.822".PadLeft(20) + " / [deg] RA at reference pixel").PadRight(CardSize)));
            Assert.That(cards[6],
                Is.EqualTo(("CD1_1   = " + "-0.0005".PadLeft(20) + " / [deg/px] WCS transform matrix").PadRight(CardSize)));
        }

        ///////////// InjectWcsAsync /////////////

        // The patch adds the ten cards before END and leaves the data exactly where it was, so the frame is still a valid
        // FITS file and a solver downstream reads the plate solve from it.
        [Test]
        public async Task TheWcsIsInjectedBeforeEndAndTheDataLeftAlone() {
            string path = WriteFits("frame.fits");

            await Patcher().InjectWcsAsync(path, Wcs, CancellationToken.None);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.That(CardsOf(bytes).Select(KeywordOf), Is.EqualTo(new[] {
                "SIMPLE", "BITPIX", "NAXIS", "CTYPE1", "CTYPE2", "CRVAL1", "CRVAL2", "CRPIX1", "CRPIX2",
                "CD1_1", "CD1_2", "CD2_1", "CD2_2", "END",
            }));
            Assert.That(bytes.Length, Is.EqualTo(2 * BlockSize), "the header still fits one block");
            Assert.That(bytes.Skip(BlockSize), Is.EqualTo(DataBlock()));
            Assert.That(_log, Is.EqualTo(new[] { "WCS injected into FITS header: frame.fits" }));
            Assert.That(_warnings, Is.Empty);
        }

        // Patching the same frame twice, as a re-solve does, replaces the WCS cards rather than stacking a second set.
        [Test]
        public async Task PatchingTwiceLeavesOneSetOfWcsCards() {
            string path = WriteFits("frame.fits");
            FitsHeaderPatcher patcher = Patcher();

            await patcher.InjectWcsAsync(path, Wcs, CancellationToken.None);
            await patcher.InjectWcsAsync(path, Wcs with { CrVal1 = 84.0 }, CancellationToken.None);

            List<string> cards = CardsOf(File.ReadAllBytes(path));
            Assert.That(cards, Has.Count.EqualTo(14));
            Assert.That(cards.Single(card => KeywordOf(card) == "CRVAL1"), Does.Contain("84"));
        }

        // A file with no END card is not one we understand, so it is left byte for byte as it was, with a warning and no
        // temporary file beside it.
        [Test]
        public async Task AFileWithoutAnEndCardIsLeftUntouched() {
            string path = Path.Combine(_folder, "frame.fits");
            byte[] original = HeaderBlock("SIMPLE  =                    T").Concat(DataBlock()).ToArray();
            File.WriteAllBytes(path, original);

            await Patcher().InjectWcsAsync(path, Wcs, CancellationToken.None);

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
            Assert.That(_warnings, Is.EqualTo(new[] { "WCS injection skipped: FITS END card not found." }));
            Assert.That(Directory.GetFiles(_folder).Select(Path.GetFileName), Is.EqualTo(new[] { "frame.fits" }));
        }

        // An XISF frame is skipped without a word, since the choice of format is the observer's and not a fault.
        [Test]
        public async Task AnUnpatchableFileIsSkippedSilently() {
            string path = Path.Combine(_folder, "frame.xisf");
            File.WriteAllBytes(path, DataBlock());

            await Patcher().InjectWcsAsync(path, Wcs, CancellationToken.None);

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(DataBlock()));
            Assert.That(_log, Is.Empty);
            Assert.That(_warnings, Is.Empty);
        }
    }
}