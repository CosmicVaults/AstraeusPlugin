using NINA.Core.Utility;
using System;
using System.Security.Cryptography;
using System.Text;

namespace CosmicVaults.NINA.Astraeus.Engine.Auth {

    /// <summary>
    /// DPAPI wrapper for the refresh token, since N.I.N.A.'s plugin settings sit in plain XML under
    /// %LOCALAPPDATA%\NINA\Profiles.
    /// </summary>
    public static class TokenStore {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("CosmicVaults.Astraeus.v1");

        public static string? Protect(string? plaintext) {
            if (string.IsNullOrEmpty(plaintext)) return null;
            try {
                byte[] bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(bytes);
            } catch (Exception ex) {
                Logger.Error($"[Astraeus] Could not protect token: {ex.Message}");
                return null;
            }
        }

        /// <summary>Null when there's nothing to read or it can't be decrypted.</summary>
        public static string? Unprotect(string? ciphertext) {
            if (string.IsNullOrEmpty(ciphertext)) return null;
            try {
                byte[] bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            } catch (Exception ex) {
                Logger.Info($"[Astraeus] Stored token unreadable ({ex.GetType().Name}); sign-in required.");
                return null;
            }
        }
    }
}