using Microsoft.AspNetCore.Http;
using allstarr.Services.Subsonic;
using System.Text;

namespace allstarr.Tests;

public class SubsonicRequestParserTests
{
    private readonly SubsonicRequestParser _parser;

    public SubsonicRequestParserTests()
    {
        _parser = new SubsonicRequestParser();
    }

    [Theory]
    [InlineData("application/json", "{ \"nested\": {\"values\":[true,7]}, \"id\": \"native\" }")]
    [InlineData("application/octet-stream", "opaque-body")]
    public void RemoveQueryValues_PreservesUnrepresentableBodyBytes(string contentType, string body)
    {
        var parameters = new SubsonicRequestParameters("POST", contentType, body,
        [new("id", "external", SubsonicParameterSource.Query), new("id", "native", SubsonicParameterSource.Query)]);
        var result = parameters.RemoveValues("ID", new HashSet<string> { "external" });
        Assert.Equal(body, result.RawBody);
        Assert.Equal(["native"], result.GetAllValues("id"));
        Assert.Equal("POST", result.Method);
        Assert.Equal(contentType, result.ContentType);
    }

    [Fact]
    public async Task RemoveFormValues_PreservesOtherPairsAndTheirOriginalEncoding()
    {
        const string body = "id=ext-deezer-song-1&id=native&untouched=hello+world&duplicate=%2f&duplicate=%2F";
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        var parameters = await _parser.ExtractAllParametersAsync(context.Request);
        var result = parameters.RemoveValues("id", new HashSet<string> { "ext-deezer-song-1" });
        Assert.Equal("id=native&untouched=hello+world&duplicate=%2f&duplicate=%2F", result.RawBody);
        Assert.Equal(["native"], result.GetAllValues("id"));
        Assert.Equal(["/", "/"], result.GetAllValues("duplicate"));
        Assert.Same(result, result.RemoveValues("id", new HashSet<string> { "absent" }));
    }

    [Fact]
    public async Task ExtractAllParametersAsync_QueryParameters_ExtractsCorrectly()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?u=admin&p=password&v=1.16.0&c=testclient&f=json");

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Equal(5, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("password", result["p"]);
        Assert.Equal("1.16.0", result["v"]);
        Assert.Equal("testclient", result["c"]);
        Assert.Equal("json", result["f"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_FormEncodedBody_ExtractsCorrectly()
    {
        var context = new DefaultHttpContext();
        var formData = "u=admin&p=password&query=test+artist&artistCount=10";
        var bytes = Encoding.UTF8.GetBytes(formData);

        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.ContentLength = bytes.Length;
        context.Request.Method = "POST";

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Equal(4, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("password", result["p"]);
        Assert.Equal("test artist", result["query"]);
        Assert.Equal("10", result["artistCount"]);
    }

    [Fact]
    public async Task QueryOnlyChanges_KeepANestedJsonBodyExactly()
    {
        const string body = "{\"name\":\"Feishin\",\"directPlayProfiles\":[{\"containers\":[\"flac\"]}]}";
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString("?mediaId=ext-deezer-song-42&mediaType=song");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        var parameters = await _parser.ExtractAllParametersAsync(context.Request);
        var replaced = parameters.ReplaceValue("mediaId", "native-7");
        var appended = parameters.SetValue("f", "json");

        Assert.Equal("native-7", replaced["mediaId"]);
        Assert.Equal(body, replaced.RawBody);
        Assert.Equal(body, appended.RawBody);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_JsonBody_ExtractsCorrectly()
    {
        var context = new DefaultHttpContext();
        var jsonData = "{\"u\":\"admin\",\"p\":\"password\",\"query\":\"test artist\",\"artistCount\":10}";
        var bytes = Encoding.UTF8.GetBytes(jsonData);

        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Equal(4, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("password", result["p"]);
        Assert.Equal("test artist", result["query"]);
        Assert.Equal("10", result["artistCount"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_QueryAndFormBody_MergesCorrectly()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?u=admin&p=password&f=json");

        var formData = "query=test&artistCount=5";
        var bytes = Encoding.UTF8.GetBytes(formData);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.ContentLength = bytes.Length;
        context.Request.Method = "POST";

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Equal(5, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("password", result["p"]);
        Assert.Equal("json", result["f"]);
        Assert.Equal("test", result["query"]);
        Assert.Equal("5", result["artistCount"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_EmptyRequest_ReturnsEmptyDictionary()
    {
        var context = new DefaultHttpContext();

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_SpecialCharacters_EncodesCorrectly()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?query=rock+%26+roll&artist=AC%2FDC");

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Equal(2, result.Count);
        Assert.Equal("rock & roll", result["query"]);
        Assert.Equal("AC/DC", result["artist"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_InvalidJson_IgnoresBody()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?u=admin");

        var invalidJson = "{invalid json}";
        var bytes = Encoding.UTF8.GetBytes(invalidJson);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Single(result);
        Assert.Equal("admin", result["u"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_NullJsonValues_HandlesGracefully()
    {
        var context = new DefaultHttpContext();
        var jsonData = "{\"u\":\"admin\",\"p\":null,\"query\":\"test\"}";
        var bytes = Encoding.UTF8.GetBytes(jsonData);

        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Equal(3, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("", result["p"]);
        Assert.Equal("test", result["query"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_DuplicateKeys_BodyOverridesQuery()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?format=xml&query=old");

        var jsonData = "{\"query\":\"new\",\"artist\":\"Beatles\"}";
        var bytes = Encoding.UTF8.GetBytes(jsonData);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        var result = await _parser.ExtractAllParametersAsync(context.Request);

        Assert.Equal(3, result.Count);
        Assert.Equal("xml", result["format"]);
        Assert.Equal("new", result["query"]); // Body overrides query
        Assert.Equal("Beatles", result["artist"]);
    }
}
