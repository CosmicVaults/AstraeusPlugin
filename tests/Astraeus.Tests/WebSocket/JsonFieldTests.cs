using System.Text.Json;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.WebSocket {
    /// <summary>
    /// JSON Fields parse named JSON elements into their respecitive types e.g. int, bool, ensuring they are valid numbers and/or bools.
    /// They return a JSONFieldState which is either Valid, Missing or Invalid.
    /// These tests ensure that invalid or missing JSON fields won't get passed the WS parser. 
    /// </summary>
    
    [TestFixture]
    public class JsonFieldsTests {
        private enum Colour { Red, Green, Blue }

        ///////////// Presence /////////////

        // A field the server didn't send must leave the setting alone, not read as false. Absent is harmless, Invalid
        // rejects the whole command.
        [Test]
        public void AMissingKeyReadsAsAbsent() {
            JsonElement parent = Json.Parse("{}");
            JsonFieldState state = JsonFields.ReadBool(parent, "is_on", out bool value);
            Assert.That(state, Is.EqualTo(JsonFieldState.Absent));
            Assert.That(value, Is.False);
        }

        // The server sends null for a cleared setting, and that must mean leave it alone, not reject the command.
        [Test]
        public void ANullValueReadsAsAbsent() {
            JsonElement parent = Json.Parse("""{"is_on": null}""");
            JsonFieldState state = JsonFields.ReadBool(parent, "is_on", out bool _);
            Assert.That(state, Is.EqualTo(JsonFieldState.Absent));
        }

        // A payload that isn't an object has no fields to read, and that reads as nothing sent rather than a
        // rejection or a crash.
        [Test]
        public void AParentThatIsNotAnObjectReadsAsAbsent() {
            JsonElement parent = Json.Parse("[1, 2]");
            JsonFieldState state = JsonFields.ReadInt(parent, "count", out int _);
            Assert.That(state, Is.EqualTo(JsonFieldState.Absent));
        }

        ///////////// Bool /////////////

        // A real true or false reaches the setter unchanged, in both values.
        [TestCase("true", true)]
        [TestCase("false", false)]
        public void ABoolReadsAsABool(string literal, bool expected) {
            JsonElement parent = Json.Parse($$"""{"is_on": {{literal}}}""");
            JsonFieldState state = JsonFields.ReadBool(parent, "is_on", out bool value);
            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(expected));
        }

        // Nothing is coerced. The number 1 or the text true is a wrong type, and a wrong type rejects the command so
        // a driver never gets a guess.
        [TestCase("1")]
        [TestCase("\"true\"")]
        public void AnythingButABoolIsInvalid(string literal) {
            JsonElement parent = Json.Parse($$"""{"is_on": {{literal}}}""");
            JsonFieldState state = JsonFields.ReadBool(parent, "is_on", out bool value);
            Assert.That(state, Is.EqualTo(JsonFieldState.Invalid));
            Assert.That(value, Is.False);
        }

        ///////////// Double /////////////

        // Any JSON number, with or without a fraction, reads as a double, so an exposure of 300 and one of 2.5 both
        // reach the camera.
        [TestCase("1.5", 1.5)]
        [TestCase("2", 2.0)]
        [TestCase("-0.25", -0.25)]
        public void ANumberReadsAsADouble(string literal, double expected) {
            JsonElement parent = Json.Parse($$"""{"ratio": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadDouble(parent, "ratio", out double value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(expected));
        }

        // A string, a bool or a number too big to be finite must never reach a driver as a value. 1e400 is past
        // double.MaxValue, so it parses as infinity or not at all, and either way it is refused.
        [TestCase("\"1.5\"")]
        [TestCase("true")]
        [TestCase("1e400")]
        public void AnythingButAFiniteNumberIsInvalid(string literal) {
            JsonElement parent = Json.Parse($$"""{"ratio": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadDouble(parent, "ratio", out double _);

            Assert.That(state, Is.EqualTo(JsonFieldState.Invalid));
        }

        // Callers pass a range for anything that moves hardware. A value just outside it is refused while the bounds
        // themselves pass.
        [TestCase("0", true)]
        [TestCase("10", true)]
        [TestCase("-0.1", false)]
        [TestCase("10.1", false)]
        public void ADoubleIsCheckedAgainstItsRangeInclusive(string literal, bool isInRange) {
            JsonElement parent = Json.Parse($$"""{"ratio": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadDouble(parent, "ratio", out double _, min: 0, max: 10);

            Assert.That(state, Is.EqualTo(isInRange ? JsonFieldState.Valid : JsonFieldState.Invalid));
        }

        ///////////// Int /////////////

        // A whole number, positive or negative, reads as an int.
        [TestCase("42", 42)]
        [TestCase("-7", -7)]
        public void AWholeNumberReadsAsAnInt(string literal, int expected) {
            JsonElement parent = Json.Parse($$"""{"count": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadInt(parent, "count", out int value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(expected));
        }

        // A fraction, a number past int.MaxValue or digits in quotes are refused, with the out value left at zero so
        // nothing downstream reads garbage.
        [TestCase("1.5")]
        [TestCase("3000000000")]
        [TestCase("\"42\"")]
        public void AnythingButAWholeNumberThatFitsAnIntIsInvalid(string literal) {
            JsonElement parent = Json.Parse($$"""{"count": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadInt(parent, "count", out int value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Invalid));
            Assert.That(value, Is.Zero);
        }

        // Same range rule as doubles: the bounds pass, and one past either bound is refused.
        [TestCase("0", true)]
        [TestCase("10", true)]
        [TestCase("-1", false)]
        [TestCase("11", false)]
        public void AnIntIsCheckedAgainstItsRangeInclusive(string literal, bool isInRange) {
            JsonElement parent = Json.Parse($$"""{"count": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadInt(parent, "count", out int _, min: 0, max: 10);

            Assert.That(state, Is.EqualTo(isInRange ? JsonFieldState.Valid : JsonFieldState.Invalid));
        }

        ///////////// RoundedInt /////////////

        // A setting the server stores as a float, like a focuser position, is rounded to the nearest int. A half goes
        // to the even neighbour, which is Math.Round's default, pinned here so a later change is a deliberate one.
        [TestCase("1.4", 1)]
        [TestCase("1.6", 2)]
        [TestCase("-1.6", -2)]
        [TestCase("2.5", 2)]
        [TestCase("3.5", 4)]
        public void AFractionIsRoundedToTheNearestInt(string literal, int expected) {
            JsonElement parent = Json.Parse($$"""{"gain": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadRoundedInt(parent, "gain", out int value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(expected));
        }

        // The range applies to the rounded value, so 10.4 passes a maximum of 10 and 10.6 does not.
        [TestCase("10.4", true, 10)]
        [TestCase("10.6", false, 0)]
        public void TheRangeIsCheckedAfterRounding(string literal, bool isInRange, int expectedValue) {
            JsonElement parent = Json.Parse($$"""{"gain": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadRoundedInt(parent, "gain", out int value, min: 0, max: 10);

            Assert.That(state, Is.EqualTo(isInRange ? JsonFieldState.Valid : JsonFieldState.Invalid));
            Assert.That(value, Is.EqualTo(expectedValue));
        }

        // Rounding keeps the double reader's verdict: a missing field stays Absent and a string stays Invalid, with
        // the out value zero.
        [TestCase("{}", true)]
        [TestCase("{\"gain\": \"7\"}", false)]
        public void AMissingOrNonNumericValueIsNotRounded(string json, bool isAbsent) {
            JsonElement parent = Json.Parse(json);

            JsonFieldState state = JsonFields.ReadRoundedInt(parent, "gain", out int value);

            Assert.That(state, Is.EqualTo(isAbsent ? JsonFieldState.Absent : JsonFieldState.Invalid));
            Assert.That(value, Is.Zero);
        }

        ///////////// Short /////////////

        // Binning travels as a short, and a whole number that fits reads as one.
        [Test]
        public void AWholeNumberThatFitsAShortReadsAsAShort() {
            JsonElement parent = Json.Parse("""{"binning": 300}""");

            JsonFieldState state = JsonFields.ReadShort(parent, "binning", out short value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(300));
        }

        // A number past a short, or a fraction, is refused rather than wrapped into a nonsense binning.
        [TestCase("40000")]
        [TestCase("-40000")]
        [TestCase("1.5")]
        public void ANumberThatDoesNotFitAShortIsInvalid(string literal) {
            JsonElement parent = Json.Parse($$"""{"binning": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadShort(parent, "binning", out short value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Invalid));
            Assert.That(value, Is.Zero);
        }

        ///////////// String /////////////

        // A string reads as itself, and an empty string is a valid value, not a missing one.
        [TestCase("\"pier\"", "pier")]
        [TestCase("\"\"", "")]
        public void AStringReadsAsAString(string literal, string expected) {
            JsonElement parent = Json.Parse($$"""{"name": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadString(parent, "name", out string value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(expected));
        }

        // A number or bool is not quietly turned into text, and the out value is empty so no caller logs a null.
        [TestCase("1")]
        [TestCase("true")]
        public void AnythingButAStringIsInvalid(string literal) {
            JsonElement parent = Json.Parse($$"""{"name": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadString(parent, "name", out string value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Invalid));
            Assert.That(value, Is.Empty);
        }

        // A missing string reads as Absent with an empty out value, so callers never have to null-check it.
        [Test]
        public void AnAbsentStringReadsAsEmptyNotNull() {
            JsonElement parent = Json.Parse("{}");

            JsonFieldState state = JsonFields.ReadString(parent, "name", out string value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Absent));
            Assert.That(value, Is.Empty);
        }

        ///////////// Enums /////////////

        // N.I.N.A.'s profile enums arrive as numbers, and a defined number must map to its member.
        [Test]
        public void ADefinedNumberReadsAsItsEnumMember() {
            JsonElement parent = Json.Parse("""{"colour": 1}""");

            JsonFieldState state = JsonFields.ReadEnumNumber(parent, "colour", out Colour value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(Colour.Green));
        }

        // A number no member has, or a name where a number is expected, is refused so an enum never holds a value it
        // doesn't define.
        [TestCase("7")]
        [TestCase("\"Green\"")]
        public void AnUndefinedNumberOrANameIsNotAnEnumNumber(string literal) {
            JsonElement parent = Json.Parse($$"""{"colour": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadEnumNumber(parent, "colour", out Colour value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Invalid));
            Assert.That(value, Is.EqualTo(Colour.Red));
        }

        // The plugin's own settings arrive as member names, and a defined name must map to its member.
        [Test]
        public void ADefinedNameReadsAsItsEnumMember() {
            JsonElement parent = Json.Parse("""{"colour": "Green"}""");

            JsonFieldState state = JsonFields.ReadEnumName(parent, "colour", out Colour value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Valid));
            Assert.That(value, Is.EqualTo(Colour.Green));
        }

        // The server sends the exact member name, so a name no member has, a case mismatch, an undefined number as
        // text and a JSON number are all refused.
        [TestCase("\"Purple\"")]
        [TestCase("\"green\"")]
        [TestCase("\"7\"")]
        [TestCase("1")]
        public void AnythingButADefinedNameIsNotAnEnumName(string literal) {
            JsonElement parent = Json.Parse($$"""{"colour": {{literal}}}""");

            JsonFieldState state = JsonFields.ReadEnumName(parent, "colour", out Colour value);

            Assert.That(state, Is.EqualTo(JsonFieldState.Invalid));
            Assert.That(value, Is.EqualTo(Colour.Red));
        }
    }
}