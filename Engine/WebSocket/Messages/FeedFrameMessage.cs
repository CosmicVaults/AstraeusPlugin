using System;
using System.Text.Json.Serialization;

namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    /// <summary>
    /// Reuses ImagePreviewMessage's envelope, since binary frames would need a second transport on the
    /// server to save a couple of KB/s per viewer. Frames aren't stored, so a webcam pointed at someone's
    /// garden leaves no trail on the server.
    /// </summary>
    public sealed record FeedFrameMessage : WsMessage {
        public FeedFrameMessage(FeedFramePayload payload)
            : base(
                type: "preview",
                device: "feed",
                context: "feed_frame",
                payload: ToJsonElement(new DataPayload<FeedFramePayload>(payload))
            ) { }
    }

    /// <summary>
    /// Wire shape of a feed frame. A failed grab is sent the same way with IsSuccess false and no
    /// image, so the dashboard can show why the panel is empty instead of going stale.
    /// </summary>
    public sealed record FeedFramePayload {
        [JsonPropertyName("success")]     public bool    IsSuccess { get; init; }
        [JsonPropertyName("fail_reason")] public string? FailReason { get; init; }

        /// <summary>Base64 JPEG. Absent on failure.</summary>
        [JsonPropertyName("image")]       public string? Image { get; init; }
        [JsonPropertyName("format")]      public string? Format { get; init; }
        [JsonPropertyName("width")]       public int?    Width { get; init; }
        [JsonPropertyName("height")]      public int?    Height { get; init; }

        /// <summary>ISO-8601 UTC.</summary>
        [JsonPropertyName("captured_at")] public string? CapturedAt { get; init; }

        public static FeedFramePayload FromFrame(byte[] jpeg, int width, int height, DateTime capturedUtc) =>
            new() {
                IsSuccess = true,
                Image = Convert.ToBase64String(jpeg),
                Format = "jpeg",
                Width = width,
                Height = height,
                CapturedAt = capturedUtc.ToString("o")
            };

        /// <summary>
        /// A frame that never arrived. The reason is shown to the user, so callers must strip
        /// credentials from it first (see FeedUrl).
        /// </summary>
        public static FeedFramePayload Failure(string reason) =>
            new() {
                IsSuccess = false,
                FailReason = reason,
                CapturedAt = DateTime.UtcNow.ToString("o")
            };
    }
}
