using System.Reflection;
using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinSearchResponseSerializationTests
{
    [Fact]
    public void SerializeSearchResponseJson_PreservesPascalCaseShape()
    {
        var payload = new
        {
            Items = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = "BTS",
                    ["Type"] = "MusicAlbum"
                }
            },
            TotalRecordCount = 1,
            StartIndex = 0
        };

        var method = typeof(JellyfinController).GetMethod(
            "SerializeSearchResponseJson",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var closedMethod = method!.MakeGenericMethod(payload.GetType());
        var json = (string)closedMethod.Invoke(null, new object?[] { payload })!;

        Assert.Equal(
            "{\"Items\":[{\"Name\":\"BTS\",\"Type\":\"MusicAlbum\"}],\"TotalRecordCount\":1,\"StartIndex\":0}",
            json);
    }
}
