namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    public class AutopilotNextResponse {
        public int?    ImageId                  { get; set; }
        public int?    ProjectId                { get; set; }
        public string? ProjectName              { get; set; }
        public string? TargetName               { get; set; }
        public double? Ra                        { get; set; }
        public double? Dec                       { get; set; }
        public double? PositionAngle             { get; set; }
        public string? TrackingType              { get; set; }
        public string? FilterName                { get; set; }
        public double? Exposure                  { get; set; }
        public short?  Binning                   { get; set; }

        /// <summary>Null uses the camera's current/profile gain.</summary>
        public int?    Gain                      { get; set; }

        /// <summary>Null uses the camera's current/profile offset.</summary>
        public int?    Offset                    { get; set; }

        /// <summary>The server may also send a number, ACP style, with -1 for auto and 0 for off.</summary>
        public bool?   ShouldDither              { get; set; }

        /// <summary>
        /// Frames delivered so far and frames wanted for the project. The server counts deliveries, so
        /// it keeps offering the same image until CapturesComplete reaches CapturesTotal.
        /// </summary>
        public int?    CapturesComplete          { get; set; }
        public int?    CapturesTotal             { get; set; }
        public int?    RetryAfter                { get; set; }

        /// <summary>
        /// Why no image was handed out, as a server slug. Only logged, and we sleep RetryAfter whatever
        /// it says, so new slugs need no change here.
        /// </summary>
        public string? Reason                    { get; set; }

        ///////////// Meridian flip /////////////

        /// <summary>
        /// The pier side this frame must be taken on, "pierEast" or "pierWest", or null to let us decide.
        /// The server guarantees the exposure plus setup fits that side's safe range, so if our check
        /// before the exposure disagrees, the two geometries have diverged and we report a fault.
        /// </summary>
        public string? RequiredPierSide          { get; set; }
    }
}