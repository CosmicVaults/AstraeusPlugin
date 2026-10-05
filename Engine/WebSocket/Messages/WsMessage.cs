using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public record DataPayload<T>(T Data);
    
    public abstract record WsMessage {
        public string Id { get; init; }
        public string Type { get; init; }
        public string Device { get; init; }
        public string? Context { get; init; }
        public JsonElement? Payload { get; init; }
        public long Timestamp { get; init; }
        
        private static readonly JsonSerializerOptions JsonOptions = new() {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        protected WsMessage(
            string type,
            string device,
            string? context = null,
            JsonElement? payload = null,
            string? id = null,
            long? timestamp = null
        ) {
            Id = id ?? GenerateId();
            Type = type;
            Device = device;
            Context = context;
            Payload = payload;
            Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        
        private static string GenerateId()
            => Guid.NewGuid().ToString("N");
        
        protected static JsonElement ToJsonElement<T>(T value) {
            return JsonSerializer.SerializeToElement(value, JsonOptions);
        }

        /// <summary>
        /// WhenWritingNull drops nulls before the filter sees them, so clearing a setting to null sends
        /// nothing. A nullable setting would need its own handling.
        /// </summary>
        protected static JsonElement ToFilteredDataPayload<T>(T value, IReadOnlyCollection<string> onlyKeys) {
            JsonObject data = JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject ?? new JsonObject();
            List<string> keys = new List<string>(data.Count);
            foreach (KeyValuePair<string, JsonNode?> entry in data) {
                keys.Add(entry.Key);
            }
            foreach (string key in keys) {
                if (!onlyKeys.Contains(key)) data.Remove(key);
            }

            JsonObject envelope = new JsonObject { ["data"] = data };
            return JsonSerializer.SerializeToElement(envelope);
        }
    }

}

