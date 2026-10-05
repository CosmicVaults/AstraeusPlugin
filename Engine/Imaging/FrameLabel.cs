using System;
using System.Threading;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// What N.I.N.A.'s current exposures are for while one of our routines (centre, solve, autofocus,
    /// flip) has handed it the camera. N.I.N.A. takes them inside its own solver and autofocus and
    /// only labels them "snapshot", so the routine sets the label here and the camera's preview relay
    /// reads it. A frame with no label is one N.I.N.A. took by itself, and isn't relayed.
    /// </summary>
    public sealed class FrameLabel {
        private string? _current;

        /// <summary>The label for exposures taken now, or null outside any of the plugin's routines.</summary>
        public string? Current => Volatile.Read(ref _current);

        /// <summary>
        /// Labels exposures until the scope is disposed, then restores the previous label so a nested
        /// routine hands the outer one back.
        /// </summary>
        public IDisposable Begin(string source) {
            string? previous = Interlocked.Exchange(ref _current, source);
            return new Scope(this, previous);
        }

        private sealed class Scope(FrameLabel owner, string? previous) : IDisposable {
            private int _disposed;

            public void Dispose() {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Volatile.Write(ref owner._current, previous);
            }
        }
    }
}
