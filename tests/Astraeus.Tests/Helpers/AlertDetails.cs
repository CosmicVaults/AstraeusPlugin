using System.Reflection;
using CosmicVaults.NINA.Astraeus.Engine;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>
    /// Reads one field of the anonymous details object the plugin attaches to an alert. An anonymous type from the
    /// plugin's assembly never equals one built in a test, so the fields are read by name.
    /// </summary>
    internal static class AlertDetails {
        public static object? Detail(this Observatory.ReportedEvent alert, string name) =>
            alert.Details?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(alert.Details);
    }
}