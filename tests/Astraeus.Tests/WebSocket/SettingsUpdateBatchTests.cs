using System.Collections.Generic;
using System.Text.Json;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.WebSocket {
    /// <summary>
    /// When a settings update comes from the server it sends a grouped set of values (e.g all of the mount settings)
    /// SettingsUpdateBatch first wraps all of the fields and ensures they fit within reasonable bounds.
    ///
    /// These tests ensure the parsing of the settings is done correctly
    /// </summary>
    
    [TestFixture]
    public class SettingsUpdateBatchTests {
        private enum Colour { Red, Green, Blue }

        // The basic path: a field of the right type reaches its setter when TryApply runs, and TryApply reports
        // success.
        [Test]
        public void AValidFieldIsAppliedByTryApply() {
            int gain = -1;
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("""{"gain": 5}"""))
                .Int("gain", value => gain = value);
            bool wasApplied = batch.TryApply();
            Assert.That(wasApplied, Is.True);
            Assert.That(gain, Is.EqualTo(5));
        }

        // Setters are only queued as fields are added, so a later field failing can still stop every earlier one.
        // This proves nothing runs early.
        [Test]
        public void NothingIsAppliedBeforeTryApply() {
            int gain = -1;
            new SettingsUpdateBatch(Json.Parse("""{"gain": 5}""")).Int("gain", value => gain = value);
            Assert.That(gain, Is.EqualTo(-1));
        }

        // A partial update from the server must not touch settings it didn't mention, and must still count as
        // applied.
        [Test]
        public void AnAbsentFieldLeavesItsSettingAlone() {
            int gain = -1;
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("{}"))
                .Int("gain", value => gain = value);

            bool wasApplied = batch.TryApply();

            Assert.That(wasApplied, Is.True);
            Assert.That(gain, Is.EqualTo(-1));
        }

        // All or nothing: one wrong-typed field stops every field in the update, even the ones that were fine, so a
        // half-applied update can't happen.
        [Test]
        public void OneInvalidFieldRejectsTheWholeUpdate() {
            int gain = -1;
            int offset = -1;
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("""{"gain": 5, "offset": "ten"}"""))
                .Int("gain", value => gain = value)
                .Int("offset", value => offset = value);

            bool wasApplied = batch.TryApply();

            Assert.That(wasApplied, Is.False);
            Assert.That(gain, Is.EqualTo(-1));
            Assert.That(offset, Is.EqualTo(-1));
            Assert.That(batch.InvalidFieldNames, Is.EqualTo("offset"));
        }

        // The rejection the dashboard gets names every field that failed, so a server bug is found from the log
        // rather than by guessing.
        [Test]
        public void InvalidFieldNamesListsEveryFailedFieldInOrder() {
            SettingsUpdateBatch batch = new SettingsUpdateBatch(
                    Json.Parse("""{"offset": "ten", "gain": 5, "binning": 1.5}"""))
                .Int("offset", _ => { })
                .Int("gain", _ => { })
                .Int("binning", _ => { });

            Assert.That(batch.InvalidFieldNames, Is.EqualTo("offset, binning"));
        }

        // No failures means an empty list, so the warning path is never taken for a good update.
        [Test]
        public void InvalidFieldNamesIsEmptyWhenEveryFieldPasses() {
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("""{"gain": 5}"""))
                .Int("gain", _ => { })
                .Int("offset", _ => { });

            Assert.That(batch.InvalidFieldNames, Is.Empty);
        }

        // A range given to the batch must reach the reader, so an out-of-range value fails the update the same way a
        // wrong type does.
        [Test]
        public void ARangeIsPassedThroughToTheReader() {
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("""{"gain": 500}"""))
                .Int("gain", _ => { }, min: 0, max: 100);

            bool wasApplied = batch.TryApply();

            Assert.That(wasApplied, Is.False);
            Assert.That(batch.InvalidFieldNames, Is.EqualTo("gain"));
        }

        // Settings that depend on each other rely on the order the code added them, not the order the JSON happened
        // to list them.
        [Test]
        public void SettersRunInTheOrderTheyWereAdded() {
            List<string> order = [];
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("""{"first": 1, "second": 2}"""))
                .Int("second", _ => order.Add("second"))
                .Int("first", _ => order.Add("first"));

            batch.TryApply();

            Assert.That(order, Is.EqualTo(new[] { "second", "first" }));
        }

        ///////////// Custom /////////////

        // A custom field the server didn't send is left alone whatever the caller's validity flag says, so an absent
        // device map can't reject an update.
        [Test]
        public void ACustomFieldThatIsNotPresentIsIgnoredEvenWhenMarkedInvalid() {
            bool wasApplied = false;
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("{}"))
                .Custom("shape", isPresent: false, isValid: false, () => wasApplied = true);

            bool didApply = batch.TryApply();

            Assert.That(didApply, Is.True);
            Assert.That(wasApplied, Is.False);
            Assert.That(batch.InvalidFieldNames, Is.Empty);
        }

        // A custom field the caller judged malformed rejects the whole update and is named, like any other bad field.
        [Test]
        public void AnInvalidCustomFieldRejectsTheUpdate() {
            bool wasApplied = false;
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("""{"shape": []}"""))
                .Custom("shape", isPresent: true, isValid: false, () => wasApplied = true);

            bool didApply = batch.TryApply();

            Assert.That(didApply, Is.False);
            Assert.That(wasApplied, Is.False);
            Assert.That(batch.InvalidFieldNames, Is.EqualTo("shape"));
        }

        // A custom field the caller accepted is applied alongside the ordinary fields on TryApply.
        [Test]
        public void AValidCustomFieldIsAppliedWithTheRest() {
            int gain = -1;
            bool wasApplied = false;
            SettingsUpdateBatch batch = new SettingsUpdateBatch(Json.Parse("""{"gain": 5, "shape": []}"""))
                .Int("gain", value => gain = value)
                .Custom("shape", isPresent: true, isValid: true, () => wasApplied = true);

            bool didApply = batch.TryApply();

            Assert.That(didApply, Is.True);
            Assert.That(gain, Is.EqualTo(5));
            Assert.That(wasApplied, Is.True);
        }

        ///////////// Every reader /////////////

        // Every reader kind is wired to collect its setter. A reader that forgot to would pass the per-kind tests and
        // silently drop its setting.
        [Test]
        public void EveryReaderHandsItsValueToItsSetter() {
            JsonElement data = Json.Parse("""
                {
                    "is_on": true,
                    "ratio": 1.5,
                    "count": 3,
                    "rounded": 2.6,
                    "small": 7,
                    "name": "pier",
                    "colour_number": 1,
                    "colour_name": "Blue"
                }
                """);
            bool isOn = false;
            double ratio = 0;
            int count = 0;
            int rounded = 0;
            short small = 0;
            string name = "";
            Colour colourNumber = Colour.Red;
            Colour colourName = Colour.Red;

            bool wasApplied = new SettingsUpdateBatch(data)
                .Bool("is_on", value => isOn = value)
                .Double("ratio", value => ratio = value)
                .Int("count", value => count = value)
                .RoundedInt("rounded", value => rounded = value)
                .Short("small", value => small = value)
                .String("name", value => name = value)
                .EnumNumber<Colour>("colour_number", value => colourNumber = value)
                .EnumName<Colour>("colour_name", value => colourName = value)
                .TryApply();

            Assert.Multiple(() => {
                Assert.That(wasApplied, Is.True);
                Assert.That(isOn, Is.True);
                Assert.That(ratio, Is.EqualTo(1.5));
                Assert.That(count, Is.EqualTo(3));
                Assert.That(rounded, Is.EqualTo(3));
                Assert.That(small, Is.EqualTo(7));
                Assert.That(name, Is.EqualTo("pier"));
                Assert.That(colourNumber, Is.EqualTo(Colour.Green));
                Assert.That(colourName, Is.EqualTo(Colour.Blue));
            });
        }
    }
}