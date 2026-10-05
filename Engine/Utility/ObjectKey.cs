using System.Collections.Generic;
using System.Text;

namespace CosmicVaults.NINA.Astraeus.Engine.Utility {
    /// <summary>
    /// All R2 object key handling goes through here, never System.IO.Path. Path uses backslashes on
    /// Windows, and R2 only reads the forward slash as a folder boundary. Never throws.
    /// </summary>
    internal static class ObjectKey {
        public const char Separator = '/';
        private const int MaxSegmentLength = 120;

        // Characters that can't safely go into a key. The two slashes would add a folder level, and
        // the rest are reserved on filesystems or ambiguous in a signed URL. Anything else survives,
        // spaces and accents included, since "NGC 7000" is what the user expects to see in the bucket.
        private static readonly char[] UnsafeCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '#'];

        // Trimmed from both ends. Some tools hide a segment with a leading dot and others drop a
        // trailing one. Control characters are already underscores by the time this runs.
        private static readonly char[] TrimCharacters = [' ', '\t', '.'];

        /// <summary>
        /// The target name comes from server JSON unvalidated, so anything unsafe becomes an underscore.
        /// Null when nothing usable is left, which callers read as "no folder". Never throws.
        /// </summary>
        public static string? SafeSegment(string? name) {
            if (string.IsNullOrWhiteSpace(name)) return null;

            StringBuilder builder = new StringBuilder(name!.Length);
            foreach (char character in name)
                builder.Append(char.IsControl(character) || IsUnsafe(character) ? '_' : character);

            string cleaned = builder.ToString().Trim(TrimCharacters);
            // Truncate before the second trim, since cutting mid-name can leave a new trailing dot or space.
            if (cleaned.Length > MaxSegmentLength) cleaned = cleaned[..MaxSegmentLength].Trim(TrimCharacters);

            return cleaned.Length == 0 ? null : cleaned;
        }

        public static string Join(params string?[] segments) {
            List<string> parts = new List<string>(segments.Length);
            foreach (string? segment in segments)
                if (!string.IsNullOrEmpty(segment)) parts.Add(segment!);
            return string.Join(Separator.ToString(), parts);
        }

        public static string ReplaceFileName(string key, string fileName) {
            int lastSeparator = key.LastIndexOf(Separator);
            return lastSeparator < 0 ? fileName : string.Concat(key[..(lastSeparator + 1)], fileName);
        }

        /// <summary>
        /// The key with its extension replaced by the given one (dot included), or with it appended
        /// if the last segment has none.
        /// </summary>
        public static string WithExtension(string key, string extension) {
            int lastSeparator = key.LastIndexOf(Separator);
            int lastDot = key.LastIndexOf('.');
            // Only a dot in the last segment counts. A dotted folder name isn't an extension.
            return lastDot > lastSeparator ? string.Concat(key[..lastDot], extension) : string.Concat(key, extension);
        }

        private static bool IsUnsafe(char character) {
            foreach (char unsafeCharacter in UnsafeCharacters)
                if (character == unsafeCharacter) return true;
            return false;
        }
    }
}
