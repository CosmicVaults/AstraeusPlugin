using System.IO;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Calibration {
    /// <summary>
    /// Ensure the master reference frame is parsed correctly and the look-up worked
    /// </summary>
    [TestFixture]
    public class MasterRefTests {
        // A full entry reads all three parts, since a wrong id would calibrate a light with the wrong master.
        [Test]
        public void AMasterReadsItsIdRevisionAndChecksum() {
            JObject master = JObject.Parse("""{"id": 4, "revision": 2, "checksum_sha256": "abc123"}""");

            MasterRef? parsed = MasterRef.Parse(master);

            Assert.That(parsed, Is.EqualTo(new MasterRef(4, 2, "abc123")));
        }

        // Revision and checksum are optional, so an older server or a master that was never checksummed is still
        // usable.
        [Test]
        public void AMissingRevisionIsZeroAndAMissingChecksumIsNull() {
            JObject master = JObject.Parse("""{"id": 4}""");

            MasterRef? parsed = MasterRef.Parse(master);

            Assert.That(parsed, Is.EqualTo(new MasterRef(4, 0, null)));
        }

        // An empty checksum must read as none, or the verification step would compare against an empty string and
        // refuse every master.
        [Test]
        public void AnEmptyChecksumIsNull() {
            JObject master = JObject.Parse("""{"id": 4, "checksum_sha256": ""}""");

            Assert.That(MasterRef.Parse(master)?.ChecksumSha256, Is.Null);
        }

        // An entry with no id names no master, so that role is skipped rather than invented.
        [Test]
        public void AMasterWithoutAnIdIsNull() {
            JObject master = JObject.Parse("""{"revision": 2}""");

            Assert.That(MasterRef.Parse(master), Is.Null);
        }

        // A role the plan leaves out, or fills with a bare value, reads as no master, which CalibrationPlan relies
        // on.
        [Test]
        public void AMissingOrNonObjectMasterIsNull() {
            Assert.That(MasterRef.Parse(null), Is.Null);
            Assert.That(MasterRef.Parse(new JValue(4)), Is.Null);
        }

        // The id goes through ResponseFields, so a wrong type refuses the whole reply rather than skipping the
        // master. A server bug putting strings where ids belong stops the registration instead of calibrating without
        // the master.
        [Test]
        public void AnIdOfTheWrongTypeRefusesTheReply() {
            JObject master = JObject.Parse("""{"id": "4"}""");

            Assert.That(() => MasterRef.Parse(master), Throws.TypeOf<InvalidDataException>());
        }
    }
}