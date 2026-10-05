using System;
using System.Text.Json;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket {
    internal enum JsonFieldState {
        /// <summary>Not sent, or sent as null. Whatever it would set is left alone.</summary>
        Absent,

        Valid,

        /// <summary>Present, but the wrong type or too big for the C# type it's stored in.</summary>
        Invalid
    }

    /// <summary>
    /// Reads typed fields from a server message, checking each has the expected JSON type and fits its
    /// C# type. Ranges aren't checked, since keeping values sensible is the server's job.
    /// </summary>
    internal static class JsonFields {
        public static JsonFieldState ReadBool(JsonElement parent, string name, out bool value) {
            value = false;
            if (!TryGetPresent(parent, name, out JsonElement element)) return JsonFieldState.Absent;
            if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False) {
                return JsonFieldState.Invalid;
            }
            value = element.GetBoolean();
            return JsonFieldState.Valid;
        }

        public static JsonFieldState ReadDouble(JsonElement parent, string name, out double value) {
            value = 0;
            if (!TryGetPresent(parent, name, out JsonElement element)) return JsonFieldState.Absent;
            bool isFiniteNumber = element.ValueKind == JsonValueKind.Number
                                  && element.TryGetDouble(out double number)
                                  && double.IsFinite(number);
            if (!isFiniteNumber) return JsonFieldState.Invalid;
            value = element.GetDouble();
            return JsonFieldState.Valid;
        }

        public static JsonFieldState ReadInt(JsonElement parent, string name, out int value) {
            value = 0;
            if (!TryGetPresent(parent, name, out JsonElement element)) return JsonFieldState.Absent;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value)) {
                value = 0;
                return JsonFieldState.Invalid;
            }
            return JsonFieldState.Valid;
        }

        public static JsonFieldState ReadRoundedInt(JsonElement parent, string name, out int value) {
            value = 0;
            JsonFieldState state = ReadDouble(parent, name, out double number);
            if (state != JsonFieldState.Valid) return state;
            double rounded = Math.Round(number);
            if (rounded < int.MinValue || rounded > int.MaxValue) return JsonFieldState.Invalid;
            value = (int)rounded;
            return JsonFieldState.Valid;
        }

        public static JsonFieldState ReadShort(JsonElement parent, string name, out short value) {
            value = 0;
            if (!TryGetPresent(parent, name, out JsonElement element)) return JsonFieldState.Absent;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt16(out value)) {
                value = 0;
                return JsonFieldState.Invalid;
            }
            return JsonFieldState.Valid;
        }

        public static JsonFieldState ReadString(JsonElement parent, string name, out string value) {
            value = string.Empty;
            if (!TryGetPresent(parent, name, out JsonElement element)) return JsonFieldState.Absent;
            if (element.ValueKind != JsonValueKind.String) return JsonFieldState.Invalid;
            value = element.GetString() ?? string.Empty;
            return JsonFieldState.Valid;
        }

        /// <summary>An enum sent as its number, as N.I.N.A.'s own profile enums are.</summary>
        public static JsonFieldState ReadEnumNumber<TEnum>(JsonElement parent, string name, out TEnum value)
            where TEnum : struct, Enum {
            value = default;
            JsonFieldState state = ReadInt(parent, name, out int number);
            if (state != JsonFieldState.Valid) return state;
            object candidate = Enum.ToObject(typeof(TEnum), number);
            if (!Enum.IsDefined(typeof(TEnum), candidate)) return JsonFieldState.Invalid;
            value = (TEnum)candidate;
            return JsonFieldState.Valid;
        }

        /// <summary>An enum sent as its name, as the plugin's own settings are.</summary>
        public static JsonFieldState ReadEnumName<TEnum>(JsonElement parent, string name, out TEnum value)
            where TEnum : struct, Enum {
            value = default;
            JsonFieldState state = ReadString(parent, name, out string text);
            if (state != JsonFieldState.Valid) return state;
            if (!Enum.TryParse(text, out TEnum parsed) || !Enum.IsDefined(typeof(TEnum), parsed)) {
                return JsonFieldState.Invalid;
            }
            value = parsed;
            return JsonFieldState.Valid;
        }

        private static bool TryGetPresent(JsonElement parent, string name, out JsonElement element) {
            element = default;
            if (parent.ValueKind != JsonValueKind.Object) return false;
            if (!parent.TryGetProperty(name, out element)) return false;
            return element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.Undefined;
        }
    }
}
