using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests {
    [TestFixture]
    public class SmokeTests {
        // Proves the InternalsVisibleTo in AssemblyInfo.cs is in place. Without it every fixture over an internal
        // type fails to compile, and this test names the cause.
        [Test]
        public void ThePluginsInternalsAreVisible() {
            Assert.That(typeof(JsonFields).IsNotPublic, Is.True);
        }
    }
}