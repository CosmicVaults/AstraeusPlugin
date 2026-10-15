using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>
    /// The Astraeus server with canned replies. A test says what each method and path answers, and reads back
    /// every request the plugin made. Anything with no reply configured gets a 404 that names itself.
    /// </summary>
    internal sealed class FakeServerHandler : HttpMessageHandler {
        public sealed record Request(string Method, string Path, string? Query, string? Body, string? Authorization);

        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _replies = new(StringComparer.Ordinal);

        public List<Request> Requests { get; } = new();

        /// <summary>What the server answers to this method and path. The path is matched exactly, without the query.</summary>
        public void Reply(string method, string path, HttpStatusCode status, string body) =>
            _replies[Key(method, path)] = (status, body);

        public void Reply(string method, string path, string jsonBody) => Reply(method, path, HttpStatusCode.OK, jsonBody);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) {
            Uri uri = request.RequestUri!;
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            string? query = string.IsNullOrEmpty(uri.Query) ? null : uri.Query.TrimStart('?');
            Requests.Add(new Request(request.Method.Method, uri.AbsolutePath, query, body,
                request.Headers.Authorization?.ToString()));

            if (!_replies.TryGetValue(Key(request.Method.Method, uri.AbsolutePath), out (HttpStatusCode Status, string Body) reply)) {
                reply = (HttpStatusCode.NotFound, $"no reply configured for {request.Method.Method} {uri.AbsolutePath}");
            }
            return new HttpResponseMessage(reply.Status) {
                Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }

        private static string Key(string method, string path) => method.ToUpperInvariant() + " " + path;
    }
}