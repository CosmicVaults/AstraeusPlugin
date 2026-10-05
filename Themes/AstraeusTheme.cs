using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Windows;

namespace CosmicVaults.NINA.Astraeus.Themes {

    /// <summary>
    /// Holds BrandTheme.xaml or NinaTheme.xaml by AstraeusThemeMode, for the options views to merge. Not
    /// exported, since N.I.N.A. merges every exported dictionary into the app's resources and the brand's
    /// implicit styles would spread over the whole app.
    /// </summary>
    public class AstraeusTheme : ResourceDictionary {

        private const string BrandSource = "Themes/BrandTheme.xaml";
        private const string NinaSource = "Themes/NinaTheme.xaml";

        // Parsed once and shared, like N.I.N.A.'s SharedResourceDictionary. Otherwise the page re-parses
        // a 700-line dictionary five times on load and five more on every toggle.
        private static readonly Dictionary<string, ResourceDictionary> Cache = new();

        public AstraeusTheme() {
            Apply();
            AstraeusThemeMode.Subscribe(this);
        }

        /// <summary>
        /// It runs again when the setting changes, and the toggle lands without reopening the page since
        /// the views use DynamicResource for every Ast.* key.
        /// </summary>
        internal void Apply() {
            List<ResourceDictionary> dictionariesToMerge = new List<ResourceDictionary>();

            if (AstraeusThemeMode.IsNinaThemeEnabled) {
                try {
                    dictionariesToMerge.Add(NinaBrushAliases.Build());
                    dictionariesToMerge.Add(Load(NinaSource));
                } catch (Exception ex) {
                    // Every Ast.* key has to resolve or the page throws on load, so a broken N.I.N.A.
                    // theme falls back to the brand look instead of taking the options page down.
                    Logger.Error($"[Astraeus] Could not load the N.I.N.A. theme, keeping the Astraeus look: {ex}");
                    dictionariesToMerge.Clear();
                }
            }

            if (dictionariesToMerge.Count == 0) { dictionariesToMerge.Add(Load(BrandSource)); }

            MergedDictionaries.Clear();
            foreach (ResourceDictionary dictionary in dictionariesToMerge) { MergedDictionaries.Add(dictionary); }
        }

        private static ResourceDictionary Load(string relativePath) {
            if (Cache.TryGetValue(relativePath, out ResourceDictionary? cached)) { return cached; }

            // An absolute pack URI, since a dictionary built in code has no base URI for a relative one.
            ResourceDictionary dictionary = new ResourceDictionary {
                Source = new Uri($"pack://application:,,,/CosmicVaults.NINA.Astraeus;component/{relativePath}",
                                 UriKind.Absolute)
            };
            // Setting Source only parses the top level, so the styles would first resolve (and fail)
            // deep inside a view's load. Touching one style whose BasedOn is in another file inflates
            // them now, so a problem surfaces here inside the caller's try/catch.
            _ = dictionary["Ast.ButtonNeutral"];

            Cache[relativePath] = dictionary;
            return dictionary;
        }
    }
}
