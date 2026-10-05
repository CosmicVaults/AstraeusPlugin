using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Auth {

    /// <summary>
    /// Adds the access token to each request and retries once with a fresh one on a 401. A handler because
    /// GetAsync and PostAsync would skip a SendAsync override on AstraeusWebClient.
    /// </summary>
    public class BearerTokenHandler : DelegatingHandler {

        private static readonly Uri AstraeusServer = new Uri(ServerEndpoints.BaseUrl);

        public Authenticator? Authenticator { get; set; }

        /// <summary>Raised on each 426 from the Astraeus server: this plugin version is no longer supported.</summary>
        public event Action? UpdateRequired;

        public BearerTokenHandler() : base(new HttpClientHandler()) { }

        protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
            HttpResponseMessage response = await SendWithBearerAsync(request, cancellationToken);
            // Checked on every path, the OAuth endpoints included, so a refused version is noticed
            // whether or not anybody is signed in.
            if (response.StatusCode == HttpStatusCode.UpgradeRequired && IsAstraeusServer(request)) {
                UpdateRequired?.Invoke();
            }
            return response;
        }

        private async Task<HttpResponseMessage> SendWithBearerAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {

            // The OAuth endpoints never get a bearer header, since getting one would recurse back
            // through GetAccessTokenAsync while refreshing. Nor does any host other than the Astraeus server.
            if (Authenticator == null || IsOAuthEndpoint(request) || !IsAstraeusServer(request)) {
                return await base.SendAsync(request, cancellationToken);
            }

            string? token = await Authenticator.GetAccessTokenAsync();
            if (token != null) {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            // Buffer before the first send so the request can be replayed on 401.
            if (request.Content != null) {
                await request.Content.LoadIntoBufferAsync();
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized) {
                return response;
            }

            // Expired mid-flight or revoked. Retry once with a fresh token. If that fails too, the
            // caller sees the 401 and the UI drops to signed-out. Invalidate the cached token first,
            // because it doesn't look expired locally, so GetAccessTokenAsync would hand back the rejected one.
            response.Dispose();
            Authenticator.InvalidateAccessToken(token);
            string? refreshed = await Authenticator.GetAccessTokenAsync();

            HttpRequestMessage retry = await CloneAsync(request);
            if (refreshed != null) {
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
            }
            return await base.SendAsync(retry, cancellationToken);
        }

        private static bool IsOAuthEndpoint(HttpRequestMessage request) =>
            request.RequestUri?.AbsolutePath.StartsWith("/o/", StringComparison.Ordinal) == true;

        private static bool IsAstraeusServer(HttpRequestMessage request) =>
            request.RequestUri is { } requestUri
            && Uri.Compare(requestUri, AstraeusServer, UriComponents.SchemeAndServer, UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0;

        /// <summary>An HttpRequestMessage can't be sent twice, so the retry needs a copy.</summary>
        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request) {
            HttpRequestMessage clone = new HttpRequestMessage(request.Method, request.RequestUri) {
                Version = request.Version,
            };

            if (request.Content != null) {
                byte[] bytes = await request.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(bytes);
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers) {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers) {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
