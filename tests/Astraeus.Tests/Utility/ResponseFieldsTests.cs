using System.IO;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Utility {
    /// <summary>
    /// Ensures feilds in a HTTP response are parsed correctly.
    /// For example a reply from GetAutopilotNextAsync will return a set of named numbers with some missing or null values
    /// This ensures those values are parsed correctly. 
    /// </summary>
    
    [TestFixture]
    public class ResponseFieldsTests {
        ///////////// Presence /////////////

        // A field a reply leaves out is null, so callers can tell missing from zero and apply their own defaults.
        [Test]
        public void AMissingFieldReadsAsNull() {
            JObject reply = JObject.Parse("{}");

            Assert.That(ResponseFields.Int(reply, "gain"), Is.Null);
        }

        // The server sends null for an unknown value, and that reads as null rather than as a wrong type.
        [Test]
        public void ANullFieldReadsAsNull() {
            JObject reply = JObject.Parse("""{"gain": null}""");

            Assert.That(ResponseFields.Int(reply, "gain"), Is.Null);
        }

        // A reply with no body at all reads as every field missing instead of throwing.
        [Test]
        public void ANullParentReadsAsNull() {
            Assert.That(ResponseFields.Int(null, "gain"), Is.Null);
        }

        // A reply that is an array or a scalar has no named fields, and that reads as missing rather than as a wrong
        // type.
        [Test]
        public void AParentThatIsNotAnObjectReadsAsNull() {
            JArray reply = JArray.Parse("[1, 2]");

            Assert.That(ResponseFields.Int(reply, "gain"), Is.Null);
        }

        ///////////// FiniteDouble /////////////

        // Whole and fractional JSON numbers both read as a double, so an RA of 83 and one of 83.822 both work.
        [TestCase("12.5", 12.5)]
        [TestCase("3", 3.0)]
        public void ANumberReadsAsADouble(string literal, double expected) {
            JObject reply = JObject.Parse($$"""{"ra": {{literal}}}""");

            Assert.That(ResponseFields.FiniteDouble(reply, "ra"), Is.EqualTo(expected));
        }

        // The server has sent NaN and Infinity before, and Newtonsoft reads them as numbers. Either one slewing a
        // mount would be a disaster, so they refuse the whole reply.
        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("-Infinity")]
        public void NaNAndInfinityAreRefused(string literal) {
            JObject reply = JObject.Parse($$"""{"ra": {{literal}}}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.FiniteDouble(reply, "ra"));
        }

        // A string or bool where a number belongs refuses the reply instead of being cast to 0 or NaN.
        [TestCase("\"12.5\"")]
        [TestCase("true")]
        public void AnythingButANumberIsNotADouble(string literal) {
            JObject reply = JObject.Parse($$"""{"ra": {{literal}}}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.FiniteDouble(reply, "ra"));
        }

        ///////////// Int, Long and Short /////////////

        // A whole number, positive or negative, reads as an int.
        [TestCase("42", 42)]
        [TestCase("-7", -7)]
        public void AWholeNumberReadsAsAnInt(string literal, int expected) {
            JObject reply = JObject.Parse($$"""{"gain": {{literal}}}""");

            Assert.That(ResponseFields.Int(reply, "gain"), Is.EqualTo(expected));
        }

        // A float, a quoted number or a bool where an int belongs refuses the reply. Newtonsoft reads 1.0 as a float
        // and the server sends whole numbers without a point, so 1.0 for a gain is a server bug worth stopping on.
        [TestCase("1.0")]
        [TestCase("\"42\"")]
        [TestCase("true")]
        public void AnythingButAWholeNumberIsNotAnInt(string literal) {
            JObject reply = JObject.Parse($$"""{"gain": {{literal}}}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.Int(reply, "gain"));
        }

        // A number that fits a long but not an int is refused rather than wrapped into a negative gain.
        [Test]
        public void ANumberPastAnIntIsRefused() {
            JObject reply = JObject.Parse("""{"gain": 3000000000}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.Int(reply, "gain"));
        }

        // Byte counts from the storage check overflow an int, so Long must read them in full.
        [Test]
        public void AWholeNumberReadsAsALong() {
            JObject reply = JObject.Parse("""{"used_bytes": 5000000000}""");

            Assert.That(ResponseFields.Long(reply, "used_bytes"), Is.EqualTo(5000000000L));
        }

        // Newtonsoft holds a number past long.MaxValue as a BigInteger, and that must be refused rather than cast to
        // nonsense.
        [Test]
        public void ANumberPastALongIsRefused() {
            JObject reply = JObject.Parse("""{"used_bytes": 10000000000000000000}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.Long(reply, "used_bytes"));
        }

        // Binning travels as a short, and a whole number that fits reads as one.
        [Test]
        public void AWholeNumberThatFitsAShortReadsAsAShort() {
            JObject reply = JObject.Parse("""{"binning": 300}""");

            Assert.That(ResponseFields.Short(reply, "binning"), Is.EqualTo(300));
        }

        // A number past a short in either direction is refused rather than wrapped.
        [TestCase("40000")]
        [TestCase("-40000")]
        public void ANumberPastAShortIsRefused(string literal) {
            JObject reply = JObject.Parse($$"""{"binning": {{literal}}}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.Short(reply, "binning"));
        }

        ///////////// RoundedInt /////////////

        // The server computes a focuser start position as a float, and it is rounded to the nearest step. A half goes
        // to the even neighbour, which is Math.Round's default, pinned here so a later change is a deliberate one.
        [TestCase("2.6", 3)]
        [TestCase("2.5", 2)]
        [TestCase("3", 3)]
        public void AFractionIsRoundedToTheNearestInt(string literal, int expected) {
            JObject reply = JObject.Parse($$"""{"starting_position": {{literal}}}""");

            Assert.That(ResponseFields.RoundedInt(reply, "starting_position"), Is.EqualTo(expected));
        }

        // NaN can't be rounded and 3e9 rounds past an int. Both refuse the reply rather than produce a position.
        [TestCase("NaN")]
        [TestCase("3e9")]
        public void AValueThatIsNotFiniteOrDoesNotFitAnIntIsNotRounded(string literal) {
            JObject reply = JObject.Parse($$"""{"starting_position": {{literal}}}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.RoundedInt(reply, "starting_position"));
        }

        ///////////// Bool /////////////

        // A real true or false reads as itself.
        [TestCase("true", true)]
        [TestCase("false", false)]
        public void ABoolReadsAsABool(string literal, bool expected) {
            JObject reply = JObject.Parse($$"""{"has_space": {{literal}}}""");

            Assert.That(ResponseFields.Bool(reply, "has_space"), Is.EqualTo(expected));
        }

        // The number 1 or the text true where a bool belongs refuses the reply rather than being read as truthy.
        [TestCase("1")]
        [TestCase("\"true\"")]
        public void AnythingButABoolIsRefused(string literal) {
            JObject reply = JObject.Parse($$"""{"has_space": {{literal}}}""");

            Assert.Throws<InvalidDataException>(() => ResponseFields.Bool(reply, "has_space"));
        }

        ///////////// The error /////////////

        // The exception names the field, so a bad reply is diagnosed from the log rather than by guessing which of a
        // dozen fields it was.
        [Test]
        public void TheErrorNamesTheField() {
            JObject reply = JObject.Parse("""{"gain": "ten"}""");

            Assert.That(() => ResponseFields.Int(reply, "gain"),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("'gain'"));
        }
    }
}