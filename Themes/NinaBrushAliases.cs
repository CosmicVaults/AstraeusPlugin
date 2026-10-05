using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CosmicVaults.NINA.Astraeus.Themes {

    /// <summary>
    /// Maps every Ast.* colour and font key onto its N.I.N.A. equivalent. Built in code so it hands back
    /// N.I.N.A.'s own brush instances and the page follows a schema change. A brush declared in a
    /// standalone ResourceDictionary has no inheritance context for a binding or DynamicResource.
    /// </summary>
    internal static class NinaBrushAliases {

        /// <summary>The N.I.N.A. keys come from NINA.WPF.Base/Resources/StaticResources/Brushes.xaml.</summary>
        private static readonly Dictionary<string, string> Aliases = new() {
            // Text. N.I.N.A. has no dim tier, and thinning PrimaryBrush to fake one could be unreadable
            // on a schema we don't control, so all three use the colour of N.I.N.A.'s own labels.
            ["Ast.Text"] = "PrimaryBrush",
            ["Ast.TextDim"] = "PrimaryBrush",
            ["Ast.TextFaint"] = "PrimaryBrush",

            // Surfaces, outermost inwards. N.I.N.A.'s three background tones line up with ours.
            ["Ast.Bg"] = "BackgroundBrush",
            ["Ast.BgPage"] = "BackgroundBrush",
            ["Ast.BgCard"] = "SecondaryBackgroundBrush",
            ["Ast.BgRaised"] = "SecondaryBackgroundBrush",
            ["Ast.BgInset"] = "TertiaryBackgroundBrush",

            // Ast.Ink is the brand's near-black outline and Ast.Divider its hairline. On a schema that
            // may be light, both use N.I.N.A.'s edge colour.
            ["Ast.Ink"] = "BorderBrush",
            ["Ast.Divider"] = "BorderBrush",
            ["Ast.OnAccent"] = "ButtonForegroundBrush",

            // Status. Warning and info have N.I.N.A. counterparts. Ast.Safe doesn't and is handled in Build.
            ["Ast.Warning"] = "NotificationWarningBrush",
            ["Ast.Info"] = "SecondaryBrush",

            // Brand accents don't belong on a N.I.N.A.-native page, so gold and violet map to N.I.N.A.'s
            // own accents, and the gradients are flattened with them. NinaTheme.xaml doesn't use the
            // gradients. They're aliased so the two dictionaries keep the same keys.
            ["Ast.Gold"] = "ButtonBackgroundSelectedBrush",
            ["Ast.GoldGrad"] = "ButtonBackgroundSelectedBrush",
            ["Ast.Violet"] = "SecondaryBrush",
            ["Ast.DangerGrad"] = "NotificationErrorBrush",
        };

        /// <summary>
        /// The signed-in dot in LoginView. N.I.N.A.'s schema has no green, and a status light that can
        /// only be red or grey is no use, so this one brand colour stays.
        /// </summary>
        private static readonly SolidColorBrush Safe = Freeze(Color.FromRgb(0x74, 0xD8, 0x9B));

        internal static ResourceDictionary Build() {
            ResourceDictionary dictionary = new ResourceDictionary();

            foreach (KeyValuePair<string, string> alias in Aliases) {
                dictionary[alias.Key] = Find(alias.Value);
            }
            dictionary["Ast.Safe"] = Safe;

            // N.I.N.A. sets no FontFamily (StandardTextBlock only sets Foreground), so its UI uses the
            // shell font. Matching it stops the page looking like ours.
            FontFamily font = SystemFonts.MessageFontFamily;
            dictionary["Ast.Display"] = font;
            dictionary["Ast.Body"] = font;
            dictionary["Ast.Mono"] = font;

            return dictionary;
        }

        /// <summary>
        /// Throws instead of guessing. A missing key means N.I.N.A. moved its brushes, and AstraeusTheme
        /// then falls back to the whole brand look instead of a page half in colours nobody chose.
        /// </summary>
        private static Brush Find(string ninaKey) {
            if (Application.Current?.TryFindResource(ninaKey) is Brush brush) { return brush; }
            throw new InvalidOperationException(
                $"N.I.N.A. brush '{ninaKey}' is not in Application.Current.Resources");
        }

        private static SolidColorBrush Freeze(Color color) {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
