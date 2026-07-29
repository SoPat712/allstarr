using System.Text.Json;
using allstarr.Core.Protocols.Jellyfin;

namespace allstarr.Tests;

public class JellyfinSearchResponseSerializationTests
{
    [Fact]
    public void ShapeItemsResponse_PreservesStatusContentTypeAndPascalCaseBody()
    {
        var items = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Name"] = "BTS",
                ["Type"] = "MusicAlbum"
            }
        };
        var adapter = new JellyfinSearchProtocolAdapter();

        var response = adapter.ShapeItemsResponse(items, startIndex: 0, limit: 20);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal(
            "{\"Items\":[{\"Name\":\"BTS\",\"Type\":\"MusicAlbum\"}],\"TotalRecordCount\":1,\"StartIndex\":0}",
            response.Body);
    }

    [Fact]
    public void ShapeItemsResponse_AppliesPagingWithoutChangingTotalOrOrder()
    {
        var items = Enumerable.Range(1, 5)
            .Select(index => new Dictionary<string, object?>
            {
                ["Id"] = $"item-{index}",
                ["Name"] = $"Item {index}"
            })
            .ToList();
        var adapter = new JellyfinSearchProtocolAdapter();

        var response = adapter.ShapeItemsResponse(items, startIndex: 1, limit: 2);
        using var body = JsonDocument.Parse(response.Body);

        Assert.Equal(5, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("StartIndex").GetInt32());
        Assert.Equal(
            ["item-2", "item-3"],
            body.RootElement.GetProperty("Items")
                .EnumerateArray()
                .Select(item => item.GetProperty("Id").GetString()!)
                .ToArray());
    }

    [Fact]
    public void ShapeItemsResponse_EmptyPageRetainsRequestedStartIndex()
    {
        var adapter = new JellyfinSearchProtocolAdapter();

        var response = adapter.ShapeItemsResponse([], startIndex: 40, limit: 20);

        Assert.Equal(
            "{\"Items\":[],\"TotalRecordCount\":0,\"StartIndex\":40}",
            response.Body);
    }
}
