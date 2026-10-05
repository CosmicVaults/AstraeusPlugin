using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace CosmicVaults.NINA.Astraeus.Engine.Utility {
    /// <summary>
    /// Typed reads of number and bool fields in the server's HTTP replies. Missing or null fields read
    /// as null. A wrong type, a number that doesn't fit, or NaN or Infinity (the server can send these)
    /// throws InvalidDataException naming the field, so the whole reply is refused.
    /// </summary>
    internal static class ResponseFields {
        public static double? FiniteDouble(JToken? parent, string name) {
            JToken? token = Find(parent, name);
            if (token == null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw WrongType(name);
            double value = token.Value<double>();
            if (!double.IsFinite(value)) throw WrongType(name);
            return value;
        }

        public static int? Int(JToken? parent, string name) {
            long? value = Long(parent, name);
            if (value == null) return null;
            if (value < int.MinValue || value > int.MaxValue) throw WrongType(name);
            return (int)value.Value;
        }

        public static short? Short(JToken? parent, string name) {
            long? value = Long(parent, name);
            if (value == null) return null;
            if (value < short.MinValue || value > short.MaxValue) throw WrongType(name);
            return (short)value.Value;
        }

        public static int? RoundedInt(JToken? parent, string name) {
            double? value = FiniteDouble(parent, name);
            if (value == null) return null;
            double rounded = Math.Round(value.Value);
            if (rounded < int.MinValue || rounded > int.MaxValue) throw WrongType(name);
            return (int)rounded;
        }

        public static long? Long(JToken? parent, string name) {
            JToken? token = Find(parent, name);
            if (token == null) return null;
            if (token.Type != JTokenType.Integer || token is not JValue { Value: long value }) throw WrongType(name);
            return value;
        }

        public static bool? Bool(JToken? parent, string name) {
            JToken? token = Find(parent, name);
            if (token == null) return null;
            if (token.Type != JTokenType.Boolean) throw WrongType(name);
            return token.Value<bool>();
        }

        private static JToken? Find(JToken? parent, string name) {
            if (parent is not JObject parentObject) return null;
            JToken? token = parentObject[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token;
        }

        private static InvalidDataException WrongType(string name) =>
            new InvalidDataException($"'{name}' in the server's reply has the wrong type");
    }
}
