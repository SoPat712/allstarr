using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ExtensionFetchTests
{
    private const string ConfigResponse = """
        {"deviceId":"fixture-device","sessionId":"fixture-session","version":"1.0.0",
         "displayLanguage":"en_US","musicTerritory":"US","csrf":{"token":"fixture-token","ts":"123","rnd":"456"}}
        """;
    private const string SearchResponse = """
        {"methods":[{"interface":"Web.TemplatesInterface.v1_0.Touch.WidgetsInterface.VisualShovelerWidgetElement",
          "header":"Songs","items":[{"interface":"Web.TemplatesInterface.v1_0.Touch.WidgetsInterface.DescriptiveRowItemElement",
          "primaryText":"Fixture Song","secondaryText1":"Fixture Artist","secondaryText3":"3:21",
          "primaryTextLink":{"deeplink":"/albums/B000000001?trackAsin=B000000002"}}]}]}
        """;

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("application/json", "application/json")]
    [InlineData("text/plain;charset=UTF-8", "text/plain; charset=UTF-8")]
    [InlineData("application/json; charset=utf-8", "application/json; charset=utf-8")]
    public void Fetch_SearchSendsStringBodyAndParameterizedContentType(string contentType, string expectedContentType)
    {
        const string source = """
            {"name":"fixture","version":"1.0.0","type":["metadata_provider"],
             "permissions":{"network":["music.amazon.com","na.mesk.skill.music.a2z.com"]},
             "searchBehavior":{"enabled":true,"filters":[{"id":"songs","label":"Songs"}]}}
            """;
        var script = """
            var config;
            registerExtension({
              initialize: function() {
                config = fetch('https://music.amazon.com/config.json?skipToken=false&clientApplication=skyfire', {
                  method: 'POST', headers: {'User-Agent':'Fixture Browser', 'Accept':'application/json'}
                }).json();
              },
              customSearch: function(query, options) {
                var pageUrl = 'https://music.amazon.com/search/' + encodeURIComponent(query);
                var body = JSON.stringify({
                  filter: JSON.stringify({IsLibrary:['false']}),
                  keyword: JSON.stringify({interface:'Web.TemplatesInterface.v1_0.Touch.SearchTemplateInterface.SearchKeywordClientInformation', keyword:query}),
                  suggestedKeyword: query, userHash: JSON.stringify({level:'LIBRARY_MEMBER'}),
                  headers: {'x-amzn-device-id':config.deviceId, 'csrf-token':config.csrf.token}
                });
                var response = fetch('https://na.mesk.skill.music.a2z.com/api/showSearch', {
                  method: 'POST', body: body,
                  headers: {'Content-Type':CONTENT_TYPE, 'User-Agent':'Fixture Browser',
                    Origin:'https://music.amazon.com', Referer:pageUrl}
                });
                if (!response.ok || options.filter !== 'songs') return [];
                return response.json().methods[0].items.map(function(item) {
                  return {id:'B000000002', name:item.primaryText, artists:item.secondaryText1, item_type:'track'};
                });
              }
            });
            """.Replace("CONTENT_TYPE", JsonSerializer.Serialize(contentType), StringComparison.Ordinal);
        var root = CreateRoot();
        try
        {
            var manifest = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            var origins = ExtensionSdkV1.ParseManifest(manifest).Permissions
                .Where(item => item.Kind == ExtensionPermissionKind.Network)
                .Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var handler = new SearchHandler();
            var sandbox = new ExtensionSandbox(root, manifest, script, new FetchClientFactory(handler),
                NullLogger.Instance, new ExtensionRuntimePermissionSet(origins, new HashSet<string>(), new HashSet<string>()), runtimeStateDirectory: root);

            using var response = JsonDocument.Parse(sandbox.InvokeJson("searchTracks", """{"query":"café fixture","page":{"limit":10}}""")!);

            Assert.Equal(2, handler.Requests.Count);
            var request = handler.Requests[1];
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://na.mesk.skill.music.a2z.com/api/showSearch", request.Url);
            Assert.Equal(expectedContentType, request.ContentType);
            Assert.Equal("https://music.amazon.com", request.Headers["Origin"]);
            Assert.Equal("https://music.amazon.com/search/caf%C3%A9%20fixture", request.Headers["Referer"]);
            Assert.Equal("Fixture Browser", request.Headers["User-Agent"]);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("café fixture", body.RootElement.GetProperty("suggestedKeyword").GetString());
            Assert.Equal("fixture-token", body.RootElement.GetProperty("headers").GetProperty("csrf-token").GetString());
            using var keyword = JsonDocument.Parse(body.RootElement.GetProperty("keyword").GetString()!);
            Assert.Equal("café fixture", keyword.RootElement.GetProperty("keyword").GetString());
            Assert.Equal("Fixture Song", Assert.Single(response.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("header")]
    [InlineData("body")]
    [InlineData("transport")]
    public void Fetch_FailuresEmitOneWarningWithoutRequestValues(string failure)
    {
        const string privateValue = "private-fixture-value";
        var root = CreateRoot();
        try
        {
            var logger = new FetchLogger();
            var events = new List<(string Level, string Message)>();
            var permissions = new ExtensionRuntimePermissionSet(
                new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>(["token"]),
                SecretResolver: _ => throw new FormatException(privateValue),
                LogSink: (level, message) => events.Add((level, message)));
            var handler = new FailingHandler(privateValue);
            var bridge = new ExtensionHostBridge(root, new FetchClientFactory(handler), logger, permissions,
                extensionId: "fixture");
            var host = failure == "permission" ? "denied.example.test" : "api.example.test";
            var body = failure == "body" ? "{{allstarr-secret:token}}" : privateValue;
            var headers = new Dictionary<string, string> { ["X-Fixture"] = privateValue };
            if (failure == "header") headers["Content-Type"] = "invalid content type; " + privateValue;

            using var result = JsonDocument.Parse(JsonSerializer.Serialize(bridge.HttpRequest("POST",
                $"https://{host}/private-path?token={privateValue}", body, headers)));

            Assert.Equal(0, result.RootElement.GetProperty("statusCode").GetInt32());
            Assert.Equal(failure == "permission" ? "permission_denied" : "extension_request_failed",
                result.RootElement.GetProperty("error").GetString());
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal("fixture", entry.State["ExtensionId"]);
            Assert.Equal("POST", entry.State["Method"]);
            Assert.Equal(host, entry.State["Host"]);
            Assert.False(string.IsNullOrWhiteSpace(entry.State["Reason"]?.ToString()));
            Assert.DoesNotContain(privateValue, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-path", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("allstarr-secret", entry.Message, StringComparison.Ordinal);
            var retained = Assert.Single(events);
            Assert.Equal("warn", retained.Level);
            Assert.Contains("fixture", retained.Message, StringComparison.Ordinal);
            Assert.Contains("POST", retained.Message, StringComparison.Ordinal);
            Assert.Contains(host, retained.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-path", retained.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(privateValue, retained.Message, StringComparison.Ordinal);
            Assert.Equal(failure == "transport" ? 1 : 0, handler.RequestCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://na.mesk.skill.music.a2z.com/api/showSearch", true)]
    [InlineData("https://na.mesk.skill.music.a2z.com:443/api/showSearch", true)]
    [InlineData("https://na.mesk.skill.music.a2z.com:444/api/showSearch", false)]
    [InlineData("https://other.na.mesk.skill.music.a2z.com/api/showSearch", false)]
    [InlineData("https://na.mesk.skill.music.a2z.com.other.test/api/showSearch", false)]
    [InlineData("http://na.mesk.skill.music.a2z.com/api/showSearch", false)]
    public void Fetch_DeclaredOriginCoversApiPathWithoutGrantingOtherOrigins(string url, bool allowed)
    {
        var root = CreateRoot();
        try
        {
            var bridge = new ExtensionHostBridge(root, new FetchClientFactory(new SearchHandler()), NullLogger.Instance,
                new ExtensionRuntimePermissionSet(new HashSet<string>(["https://na.mesk.skill.music.a2z.com/"]),
                    new HashSet<string>(), new HashSet<string>()));

            Assert.Equal(allowed, bridge.IsNetworkAllowed(new Uri(url)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-fetch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record CapturedRequest(string Method, string Url, string Body, string? ContentType, Dictionary<string, string> Headers);

    private sealed class SearchHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.NonValidated.ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            var payload = request.RequestUri.AbsolutePath switch
            {
                "/config.json" => ConfigResponse,
                "/api/showSearch" => SearchResponse,
                _ => throw new InvalidOperationException("Unexpected fixture request.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FetchClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FailingHandler(string message) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new HttpRequestException(message);
        }
    }

    private sealed class FetchLogger : ILogger
    {
        public List<(LogLevel Level, Dictionary<string, object?> State, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(item => item.Key, item => item.Value), formatter(state, exception), exception));
    }
}
