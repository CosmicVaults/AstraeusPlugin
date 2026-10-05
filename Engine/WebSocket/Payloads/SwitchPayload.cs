using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {

    public sealed record SwitchUpdate : WsMessage {
        public SwitchUpdate(SwitchPayload payload, string? context = null)
            : base(
                type: "update",
                device: "switch",
                context: context,
                payload: ToJsonElement(new DataPayload<SwitchPayload>(payload))
            ) { }
    }

    public record SwitchPayload : DevicePayload {
        public IReadOnlyList<SwitchItem>? Switches { get; init; }
    }

    /// <summary>
    /// One port, sent in full by the static-info frame and as id and value by the periodic one. Id is the
    /// only stable key since drivers let names change, and it's non-nullable so every frame carries it.
    /// </summary>
    public record SwitchItem {
        public short Id { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public double? Value { get; init; }

        [JsonPropertyName("is_writable")]
        public bool? IsWritable { get; init; }

        /// <summary>
        /// A 0/1 switch with a step of 1, drawn as a toggle. N.I.N.A.'s SwitchTemplateSelector uses the same
        /// test, so the two UIs agree.
        /// </summary>
        [JsonPropertyName("is_boolean")]
        public bool? IsBoolean { get; init; }

        public double? Minimum { get; init; }
        public double? Maximum { get; init; }

        [JsonPropertyName("step_size")]
        public double? StepSize { get; init; }
    }
}
