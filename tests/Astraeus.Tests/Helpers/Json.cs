using System.Text.Json;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    internal static class Json {
        /// <summary>
        /// Convert JSON string into a JsonElement as used in Settings Update.
        /// </summary>
        public static JsonElement Parse(string text) {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
    }
}