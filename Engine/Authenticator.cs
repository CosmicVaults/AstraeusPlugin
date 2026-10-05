using CosmicVaults.NINA.Astraeus.Engine.Auth;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Authenticator : INotifyPropertyChanged {

        // How far ahead of expiry we start refreshing. If refreshes fail, it's how long the run can
        // keep using the current token while we retry. An hour of an 8h token costs nothing, since the
        // server issues a full 8h replacement anyway, and gives about a dozen attempts at the backoff
        // below.
        private static readonly TimeSpan RefreshSkew = TimeSpan.FromHours(1);

        // A failed restore keeps retrying. NINA often starts before Wi-Fi or the VPN is up, and a rig
        // that needs signing in by hand has lost the night. The backoff stops the 2s tick turning a
        // server outage into a retry storm.
        private static readonly TimeSpan[] RestoreBackoff = {
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
        };

        private const int DefaultTokenLifetimeSeconds = 3600;

        /// <summary>
        /// Only an explicit rejection deletes the credential. A 502 from a restarting server or a proxy
        /// error page at 3am must leave a working token in place.
        /// </summary>
        private enum ExchangeOutcome { Success, Retryable, CredentialRejected }

        private readonly AstraeusWebClient _webClient;
        private readonly AstraeusSettings _settings;
        private readonly Observatory _observatory;
        private readonly PkceLoginFlow _loginFlow;

        // Tokens are read from the WS, imaging and UI threads, and any of them can trigger a refresh.
        // Without this gate the 2s tick would stampede the token endpoint, and with rotation on each
        // refresh would invalidate the others.
        private readonly SemaphoreSlim _refreshGate = new(1, 1);

        private string? _accessToken;
        private DateTime _expiresAtUtc = DateTime.MinValue;

        private int _restoreAttempt;
        private DateTime _nextRestoreUtc = DateTime.MinValue;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isAuthenticated;
        public bool IsAuthenticated {
            get => _isAuthenticated;
            private set { if (value != _isAuthenticated) { _isAuthenticated = value; OnPropertyChanged(); } }
        }

        private bool _isSigningIn;
        public bool IsSigningIn {
            get => _isSigningIn;
            private set { if (value != _isSigningIn) { _isSigningIn = value; OnPropertyChanged(); } }
        }

        public string? SignedInAs => _settings.GetUserEmail();

        private const string LogDevice = "webclient";
        private void Log(string message)        => _observatory.Log(message, LogDevice, LogCategory.Network);
        private void LogWarning(string message) => _observatory.LogWarning(message, LogDevice, LogCategory.Network);
        private void LogError(string message)   => _observatory.LogError(message, LogDevice, LogCategory.Network);

        public Authenticator(AstraeusWebClient webClient, AstraeusSettings settings, Observatory observatory) {
            _webClient = webClient;
            _settings = settings;
            _observatory = observatory;
            _loginFlow = new PkceLoginFlow(observatory);
        }

        public async Task Awake() {
            if (string.IsNullOrEmpty(_settings.GetRefreshToken())) {
                IsAuthenticated = false;
                Log("No stored sign-in; browser sign-in required.");
                return;
            }
            await TryRestoreSessionAsync();
        }

        /// <summary>
        /// Tries at most once per backoff interval, so it's safe to call from the plugin tick. Once the server
        /// has rejected the credential nothing is stored, and only a browser sign-in can recover.
        /// </summary>
        public async Task TryRestoreSessionAsync() {
            if (IsAuthenticated || IsSigningIn) return;
            if (string.IsNullOrEmpty(_settings.GetRefreshToken())) return;
            if (DateTime.UtcNow < _nextRestoreUtc) return;

            await _refreshGate.WaitAsync();
            try {
                // Check again in case another caller restored while we queued.
                if (IsAuthenticated || IsSigningIn) return;
                if (DateTime.UtcNow < _nextRestoreUtc) return;

                // Re-read inside the gate, since a concurrent exchange may have rotated it.
                string? refreshToken = _settings.GetRefreshToken();
                if (string.IsNullOrEmpty(refreshToken)) return;

                ExchangeOutcome outcome = await RefreshAsync(refreshToken!);
                IsAuthenticated = outcome == ExchangeOutcome.Success;

                if (outcome == ExchangeOutcome.Success) {
                    ResetRestoreBackoff();
                    Log("Signed-in session restored.");
                } else if (outcome == ExchangeOutcome.Retryable) {
                    ScheduleNextRestore();
                }
                // CredentialRejected deleted the token, so the guard above ends the retries. Only an
                // interactive sign-in can recover.
            } finally {
                _refreshGate.Release();
            }
        }

        /// <summary>Opens the system browser.</summary>
        public async Task<bool> SignInAsync(CancellationToken cancellationToken) {
            if (IsSigningIn) return false;
            IsSigningIn = true;
            try {
                AuthorizationCode? authorization = await _loginFlow.RequestCodeAsync(cancellationToken);
                if (authorization == null) {
                    IsAuthenticated = false;
                    return false;
                }

                // Under the gate like every other exchange, so a refresh still in flight cannot land
                // on top of the new session. Not held while the browser is open.
                ExchangeOutcome outcome;
                await _refreshGate.WaitAsync(cancellationToken);
                try {
                    outcome = await ExchangeAsync(new Dictionary<string, string> {
                        ["grant_type"]    = "authorization_code",
                        ["code"]          = authorization.Code,
                        ["redirect_uri"]  = authorization.RedirectUri,
                        ["client_id"]     = ServerEndpoints.OAuthClientId,
                        ["code_verifier"] = authorization.Verifier,
                    });
                } finally {
                    _refreshGate.Release();
                }

                IsAuthenticated = outcome == ExchangeOutcome.Success;
                if (IsAuthenticated) {
                    ResetRestoreBackoff();
                    await FetchAccountEmailAsync();
                }
                return IsAuthenticated;

            } catch (Exception ex) {
                LogError($"Sign-in failed: {ex.Message}");
                IsAuthenticated = false;
                return false;
            } finally {
                IsSigningIn = false;
            }
        }

        /// <summary>Null means the caller must not make the request.</summary>
        public async Task<string?> GetAccessTokenAsync() {
            if (IsFresh()) return _accessToken;

            await _refreshGate.WaitAsync();
            try {
                // Check again in case another caller refreshed while we queued.
                if (IsFresh()) return _accessToken;

                // A refresh already failed and the old token is still valid, so wait out the backoff.
                if (DateTime.UtcNow < _nextRestoreUtc && IsUsable()) return _accessToken;

                string? refreshToken = _settings.GetRefreshToken();
                if (string.IsNullOrEmpty(refreshToken)) {
                    IsAuthenticated = false;
                    return null;
                }

                ExchangeOutcome outcome = await RefreshAsync(refreshToken!);
                if (outcome == ExchangeOutcome.Success) {
                    IsAuthenticated = true;
                    ResetRestoreBackoff();
                    return _accessToken;
                }

                if (outcome == ExchangeOutcome.Retryable) {
                    ScheduleNextRestore();

                    // The old token is only in the early-refresh window and still valid, so keep
                    // using it. Signing out would make Observatory.Update() close the websocket and
                    // stop updating components, and a brief outage shouldn't end a run that way.
                    if (IsUsable()) return _accessToken;
                }

                IsAuthenticated = false;
                return null;
            } finally {
                _refreshGate.Release();
            }
        }

        /// <summary>
        /// Renews now so the night runs on a full-length token. A failed renewal still returns true while
        /// the current token is valid, since that's no reason to cancel the night.
        /// </summary>
        public async Task<bool> ForceRefreshAsync() {
            await _refreshGate.WaitAsync();
            try {
                string? refreshToken = _settings.GetRefreshToken();
                if (string.IsNullOrEmpty(refreshToken)) {
                    IsAuthenticated = false;
                    return false;
                }

                ExchangeOutcome outcome = await RefreshAsync(refreshToken!);
                if (outcome == ExchangeOutcome.Success) {
                    IsAuthenticated = true;
                    ResetRestoreBackoff();
                    Log("Session renewed for tonight.");
                    return true;
                }

                if (outcome == ExchangeOutcome.Retryable) {
                    ScheduleNextRestore();
                    if (IsUsable()) {
                        LogWarning("Could not renew the session; continuing on the current token.");
                        return true;
                    }
                }

                IsAuthenticated = false;
                return false;
            } finally {
                _refreshGate.Release();
            }
        }

        /// <summary>
        /// Drops the in-memory session first, since Awake() alone would return early while IsAuthenticated
        /// is still true from the old profile.
        /// </summary>
        public async Task ReloadForProfileChangeAsync() {
            // Waits out any exchange in flight, whose result belongs to the old profile.
            await _refreshGate.WaitAsync();
            try {
                _accessToken = null;
                _expiresAtUtc = DateTime.MinValue;
                ResetRestoreBackoff();
                IsAuthenticated = false;
            } finally {
                _refreshGate.Release();
            }
            OnPropertyChanged(nameof(SignedInAs));
            await Awake();
        }

        /// <summary>
        /// Discards the cached token, which can still look fresh with a drifting clock. Only if it's the one
        /// rejected, so a second concurrent 401 doesn't discard the new replacement.
        /// </summary>
        public void InvalidateAccessToken(string? rejectedToken) {
            if (rejectedToken != null && rejectedToken == _accessToken) {
                _expiresAtUtc = DateTime.MinValue;
            }
        }

        public async Task SignOutAsync() {
            // Under the gate, so a refresh in flight cannot store a token after the sign-out.
            await _refreshGate.WaitAsync();
            try {
                string? refreshToken = _settings.GetRefreshToken();
                if (!string.IsNullOrEmpty(refreshToken)) {
                    try {
                        string url = $"{ServerEndpoints.BaseUrl.TrimEnd('/')}/o/revoke_token/";
                        using HttpResponseMessage response = await _webClient.PostAsync(url, new FormUrlEncodedContent(
                            new Dictionary<string, string> {
                                ["token"]     = refreshToken!,
                                ["client_id"] = ServerEndpoints.OAuthClientId,
                            }));
                    } catch (Exception ex) {
                        LogError($"Could not revoke token server-side: {ex.Message}");
                    }
                }
                _accessToken = null;
                _expiresAtUtc = DateTime.MinValue;
                _settings.DeleteRefreshToken();
                ResetRestoreBackoff();
                IsAuthenticated = false;
            } finally {
                _refreshGate.Release();
            }
        }

        private bool IsFresh() =>
            _accessToken != null && DateTime.UtcNow < _expiresAtUtc - RefreshSkew;

        /// <summary>
        /// Still accepted by the server, though inside the early-refresh window.
        /// Good enough to keep a run going while refreshes are failing.
        /// </summary>
        private bool IsUsable() =>
            _accessToken != null && DateTime.UtcNow < _expiresAtUtc;

        private void ResetRestoreBackoff() {
            _restoreAttempt = 0;
            _nextRestoreUtc = DateTime.MinValue;
        }

        private void ScheduleNextRestore() {
            TimeSpan delay = RestoreBackoff[Math.Min(_restoreAttempt, RestoreBackoff.Length - 1)];
            _restoreAttempt++;
            _nextRestoreUtc = DateTime.UtcNow.Add(delay);
            Log($"Could not reach the token endpoint; retrying sign-in in {delay.TotalSeconds:F0}s.");
        }

        private Task<ExchangeOutcome> RefreshAsync(string refreshToken) =>
            ExchangeAsync(new Dictionary<string, string> {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"]     = ServerEndpoints.OAuthClientId,
            });

        // Callers hold _refreshGate.
        private async Task<ExchangeOutcome> ExchangeAsync(Dictionary<string, string> form) {
            try {
                Guid? profileIdAtStart = _settings.ProfileService.ActiveProfile?.Id;
                string url = $"{ServerEndpoints.BaseUrl.TrimEnd('/')}/o/token/";
                using HttpResponseMessage response = await _webClient.PostAsync(url, new FormUrlEncodedContent(form));
                string body = await response.Content.ReadAsStringAsync();

                // Tokens are stored per N.I.N.A. profile. One that belongs to the profile active when
                // the request went out must not be written into, or delete from, another.
                if (_settings.ProfileService.ActiveProfile?.Id != profileIdAtStart) {
                    LogWarning("The N.I.N.A. profile changed during a sign-in exchange; its result was discarded.");
                    return ExchangeOutcome.Retryable;
                }

                if (!response.IsSuccessStatusCode) {
                    LogError($"Token endpoint returned {response.StatusCode}: {body}");

                    if (form["grant_type"] == "refresh_token" && IsInvalidGrant(body)) {
                        _settings.DeleteRefreshToken();
                        _accessToken = null;
                        _expiresAtUtc = DateTime.MinValue;
                        LogError("Stored sign-in was rejected by the server; browser sign-in required.");
                        return ExchangeOutcome.CredentialRejected;
                    }
                    return ExchangeOutcome.Retryable;
                }

                JObject json = JObject.Parse(body);
                // The rotated refresh token is saved first. The old one is already spent, so losing this
                // one to a malformed field further down would sign the user out.
                if (json["refresh_token"] is JValue { Type: JTokenType.String } refreshValue
                    && refreshValue.Value<string>() is { Length: > 0 } newRefreshToken) {
                    _settings.SetRefreshToken(newRefreshToken);
                }

                int lifetimeSeconds = json["expires_in"] is JValue { Type: JTokenType.Integer } lifetimeValue
                    ? lifetimeValue.Value<int>()
                    : DefaultTokenLifetimeSeconds;
                _accessToken = json["access_token"] is JValue { Type: JTokenType.String } accessValue
                    ? accessValue.Value<string>()
                    : null;
                _expiresAtUtc = DateTime.UtcNow.AddSeconds(lifetimeSeconds);

                return string.IsNullOrEmpty(_accessToken)
                    ? ExchangeOutcome.Retryable
                    : ExchangeOutcome.Success;

            } catch (Exception ex) {
                LogError($"Token exchange failed: {ex.Message}");
                return ExchangeOutcome.Retryable;
            }
        }

        /// <summary>
        /// The error body is only JSON when it came from the application. A gateway timeout or captive
        /// portal returns HTML, which must not read as a rejection.
        /// </summary>
        private static bool IsInvalidGrant(string body) {
            try {
                return JObject.Parse(body)["error"]?.ToString() == "invalid_grant";
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Only fills the "signed in as" label. GetObserverDetailAsync swallows its own failures and
        /// returns null, so there's nothing to catch.
        /// </summary>
        private async Task FetchAccountEmailAsync() {
            JObject? result = await _webClient.GetObserverDetailAsync();
            if ((string?)result?["email"] is { } email) {
                _settings.SetUserEmail(email);
                OnPropertyChanged(nameof(SignedInAs));
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
