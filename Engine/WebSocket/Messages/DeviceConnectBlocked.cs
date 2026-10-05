namespace CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages {
    /// <summary>
    /// A connect is waiting on a modal only someone at the N.I.N.A. machine can answer, usually the
    /// site-settings sync prompt. Sent on each change, not per poll, so the dashboard can hold its own
    /// timeout open and say what it's waiting for.
    /// </summary>
    public sealed record DeviceConnectBlocked : WsMessage {
        public DeviceConnectBlocked(string device, bool isBlocked, string? title)
            : base(
                type: "update",
                device: device,
                context: "connect_blocked",
                payload: ToJsonElement(new { blocked = isBlocked, title })
            ) { }
    }
}
