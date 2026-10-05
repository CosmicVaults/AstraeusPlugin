using CosmicVaults.NINA.Astraeus.Engine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Auth {

    public sealed record AuthorizationCode(string Code, string Verifier, string RedirectUri);

    /// <summary>
    /// OAuth 2.0 authorization code flow with PKCE over a loopback redirect (RFC 8252). The user
    /// signs in through their own browser, so the plugin never sees or stores a password.
    /// </summary>
    public class PkceLoginFlow {
        private static readonly TimeSpan BrowserTimeout = TimeSpan.FromMinutes(3);
        private const string CallbackPath = "/callback";
        private const int VerifierByteCount = 32;
        private const int StateByteCount = 16;

        private readonly Observatory _observatory;

        private const string LogDevice = "webclient";
        private void Log(string message)      => _observatory.Log(message, LogDevice, LogCategory.Network);
        private void LogError(string message) => _observatory.LogError(message, LogDevice, LogCategory.Network);

        public PkceLoginFlow(Observatory observatory) {
            _observatory = observatory;
        }

        /// <summary>Null if the user cancelled, it timed out or the server refused.</summary>
        public async Task<AuthorizationCode?> RequestCodeAsync(CancellationToken cancellationToken) {
            string verifier  = Base64Url(RandomNumberGenerator.GetBytes(VerifierByteCount));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state     = Base64Url(RandomNumberGenerator.GetBytes(StateByteCount));

            HttpListener? listener = null;
            try {
                listener = StartListener(out int port);
                if (listener == null) {
                    LogError("No loopback port available for sign-in.");
                    return null;
                }

                string redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";
                string authorizeUrl = BuildAuthorizeUrl(redirectUri, challenge, state);

                // UseShellExecute must be true
                Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

                // Only a redirect carrying our state ends the wait. Anything else didn't come from our request.
                Dictionary<string, string>? query = await WaitForCallbackAsync(listener, state, cancellationToken);
                if (query == null) {
                    Log("Sign-in cancelled or timed out.");
                    return null;
                }

                if (query.TryGetValue("error", out string? error)) {
                    LogError($"Sign-in refused by server: {error}");
                    return null;
                }

                if (!query.TryGetValue("code", out string? code) || string.IsNullOrEmpty(code)) {
                    LogError("Sign-in returned no authorization code.");
                    return null;
                }

                return new AuthorizationCode(code, verifier, redirectUri);

            } catch (Exception ex) {
                LogError($"Sign-in failed: {ex.Message}");
                return null;
            } finally {
                // Must always run. An abandoned listener holds the port until N.I.N.A. exits, even
                // across a plugin reload.
                listener?.Close();
            }
        }

        private static HttpListener? StartListener(out int port) {
            foreach (int candidate in ServerEndpoints.LoopbackPorts) {
                HttpListener listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                try {
                    listener.Start();
                    port = candidate;
                    return listener;
                } catch (HttpListenerException) {
                    listener.Close();   // port busy, try the next
                }
            }
            port = 0;
            return null;
        }

        private static string BuildAuthorizeUrl(string redirectUri, string challenge, string state) {
            StringBuilder query = new StringBuilder();
            query.Append($"{ServerEndpoints.BaseUrl.TrimEnd('/')}/o/authorize/");
            query.Append("?response_type=code");
            query.Append($"&client_id={Uri.EscapeDataString(ServerEndpoints.OAuthClientId)}");
            query.Append($"&redirect_uri={Uri.EscapeDataString(redirectUri)}");
            query.Append($"&scope={Uri.EscapeDataString(ServerEndpoints.OAuthScope)}");
            query.Append($"&state={Uri.EscapeDataString(state)}");
            query.Append($"&code_challenge={Uri.EscapeDataString(challenge)}");
            query.Append("&code_challenge_method=S256");
            return query.ToString();
        }

        /// <summary>
        /// Waits for the redirect carrying expectedState and returns its query, or null on timeout or
        /// cancellation. Any other request to the loopback port gets a 404 and the wait goes on.
        /// </summary>
        private static async Task<Dictionary<string, string>?> WaitForCallbackAsync(
                HttpListener listener, string expectedState, CancellationToken cancellationToken) {

            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(BrowserTimeout);
            Task timeoutTask = Task.Delay(Timeout.Infinite, timeoutSource.Token);

            while (true) {
                Task<HttpListenerContext> contextTask = listener.GetContextAsync();

                // GetContextAsync ignores cancellation, so race it against a cancellable delay.
                // The Close() in the caller's finally block is what unblocks it.
                Task finished = await Task.WhenAny(contextTask, timeoutTask);
                if (finished != contextTask) {
                    // Observe the abandoned task so disposing the listener doesn't surface as an
                    // unobserved exception.
                    _ = contextTask.ContinueWith(task => _ = task.Exception,
                                                 TaskContinuationOptions.OnlyOnFaulted);
                    return null;
                }

                HttpListenerContext context = await contextTask;
                bool isOurCallback = context.Request.Url?.AbsolutePath == CallbackPath
                                     && context.Request.QueryString["state"] == expectedState;
                if (!isOurCallback) {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    continue;
                }

                Dictionary<string, string> result = new Dictionary<string, string>();
                foreach (string? key in context.Request.QueryString.AllKeys) {
                    if (key != null) result[key] = context.Request.QueryString[key] ?? string.Empty;
                }

                await WriteClosingPageAsync(context.Response, result.ContainsKey("code"));
                return result;
            }
        }

        private static async Task WriteClosingPageAsync(HttpListenerResponse response, bool isSuccess) {
            string message = isSuccess
                ? "Signed in to Astraeus. You can close this tab and return to N.I.N.A."
                : "Astraeus sign-in did not complete. You can close this tab and try again.";
            string html = "<!doctype html><meta charset=\"utf-8\"><title>Astraeus</title>"
                     + "<body style=\"font-family:system-ui;padding:3rem;text-align:center\">"
                     + $"<h2>{message}</h2></body>";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}