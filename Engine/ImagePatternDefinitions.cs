using NINA.Core.Model;

namespace CosmicVaults.NINA.Astraeus.Engine {
    /// <summary>
    /// The plugin's $$...$$ image file name patterns, shown in NINA's pattern picker and filled into
    /// saved file names and FITS keywords. AstraeusSettings.SetImagePatterns registers them without a
    /// value, and the camera's BeforeFinalizeImageSaved handler builds the same ones with the live value.
    /// </summary>
    internal static class ImagePatternDefinitions {
        public const string ProjectNameKey = "$$ASTRAEUSPROJECTNAME$$";
        public const string TargetNameKey = "$$ASTRAEUSTARGETNAME$$";

        public const string ProjectNameDescription = "The active project name if available";
        public const string TargetNameDescription = "The active target name if available";

        public const string Category = "Astraeus Autopilot";

        public static ImagePattern CreateProjectNamePattern(string? value = null) =>
            new ImagePattern(ProjectNameKey, ProjectNameDescription, Category) { Value = value };

        public static ImagePattern CreateTargetNamePattern(string? value = null) =>
            new ImagePattern(TargetNameKey, TargetNameDescription, Category) { Value = value };
    }
}
