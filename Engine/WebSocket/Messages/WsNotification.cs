namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    public sealed record WsNotification : WsMessage {
        public WsNotification(
            string device,
            string category,
            string message,
            bool shouldTimeout = true,
            bool shouldClear = false,
            bool isToast = false,
            string severity = "INFO"
        ) : base(
            type: "notification",
            device: device,
            context: category,
            payload: ToJsonElement(new {
                category = category,
                message = message,
                severity = severity,
                timeout = shouldTimeout,
                clear = shouldClear,
                isToast = isToast
            })
        ) { }
    }
}