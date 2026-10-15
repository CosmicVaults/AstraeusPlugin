using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests {
    [TestFixture]
    public class AstraeusSettingsTests {
        // The option was renamed in code but its profile key stayed, so every user's stored value carries over.
        private const string RoofOptionKey = "Astraeus.ParkMountToOpenRoof";

        // The option is off by default, since a park position only reachable with the roof open means a collision.
        [Test]
        public void TheRoofOptionIsOffUntilSet() {
            FakeNina nina = new FakeNina();

            Assert.That(nina.Settings.CanMountMoveUnderClosedRoof(), Is.False);
        }

        // The option was renamed in code but its profile key stayed, so every user's stored value must still be read.
        [Test]
        public void TheRoofOptionReadsTheValueStoredUnderItsOriginalKey() {
            FakeNina nina = new FakeNina();
            nina.StoreSetting(RoofOptionKey, "true");

            Assert.That(nina.Settings.CanMountMoveUnderClosedRoof(), Is.True);
        }

        // Setting the option writes the original key, so the value survives a downgrade and matches what older builds
        // read.
        [Test]
        public void SettingTheRoofOptionWritesItsOriginalKey() {
            FakeNina nina = new FakeNina();

            nina.Settings.SetCanMountMoveUnderClosedRoof(true);

            Assert.That(nina.StoredSetting(RoofOptionKey), Is.EqualTo("true"));
        }

        // A corrupt stored value reads as the safe default rather than as on.
        [Test]
        public void AStoredValueThatIsNotABoolReadsAsTheDefault() {
            FakeNina nina = new FakeNina();
            nina.StoreSetting(RoofOptionKey, "maybe");

            Assert.That(nina.Settings.CanMountMoveUnderClosedRoof(), Is.False);
        }
    }
}