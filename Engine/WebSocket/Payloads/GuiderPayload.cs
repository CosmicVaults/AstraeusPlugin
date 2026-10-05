using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads {
    public sealed record GuiderUpdate : WsMessage {
        public GuiderUpdate(GuiderPayload payload, string? context = null)
            : base(
                type: "update",
                device: "guider",
                context: context,
                payload: ToJsonElement(new DataPayload<GuiderPayload>(payload))
            ) { }
    }

    public record GuiderPayload : DevicePayload {
        [JsonPropertyName("can_clear_calibration")]
        public bool? CanClearCalibration { get; init; }

        [JsonPropertyName("can_shift_rate")]
        public bool? CanShiftRate { get; init; }

        [JsonPropertyName("can_get_lock_position")]
        public bool? CanGetLockPosition { get; init; }
    }
}