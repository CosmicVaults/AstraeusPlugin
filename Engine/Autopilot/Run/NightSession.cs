using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// One night, from StartSessionAsync at dusk until its end is reported. Its id ties the start and end
    /// emails together, its flags are the facts a night accumulates, and a fresh one each dusk is what
    /// resets them. The Context outlives the night, so without that the first fault would be re-reported
    /// every night after, and Tuesday's secure would still count on Friday.
    /// </summary>
    internal sealed class NightSession(Context context)
    {
        private int _hasReportedEnd;

        /// <summary>Shared by the start and end emails, so the server can pair them.</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// When the night began, or null for the placeholder before the first dusk. A start time, not a
        /// stopwatch, because the email says when the night began, in a timezone the server knows and
        /// we may not.
        /// </summary>
        public DateTime? StartedUtc { get; private set; }

        /// <summary>True from the started email until the end is reported.</summary>
        public bool IsOpen => StartedUtc is not null && Volatile.Read(ref _hasReportedEnd) == 0;

        /// <summary>
        /// Why a fault ended this night, or null if it was just switched off. The teardown reads it to
        /// pick the "stopped" or "failed" email, so a fault is reported once, with its cause.
        /// </summary>
        public string? EndReason { get; private set; }

        /// <summary>The first cause recorded wins, since it is the one that ended the night.</summary>
        public void RecordEndReason(string? reason) => EndReason ??= reason;

        /// <summary>
        /// Whether this night's wind-down parked the mount, and closed the roof if we operate it. False
        /// on a deliberate stop, which leaves things as found since that's usually someone taking manual
        /// control. The session-end email warns about it.
        /// </summary>
        public bool IsSecured { get; set; }

        public DateTime? SecuredAtUtc { get; set; }

        /// <summary>Whether the dawn disconnect let go of the mount while it read parked.</summary>
        public bool HasDisconnectedParkedMount { get; set; }

        /// <summary>
        /// Whether tonight's first equipment connect is done. Alerts and reconnects wait for it, since
        /// before then a disconnected device usually means the mount wasn't powered on yet.
        /// </summary>
        public bool IsStartupComplete { get; set; }

        /// <summary>Whether tonight's log already explained N.I.N.A.'s dome-geometry failure after a centring slew.</summary>
        public bool HasWarnedDomeGeometry { get; set; }

        public bool HasNotedCloudUploadOff { get; set; }

        /// <summary>
        /// Star counts of the latest frames in a row that had almost no stars and failed their plate
        /// solve, oldest first. Any frame that doesn't empties it.
        /// </summary>
        public List<int> DarkFrameStarCounts { get; } = new();

        public bool HasReportedDarkFrames { get; set; }

        /// <summary>How centring has gone on the target in hand. A new night starts it clean.</summary>
        public CentringTracker Centring { get; } = new CentringTracker(context);

        /// <summary>Opens the night and sends the started email.</summary>
        public async Task StartAsync()
        {
            StartedUtc = context.UtcNow;
            // Awaited, unlike other alerts. Nothing waits on this task, and it cheaply keeps "started"
            // ahead of a fast "failed", since the server orders by arrival.
            await context.Observatory.ReportEventAsync(NotificationKind.AutopilotStarted,
                "Autopilot session started.", BuildStartDetails(), $"{Id}-start");
        }

        /// <summary>
        /// The settings an observer wants confirmed at dusk. The session id goes in client_ref, since it
        /// means nothing to the person reading the email.
        /// </summary>
        private object BuildStartDetails() => new
        {
            roof_mode = context.RoofMode.ToString(),
            operating_window = context.OperatingWindow.ToString(),
            auto_connect_equipment = context.ShouldAutoConnectEquipment ? "yes" : "no",
            camera_cooling = context.IsCameraCoolingEnabled ? $"yes, {context.CameraCoolingTemperature:F1}°C" : "no",
            upload_to_cloud = context.IsCloudUploadEnabled ? "yes" : "no",
        };

        /// <summary>
        /// Closes the night and reports "failed" if a fault ended it, otherwise "stopped". EndSession at
        /// dawn and the machine teardown both call it, and the atomic take means only the first reports.
        /// A fault is always reported, even with no night open.
        /// </summary>
        public void ReportEnd(string? faultReason = null, string? stopReason = null)
        {
            bool wasOpen = StartedUtc is not null && Interlocked.Exchange(ref _hasReportedEnd, 1) == 0;
            string? reason = faultReason ?? EndReason;
            if (!wasOpen && reason == null) return;

            // Sent here because the session is closed by the time Observatory adds its rig snapshot.
            // Null keys are dropped, so a fault with no night behind it shows no session length.
            string? sessionStartedAt = wasOpen
                ? StartedUtc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : null;
            // Read now, since the roof may have been opened again after the last secure, and what
            // matters is where it is when the email goes.
            bool isRoofOpen = context.Dome is { IsConnected: true } dome && !dome.IsShutterClosed();
            var details = new
            {
                session_started_at = sessionStartedAt,
                reason = reason ?? stopReason ?? "switched off",
                // Drives the "mount is not parked, roof may still be open" warning in the email.
                secured = context.IsStillSecured ? "yes" : "no",
                roof = DescribeRoofNow(),
            };
            // A deliberate stop leaves the observatory as found. If the roof is open the first line says
            // so, because "stopped" alone reads as routine and this is what the observer must act on.
            string message = reason ?? (isRoofOpen
                ? "Autopilot session stopped. The roof is still open."
                : "Autopilot session stopped.");
            context.Observatory.ReportEvent(
                reason == null ? NotificationKind.AutopilotStopped : NotificationKind.AutopilotFailed,
                message, details,
                $"{(wasOpen ? Id : Guid.NewGuid().ToString("N"))}-end");
        }

        private string DescribeRoofNow()
        {
            string state;
            if (context.Dome is not { IsConnected: true } dome)
            {
                state = "unknown (dome not connected)";
            }
            else if (dome.IsShutterClosed())
            {
                state = "closed";
            }
            else
            {
                state = "open";
            }
            return context.RoofMode == RoofMode.Operate ? state : $"{state} (not operated by the autopilot)";
        }
    }
}
