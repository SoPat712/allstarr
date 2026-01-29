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

    [Fact]
    public async Task ExtractAllParametersAsync_QueryParameters_ExtractsCorrectly()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?u=admin&p=password&v=1.16.0&c=testclient&f=json");

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
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
        // Arrange
        var context = new DefaultHttpContext();
        var formData = "u=admin&p=password&query=test+artist&artistCount=10";
        var bytes = Encoding.UTF8.GetBytes(formData);
        
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.ContentLength = bytes.Length;
        context.Request.Method = "POST";

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("password", result["p"]);
        Assert.Equal("test artist", result["query"]);
        Assert.Equal("10", result["artistCount"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_JsonBody_ExtractsCorrectly()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var jsonData = "{\"u\":\"admin\",\"p\":\"password\",\"query\":\"test artist\",\"artistCount\":10}";
        var bytes = Encoding.UTF8.GetBytes(jsonData);
        
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("password", result["p"]);
        Assert.Equal("test artist", result["query"]);
        Assert.Equal("10", result["artistCount"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_QueryAndFormBody_MergesCorrectly()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?u=admin&p=password&f=json");
        
        var formData = "query=test&artistCount=5";
        var bytes = Encoding.UTF8.GetBytes(formData);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.ContentLength = bytes.Length;
        context.Request.Method = "POST";

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
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
        // Arrange
        var context = new DefaultHttpContext();

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_SpecialCharacters_EncodesCorrectly()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?query=rock+%26+roll&artist=AC%2FDC");

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("rock & roll", result["query"]);
        Assert.Equal("AC/DC", result["artist"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_InvalidJson_IgnoresBody()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?u=admin");
        
        var invalidJson = "{invalid json}";
        var bytes = Encoding.UTF8.GetBytes(invalidJson);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
        Assert.Single(result);
        Assert.Equal("admin", result["u"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_NullJsonValues_HandlesGracefully()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var jsonData = "{\"u\":\"admin\",\"p\":null,\"query\":\"test\"}";
        var bytes = Encoding.UTF8.GetBytes(jsonData);
        
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("admin", result["u"]);
        Assert.Equal("", result["p"]);
        Assert.Equal("test", result["query"]);
    }

    [Fact]
    public async Task ExtractAllParametersAsync_DuplicateKeys_BodyOverridesQuery()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?format=xml&query=old");
        
        var jsonData = "{\"query\":\"new\",\"artist\":\"Beatles\"}";
        var bytes = Encoding.UTF8.GetBytes(jsonData);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        // Act
        var result = await _parser.ExtractAllParametersAsync(context.Request);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("xml", result["format"]);
        Assert.Equal("new", result["query"]); // Body overrides query
        Assert.Equal("Beatles", result["artist"]);
    }
}
