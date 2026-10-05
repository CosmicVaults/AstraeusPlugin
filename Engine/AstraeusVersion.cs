using System.Reflection;

namespace CosmicVaults.NINA.Astraeus.Engine {
    /// <summary>
    /// The plugin version from AssemblyFileVersion, which GenerateAstraeusVersionInfo writes at build time
    /// from AstraeusReleaseVersion in Astraeus.csproj. NINA reads the same attribute for its plugin list,
    /// so the server and the user always see the same version.
    /// </summary>
    internal static class AstraeusVersion {
        /// <summary>
        /// Three release parts and the build day, e.g. "1.0.0.271". Read once, since the connect hello is
        /// rebuilt on every reconnect. The fallback makes an unknown version obviously bogus instead of null.
        /// </summary>
        public static readonly string Current =
            typeof(AstraeusVersion).Assembly
                .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? "0.0.0.0";

        /// <summary>
        /// Sent on every request and on the socket handshake, so the server can refuse a version it no
        /// longer supports (HTTP 426 or close 4026) instead of letting it misbehave.
        /// </summary>
        public static readonly string UserAgent = "Astraeus-NINA/" + Current;
    }
}
