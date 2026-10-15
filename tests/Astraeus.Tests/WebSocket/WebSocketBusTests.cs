using System.Collections.Generic;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.WebSocket {
    [TestFixture]
    public class WebSocketBusTests {
        // While the socket is down, one-off messages are held and sent late: notifications, command replies, device events and
        // settings. Anything the next tick replaces is dropped, so a reconnect doesn't replay an hour of telemetry.
        [Test]
        public void OneOffMessagesAreWorthReplayingAndTelemetryIsNot() {
            Assert.Multiple(() => {
                Assert.That(WebSocketBus.IsWorthReplaying(new WsNotification("mount", "equipment", "Parked.")), Is.True, "notification");
                Assert.That(WebSocketBus.IsWorthReplaying(new WsResponse("mount", "1", "received", new { status = "accepted" })), Is.True, "command reply");
                Assert.That(WebSocketBus.IsWorthReplaying(new DeviceConnectionUpdate("mount", "id", "Mount", true)), Is.True, "device connection");
                Assert.That(WebSocketBus.IsWorthReplaying(SettingsUpdate.Create(new { }, "autopilot")), Is.True, "settings");
                Assert.That(WebSocketBus.IsWorthReplaying(new DashboardUpdate(new Dictionary<string, object>())), Is.False, "telemetry tick");
                Assert.That(WebSocketBus.IsWorthReplaying(new NinaConnectionUpdate(true)), Is.False, "N.I.N.A. connection");
                Assert.That(WebSocketBus.IsWorthReplaying(new FeedFrameMessage(new FeedFramePayload())), Is.False, "feed frame");
            });
        }
    }
}