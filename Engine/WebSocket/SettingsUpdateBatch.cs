using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket {
    /// <summary>
    /// The fields of one settings update from the server, applied together or not at all. Each field
    /// is type-checked as it's added, and TryApply only runs the setters if every sent field passed,
    /// so a wrong type part-way through can't leave a half-applied update.
    /// </summary>
    internal sealed class SettingsUpdateBatch {
        private readonly JsonElement _data;
        private readonly List<Action> _setters = new List<Action>();
        private readonly List<string> _invalidFieldNames = new List<string>();

        public SettingsUpdateBatch(JsonElement data) {
            _data = data;
        }

        public string InvalidFieldNames => string.Join(", ", _invalidFieldNames);

        public SettingsUpdateBatch Bool(string name, Action<bool> apply) {
            JsonFieldState state = JsonFields.ReadBool(_data, name, out bool value);
            Collect(name, state, () => apply(value));
            return this;
        }

        public SettingsUpdateBatch Double(string name, Action<double> apply) {
            JsonFieldState state = JsonFields.ReadDouble(_data, name, out double value);
            Collect(name, state, () => apply(value));
            return this;
        }

        public SettingsUpdateBatch Int(string name, Action<int> apply) {
            JsonFieldState state = JsonFields.ReadInt(_data, name, out int value);
            Collect(name, state, () => apply(value));
            return this;
        }

        public SettingsUpdateBatch RoundedInt(string name, Action<int> apply) {
            JsonFieldState state = JsonFields.ReadRoundedInt(_data, name, out int value);
            Collect(name, state, () => apply(value));
            return this;
        }

        public SettingsUpdateBatch Short(string name, Action<short> apply) {
            JsonFieldState state = JsonFields.ReadShort(_data, name, out short value);
            Collect(name, state, () => apply(value));
            return this;
        }

        public SettingsUpdateBatch String(string name, Action<string> apply) {
            JsonFieldState state = JsonFields.ReadString(_data, name, out string value);
            Collect(name, state, () => apply(value));
            return this;
        }

        public SettingsUpdateBatch EnumNumber<TEnum>(string name, Action<TEnum> apply) where TEnum : struct, Enum {
            JsonFieldState state = JsonFields.ReadEnumNumber(_data, name, out TEnum value);
            Collect(name, state, () => apply(value));
            return this;
        }

        public SettingsUpdateBatch EnumName<TEnum>(string name, Action<TEnum> apply) where TEnum : struct, Enum {
            JsonFieldState state = JsonFields.ReadEnumName(_data, name, out TEnum value);
            Collect(name, state, () => apply(value));
            return this;
        }

        /// <summary>For a field with its own shape. If isValid is false, the whole update is rejected.</summary>
        public SettingsUpdateBatch Custom(string name, bool isPresent, bool isValid, Action apply) {
            if (!isPresent) return this;
            JsonFieldState state = isValid ? JsonFieldState.Valid : JsonFieldState.Invalid;
            Collect(name, state, apply);
            return this;
        }

        public bool TryApply() {
            if (_invalidFieldNames.Count > 0) return false;
            foreach (Action setter in _setters) {
                setter();
            }
            return true;
        }

        private void Collect(string name, JsonFieldState state, Action setter) {
            if (state == JsonFieldState.Valid) {
                _setters.Add(setter);
            } else if (state == JsonFieldState.Invalid) {
                _invalidFieldNames.Add(name);
            }
        }
    }
}
