using System;
using NINA.Core.Enum;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// The frame in hand: the target the server assigned and everything the run learns about it before the
    /// report. PollForTarget replaces it whole, so nothing from one frame can leak into the next, and the
    /// report releases the target once the frame is done.
    /// </summary>
    internal sealed class Frame
    {
        private readonly Context _context;

        public Frame(Context context, AutopilotNextResponse? target)
        {
            _context = context;
            Target = target;
            AssignedAtUtc = context.UtcNow;
        }

        /// <summary>The server's assignment, or null between frames and once the frame is reported.</summary>
        public AutopilotNextResponse? Target { get; private set; }

        /// <summary>When the target was assigned. The server judged its window from then.</summary>
        public DateTime AssignedAtUtc { get; }

        /// <summary>
        /// Setup has outrun the margin the server allowed for, so a limit refusal now is a timing miss for
        /// the server to re-judge, not a fault for the retry ladder.
        /// </summary>
        public bool HasServerWindowExpired =>
            (_context.UtcNow - AssignedAtUtc).TotalSeconds > AutopilotConstants.LimitLookAheadMarginSeconds;

        /// <summary>
        /// The pier side the server pinned for this frame, or null when the choice is ours. A value we
        /// can't parse reads as null, since we shouldn't act on an orientation we don't understand.
        /// </summary>
        public PierSide? RequiredPierSide => Target?.RequiredPierSide switch
        {
            "pierEast" => PierSide.pierEast,
            "pierWest" => PierSide.pierWest,
            _ => null,
        };

        /// <summary>
        /// True once the image is marked started on the server (/start/) and not yet reported, so the abort
        /// path fails it instead of leaving it stuck.
        /// </summary>
        public bool IsStarted { get; set; }

        /// <summary>
        /// Idempotency key for the exposure, made when the image is marked started. Every report for it
        /// sends the same ref, so the server sees a retried call as the same exposure.
        /// </summary>
        public string? ClientRef { get; set; }

        /// <summary>When the committed exposure window ends. Null until the image is marked started.</summary>
        public DateTime? WindowEndsAtUtc { get; set; }

        /// <summary>
        /// True once a meridian flip has finished for this frame. One exposure crosses the meridian once,
        /// so a second request means something disagrees, and flipping again would swing the mount back
        /// and forth until dawn. RunMeridianFlip fails the frame instead.
        /// </summary>
        public bool HasFlipped { get; set; }

        public bool HasFilterChanged { get; set; }

        /// <summary>
        /// True when CheckTargetChanged took the fast path because the mount is already on this field and
        /// nothing has moved since the last frame. That marks the frame as a repeat, and so a dither point.
        /// </summary>
        public bool HasSkippedSlew { get; set; }

        /// <summary>
        /// True when the frame must be plate solved instead of trusting where the mount says it points.
        /// Set after a meridian flip, since the mount reports the target to arcseconds whether or not
        /// N.I.N.A.'s re-centre ran, and a field rotated 180 degrees gives the same numbers.
        /// </summary>
        public bool IsRecenterRequired { get; set; }

        public CaptureResult? LastCapture { get; set; }

        /// <summary>
        /// The latest Smart Autofocus status, or null when it's off or the server was unreachable. Set by
        /// IsAutofocusRequired and reused by RunAutofocus and ApplyRecommendedFocus, so one server call
        /// serves the frame.
        /// </summary>
        public AutofocusStatusResponse? LastAutofocusStatus { get; set; }

        /// <summary>
        /// Lets the target go once the frame is reported or refused, so the dashboard stops showing it, and
        /// forgets the started image and its capture with it. The rest stands until PollForTarget replaces
        /// the frame.
        /// </summary>
        public void Release()
        {
            bool hadTarget = Target is not null;
            Target = null;
            LastCapture = null;
            IsStarted = false;
            ClientRef = null;
            if (hadTarget) _context.RaiseStatusChanged();
        }
    }
}
