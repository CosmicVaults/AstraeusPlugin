using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// The Settings component sends only Key to the server, because a full push would overwrite
    /// scheduler fields like airmass_weight and max_retries that only the web sets.
    /// </summary>
    public sealed class SettingChangedEventArgs(string propertyName, string? key) : EventArgs {
        public string PropertyName { get; } = propertyName;

        /// <summary>
        /// The setting's snake_case key, matching its [JsonPropertyName] in AutopilotSettingsPayload.
        /// </summary>
        public string? Key { get; } = key;
    }
}
