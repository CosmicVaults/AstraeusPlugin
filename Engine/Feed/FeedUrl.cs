using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Feed {
    /// <summary>
    /// Credential handling for feed URLs. Camera URLs are often rtsp://user:pass@host/stream, and
    /// AstraeusLogSink shows every N.I.N.A. log event at Information and above on the dashboard. So a
    /// feed URL never goes in a log line, a payload or to the server, even with the credentials
    /// stripped, since the host alone gives away the user's network.
    /// </summary>
    internal static class FeedUrl {
        /// <summary>
        /// Strips the camera's address and credentials from a message before it's reported. Exception text
        /// can carry the address too, so leaving it out of our own text isn't enough.
        /// </summary>
        public static string RedactCameraDetails(string message, string? url) {
            if (string.IsNullOrEmpty(message)) return message;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return message;

            string redacted = message.Replace(url!, "the camera address", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(uri.UserInfo)) {
                redacted = redacted.Replace(uri.UserInfo, "***", StringComparison.Ordinal);
            }
            if (!string.IsNullOrEmpty(uri.Host)) {
                redacted = redacted.Replace(uri.Host, "the camera", StringComparison.OrdinalIgnoreCase);
            }
            return redacted;
        }

        /// <summary>
        /// url with any user:password@ taken out and returned separately, so the credentials go in the
        /// protected fields instead of the plain-text profile.
        /// </summary>
        public static string? WithoutCredentials(string? url, out string? username, out string? password) {
            username = null;
            password = null;
            if (string.IsNullOrWhiteSpace(url)) return url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.UserInfo)) return url;

            (username, password) = SplitUserInfo(uri.UserInfo);

            // Cut out of the text as typed, so nothing else in the URL is re-escaped or reordered.
            string credentialsPart = uri.UserInfo + "@";
            int credentialsIndex = url.IndexOf(credentialsPart, StringComparison.Ordinal);
            if (credentialsIndex >= 0) return url.Remove(credentialsIndex, credentialsPart.Length);

            UriBuilder withoutCredentials = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            return withoutCredentials.Uri.AbsoluteUri;
        }

        /// <summary>
        /// The credentials to use. The configured pair wins, then any in the URL, so a pasted full
        /// rtsp:// string still connects.
        /// </summary>
        public static (string? User, string? Password) ResolveCredentials(
            string? url, string? username, string? password) {
            if (!string.IsNullOrEmpty(username)) return (username, password);
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                !string.IsNullOrEmpty(uri.UserInfo)) {
                return SplitUserInfo(uri.UserInfo);
            }
            return (null, null);
        }

        private static (string? User, string? Password) SplitUserInfo(string userInfo) {
            string[] parts = userInfo.Split(':', 2);
            string user = Uri.UnescapeDataString(parts[0]);
            string? password = null;
            if (parts.Length > 1) password = Uri.UnescapeDataString(parts[1]);
            return (user, password);
        }
    }
}
