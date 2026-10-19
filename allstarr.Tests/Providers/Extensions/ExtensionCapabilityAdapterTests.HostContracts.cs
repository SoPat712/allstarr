using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ExtensionCapabilityAdapterTests
{
    [Fact]
    public void HttpRequest_AcceptsUrlAndOptionsAndSerializesBodies()
    {
        var handler = new ContractHandler();
        using var result = ProbeHost("""
            var a = http.request('https://api.example.test/items', {method:'patch', body:{title:'café'}, headers:{'X-Test':'options'}});
            var b = http.post('https://api.example.test/items', {count:2}, {headers:{'X-Test':'wrapped'}});
            var c = http.get('https://api.example.test/items', {headers:{'X-Test':'get'}});
            return [a.status, a.statusCode, a.ok, a.headers['content-type'], a.headers['x-test'], b.ok, c.ok];
            """, handler);

        Assert.Equal("[201,201,true,\"application/json\",\"response\",true,true]", result.RootElement.GetRawText());
        Assert.Equal(new[] { "PATCH", "POST", "GET" }, handler.Requests.Select(item => item.Method));
        Assert.Equal("{\"title\":\"café\"}", handler.Requests[0].Body);
        Assert.Equal("{\"count\":2}", handler.Requests[1].Body);
        Assert.Equal(new[] { "options", "wrapped", "get" }, handler.Requests.Select(item => item.Header));
    }

    [Fact]
    public void HttpRequest_ReportsTransportFailureWithoutAnHttpStatus()
    {
        using var result = ProbeHost("""
            var response = http.request('https://denied.example.test/items', {method:'HEAD'});
            return {status:response.status, statusCode:response.statusCode, ok:response.ok, error:response.error};
            """);

        Assert.Equal(0, result.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("statusCode").GetInt32());
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("permission_denied", result.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(302, "GET", "")]
    [InlineData(307, "POST", "payload")]
    [InlineData(308, "POST", "payload")]
    public void HttpRedirects_PreserveMethodsAsRequiredAndCheckEveryDestination(int status, string method, string body)
    {
        var handler = new RedirectContractHandler(status);
        using var result = ProbeHost("""
            var success = http.post('https://api.example.test/start', 'payload');
            var denied = http.get('https://api.example.test/denied');
            var loop = http.get('https://api.example.test/loop');
            return [success.status, success.url, denied.error, loop.status];
            """, handler);

        Assert.Equal(201, result.RootElement[0].GetInt32());
        Assert.Equal("https://api.example.test/final", result.RootElement[1].GetString());
        Assert.Equal("permission_denied", result.RootElement[2].GetString());
        Assert.Equal(0, result.RootElement[3].GetInt32());
        var final = Assert.Single(handler.Requests, item => item.Path == "/final");
        Assert.Equal(method, final.Method);
        Assert.Equal(body, final.Body);
        Assert.Equal(9, handler.Requests.Count);
    }

    [Fact]
    public void Storage_PreservesJsonValuesAndSupportsRemovalAndFallbacks()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-storage-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "storage.json"), """{"legacy":"123"}""");
            using (var result = ProbeHost("""
                var saved = storage.set('state', {count:2, flags:[true,null,'value']});
                storage.set('text', '123');
                storage.set('empty', null);
                return [saved, storage.get('state'), storage.get('text'), storage.get('empty'), storage.get('missing', false),
                  storage.set('removed', 1), storage.delete('removed'), storage.remove('removed'), storage.get('legacy')];
                """, runtimeDirectory: root))
                Assert.Equal("[true,{\"count\":2,\"flags\":[true,null,\"value\"]},\"123\",null,false,true,true,true,\"123\"]", result.RootElement.GetRawText());
            using var persisted = ProbeHost("return storage.get('state');", runtimeDirectory: root);
            Assert.Equal(2, persisted.RootElement.GetProperty("count").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HmacSha1_RejectsUnsupportedInputsAndPreservesArrayPositions()
    {
        using var result = ProbeHost("""
            return [utils.hmacSHA1({}, 'message').length, utils.hmacSHA1('key', null).length,
              Array.from(utils.hmacSHA1([1, '2', , 4], 'message')),
              Array.from(utils.hmacSHA1([1, 0, 0, 4], 'message')), Array.isArray(utils.hmacSHA1('key', 'message'))];
            """);

        Assert.Equal(0, result.RootElement[0].GetInt32());
        Assert.Equal(0, result.RootElement[1].GetInt32());
        using var expected = new System.Security.Cryptography.HMACSHA1([1, 0, 0, 4]);
        var bytes = expected.ComputeHash(Encoding.UTF8.GetBytes("message"));
        Assert.Equal(bytes, result.RootElement[2].EnumerateArray().Select(item => item.GetByte()));
        Assert.Equal(bytes, result.RootElement[3].EnumerateArray().Select(item => item.GetByte()));
        Assert.True(result.RootElement[4].GetBoolean());
    }

    [Fact]
    public void HashUtilities_CoerceProvidedValuesAndHandleMissingArguments()
    {
        using var result = ProbeHost("return [utils.sha256(null), utils.md5(false), utils.sha256(), utils.md5()];");

        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("null"))).ToLowerInvariant(), result.RootElement[0].GetString());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("false"))).ToLowerInvariant(), result.RootElement[1].GetString());
        Assert.Equal("", result.RootElement[2].GetString());
        Assert.Equal("", result.RootElement[3].GetString());
    }

    [Fact]
    public void SettingsDefaults_PreserveAllJsonTypes()
    {
        const string source = """
            {"name":"fixture","version":"1.0.0","type":["metadata_provider"],"permissions":{},"settings":[
              {"key":"fraction","type":"number","default":1.5},
              {"key":"options","type":"string","default":["a",2]},
              {"key":"nested","type":"string","default":{"enabled":false}}]}
            """;
        var manifest = SpotiFlacExtensionCompatibility.NormalizeManifest(source, "registerExtension({getTrack:function(){}});");
        using var settings = JsonDocument.Parse(SpotiFlacExtensionCompatibility.SettingsJson(manifest));

        Assert.Equal(1.5, settings.RootElement.GetProperty("fraction").GetDouble());
        Assert.Equal("a", settings.RootElement.GetProperty("options")[0].GetString());
        Assert.False(settings.RootElement.GetProperty("nested").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void LocalTime_IsAvailableThroughTheLegacyBridge()
    {
        using var result = ProbeHost("""
            var time = gobackend.getLocalTime();
            var date = new Date(time.timestamp * 1000);
            return [time.year === date.getFullYear(), time.month === date.getMonth() + 1, time.day === date.getDate(),
              time.weekday === date.getDay(), time.offsetMinutes === date.getTimezoneOffset(), typeof time.timezone === 'string',
              gobackend === utils];
            """);

        Assert.All(result.RootElement.EnumerateArray(), item => Assert.True(item.GetBoolean()));
    }

    [Theory]
    [InlineData("hex", "4ef997456198dd78", "hex", "0000000000000000", "0000000000000000")]
    [InlineData("base64", "TvmXRWGY3Xg=", "base64", "AAAAAAAAAAA=", "0000000000000000")]
    [InlineData("hex", "4ef997456198dd78", "utf8", "\0\0\0\0\0\0\0\0", "0000000000000000")]
    [InlineData("hex", "4ef997456198dd78", "hex", "0000000000000000", "00")]
    public void BlockDecryption_AcceptsExplicitEncodings(string inputEncoding, string data, string outputEncoding, string expected, string key)
    {
        using var result = ProbeHost($$"""
            return utils.decryptBlockCipher({{JsonSerializer.Serialize(data)}}, {algorithm:'blowfish', mode:'cbc',
              key:{{JsonSerializer.Serialize(key)}}, keyEncoding:'hex', iv:'0000000000000000', ivEncoding:'hex', padding:'none',
              inputEncoding:{{JsonSerializer.Serialize(inputEncoding)}}, outputEncoding:{{JsonSerializer.Serialize(outputEncoding)}}});
            """);

        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(expected, result.RootElement.GetProperty("data").GetString());
        using var invalid = ProbeHost("return utils.decryptBlockCipher('invalid', {algorithm:'blowfish', key:'bad', keyEncoding:'hex'});");
        Assert.False(invalid.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task FileBytes_RespectRangesEncodingsAppendAndWorkspaceLimits()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-file-contract", Guid.NewGuid().ToString("N"));
        try
        {
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(),
                new ProviderDownloadWorkspaceOptions { RootPath = root, MaximumArtifactBytes = 16 });
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(Guid.CreateVersion7(), job, "fixture-extension", null, "download"));
            using var scope = ExtensionArtifactInvocationScope.Open(resolver, workspace.Reference, job, "fixture-extension", 16, CancellationToken.None);
            using var result = ProbeHost("""
                var written = file.writeBytes('bytes', '000102', {encoding:'hex'});
                var appended = file.writeBytes('bytes', 'AwQ=', {append:true});
                var part = file.readBytes('bytes', {offset:1,length:2,encoding:'hex'});
                var tail = file.readBytes('bytes', {offset:3,length:8});
                var eof = file.readBytes('bytes', {offset:5,length:2});
                var oversized = file.writeBytes('bytes', '0123456789abcdef', {append:true,encoding:'utf8'});
                var denied = file.writeBytes('../outside', 'AA==');
                file.writeBytes('edit', '010203', {encoding:'hex'});
                file.writeBytes('edit', 'ff', {encoding:'hex'});
                file.writeBytes('edit', 'ee', {encoding:'hex',offset:1});
                var edited = file.readBytes('edit', {encoding:'hex'});
                file.writeBytes('edit', '00', {encoding:'hex',truncate:true});
                return {written:written.success, appended:appended.success, part:part, tail:tail, eof:eof,
                  size:file.getSize('bytes'), oversized:oversized.success, denied:denied.success, missing:file.getSize('missing').success,
                  missingDelete:file.delete('missing'), edited:edited.data,
                  truncated:file.getSize('edit').size};
                """);

            var value = result.RootElement;
            Assert.True(value.GetProperty("written").GetBoolean());
            Assert.True(value.GetProperty("appended").GetBoolean());
            Assert.Equal("0102", value.GetProperty("part").GetProperty("data").GetString());
            Assert.Equal(2, value.GetProperty("part").GetProperty("bytes_read").GetInt32());
            Assert.False(value.GetProperty("part").GetProperty("eof").GetBoolean());
            Assert.Equal("AwQ=", value.GetProperty("tail").GetProperty("data").GetString());
            Assert.True(value.GetProperty("tail").GetProperty("eof").GetBoolean());
            Assert.True(value.GetProperty("eof").GetProperty("eof").GetBoolean());
            Assert.Equal(5, value.GetProperty("size").GetProperty("size").GetInt32());
            Assert.False(value.GetProperty("oversized").GetBoolean());
            Assert.False(value.GetProperty("denied").GetBoolean());
            Assert.False(value.GetProperty("missing").GetBoolean());
            Assert.False(value.GetProperty("missingDelete").GetBoolean());
            Assert.Equal("ffee03", value.GetProperty("edited").GetString());
            Assert.Equal(1, value.GetProperty("truncated").GetInt32());
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, File.ReadAllBytes(scope.ResolveTemporaryPath("bytes")));
            Assert.Throws<InvalidOperationException>(() => ProbeHost("return file.delete('../outside');"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationProbes_ObserveTheCurrentDownloadToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-cancellation-contract", Guid.NewGuid().ToString("N"));
        try
        {
            var resolver = new ProviderDownloadArtifactResolver(new MemoryArtifactStore(), new ProviderDownloadWorkspaceOptions { RootPath = root });
            var job = Guid.CreateVersion7();
            var workspace = await resolver.CreateWorkspaceAsync(new(Guid.CreateVersion7(), job, "fixture-extension", null, "download"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            const string source = """
                {"name":"fixture","version":"1.0.0","type":["metadata_provider"],"permissions":{}}
                """;
            const string script = """
                var observed;
                registerExtension({initialize:function(){observed=[utils.isDownloadCancelled(),utils.isRequestCancelled()];},
                  getTrack:function(){return {id:JSON.stringify(observed)};}});
                """;
            var manifest = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
            ExtensionSandbox sandbox;
            using (ExtensionArtifactInvocationScope.Open(resolver, workspace.Reference, job, "fixture-extension", 16, cancelled.Token))
                sandbox = new ExtensionSandbox(root, manifest, script, new HttpClientFactory(), NullLogger.Instance,
                    runtimeStateDirectory: Path.Combine(root, "runtime"));

            using var response = JsonDocument.Parse(sandbox.InvokeJson("getTrack", """{"trackId":"probe"}""")!);
            Assert.Equal("[true,true]", response.RootElement.GetProperty("id").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static JsonDocument ProbeHost(string body, HttpMessageHandler? handler = null, string? runtimeDirectory = null)
    {
        const string source = """
            {"name":"fixture","version":"1.0.0","type":["metadata_provider"],"permissions":{"storage":true}}
            """;
        var script = "registerExtension({getTrack:function(){return {id:JSON.stringify((function(){" + body + "})())};}});";
        var manifest = SpotiFlacExtensionCompatibility.NormalizeManifest(source, script);
        var permissions = new ExtensionRuntimePermissionSet(new HashSet<string>(["https://api.example.test/"]),
            new HashSet<string>(["*"]), new HashSet<string>());
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), manifest, script, new HttpClientFactory(handler),
            NullLogger.Instance, permissions, runtimeDirectory);
        using var response = JsonDocument.Parse(sandbox.InvokeJson("getTrack", """{"trackId":"probe"}""")!);
        return JsonDocument.Parse(response.RootElement.GetProperty("id").GetString()!);
    }

    private sealed class ContractHandler : HttpMessageHandler
    {
        public List<(string Method, string Body, string Header)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("X-Test", out var values) ? values.Single() : ""));
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response.Content.Headers.ContentType!.CharSet = null;
            response.Headers.Add("X-Test", "response");
            return response;
        }
    }

    private sealed class RedirectContractHandler(int status) : HttpMessageHandler
    {
        public List<(string Path, string Method, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, request.Method.Method, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var response = new HttpResponseMessage(path == "/final" ? HttpStatusCode.Created : (HttpStatusCode)status)
            { RequestMessage = request, Content = new StringContent("{}") };
            if (path != "/final") response.Headers.Location = new Uri(path == "/denied"
                ? "https://denied.example.test/private" : path == "/loop" ? "/loop" : "/final", UriKind.RelativeOrAbsolute);
            return response;
        }
    }
}
