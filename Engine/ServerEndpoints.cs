namespace CosmicVaults.NINA.Astraeus.Engine {
    internal static class ServerEndpoints {
        public const string AutopilotApiSegment = "autopilot";
        public const string AutofocusApiSegment = "autofocus";

        public const string OAuthClientId = "astraeus-nina-plugin";
        public const string OAuthScope = "observatory autopilot";

        // The server matches redirect_uri exactly and doesn't wildcard the port, so these must match
        // the loopback ports registered on the server.
        public static readonly int[] LoopbackPorts = {
            47821, 47822, 47823, 47824, 47825, 47826, 47827, 47828, 47829, 47830
        };

#if PRODUCTION
        public const string BaseUrl = "https://astraeus.cosmicvaults.com";
        public const string WebSocketBaseUri = "wss://astraeus.cosmicvaults.com/ws/observatory/";
#elif DEV
        public const string BaseUrl = "http://localhost:8000";
        public const string WebSocketBaseUri = "ws://localhost:8000/ws/observatory/";
#elif STAGING
        // A staging server, for the maintainer's own testing. Its address is not published, so no
        // public build can point users at it. Fill in both lines to build against one with
        // -p:AstraeusBackend=staging.
        // public const string BaseUrl = "https://<staging host>";
        // public const string WebSocketBaseUri = "wss://<staging host>/ws/observatory/";
#error The staging server's address is not published. Fill it in above to build against staging.
#else
#error No Astraeus server selected: define DEV, PRODUCTION or STAGING (AstraeusBackend in Astraeus.csproj).
#endif

        /// <summary>The server's host name, shown on the options page and in the log so a screenshot says which server a build uses.</summary>
        public static readonly string Host = new System.Uri(BaseUrl).Authority;
    }
}
