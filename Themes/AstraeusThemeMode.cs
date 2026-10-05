using CosmicVaults.NINA.Astraeus.Engine;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace CosmicVaults.NINA.Astraeus.Themes {

    /// <summary>
    /// Which look the options views use. Static because XAML builds AstraeusTheme through a parameterless
    /// constructor, so it can't be handed the settings. Until Bind, or if the read fails, the brand look wins.
    /// </summary>
    public static class AstraeusThemeMode {

        private static AstraeusSettings? _settings;

        // The options page builds five AstraeusTheme instances each time it opens. Weak references let a
        // closed page's ones be collected instead of living as long as the process.
        private static readonly List<WeakReference<AstraeusTheme>> _themes = new();

        public static void Bind(AstraeusSettings settings) {
            _settings = settings;
            Raise();
        }

        /// <summary>
        /// Read from the store on every call so the active N.I.N.A. profile always decides, like the
        /// plugin's other settings.
        /// </summary>
        public static bool IsNinaThemeEnabled => _settings?.IsNinaThemeEnabled() ?? false;

        internal static void Subscribe(AstraeusTheme theme) {
            lock (_themes) {
                Prune();
                _themes.Add(new WeakReference<AstraeusTheme>(theme));
            }
        }

        /// <summary>
        /// Re-applies the current mode to every live dictionary. Call after the stored value changes,
        /// from the options toggle or on a profile switch, since the setting is per profile.
        /// </summary>
        public static void Raise() {
            List<AstraeusTheme> liveThemes = new List<AstraeusTheme>();
            lock (_themes) {
                Prune();
                foreach (WeakReference<AstraeusTheme> weakTheme in _themes) {
                    if (weakTheme.TryGetTarget(out AstraeusTheme? theme)) { liveThemes.Add(theme); }
                }
            }
            if (liveThemes.Count == 0) { return; }

            // A ResourceDictionary in use can only be touched on the UI thread, and the profile switch
            // that calls us doesn't arrive on it.
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) { return; }
            dispatcher.BeginInvoke(new Action(() => {
                foreach (AstraeusTheme theme in liveThemes) {
                    try {
                        theme.Apply();
                    } catch (Exception ex) {
                        Logger.Warning($"[Astraeus] Failed to re-apply the options page theme: {ex.Message}");
                    }
                }
            }));
        }

        private static void Prune() {
            _themes.RemoveAll(weakTheme => !weakTheme.TryGetTarget(out _));
        }
    }
}
