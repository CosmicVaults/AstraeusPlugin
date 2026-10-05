using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Utility {
    /// <summary>
    /// Keeps the Windows user name out of text sent to the server. Local paths in log lines usually
    /// start with the user's profile folder (C:\Users\name\...), which names the user.
    /// </summary>
    internal static class UserProfilePath {
        private const string Placeholder = "%USERPROFILE%";

        private static readonly string ProfileFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string Redact(string text) {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(ProfileFolder)) return text;
            return text.Replace(ProfileFolder, Placeholder, StringComparison.OrdinalIgnoreCase);
        }
    }
}
