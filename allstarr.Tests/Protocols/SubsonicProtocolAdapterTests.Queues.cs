using System.Net;
using System.Text;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Services.Subsonic;

namespace allstarr.Tests;

public sealed partial class SubsonicProtocolAdapterTests
{
    [Theory]
    [InlineData(true, "2", "12345", 2, 12345)]
    [InlineData(false, "external", "0", 1, 0)]
    [InlineData(false, "native", "", 0, 0)]
    [InlineData(true, "0", "4294967296", 0, 4294967296)]
    public void QueueClientParametersPreserveDuplicatesAndMillisecondPrecision(
        bool byIndex, string current, string position, int expectedIndex, long expectedPosition)
    {
        var parameters = new SubsonicRequestParameters("POST", "application/x-www-form-urlencoded", null,
        [
            new("id", "native", SubsonicParameterSource.Form),
            new("id", "external", SubsonicParameterSource.Form),
            new("id", "external", SubsonicParameterSource.Form),
            new(byIndex ? "currentIndex" : "current", current, SubsonicParameterSource.Form),
            new("position", position, SubsonicParameterSource.Form)
        ]);
        var queue = SubsonicPlayQueueProtocolAdapter.Parse(parameters, byIndex);
        Assert.Equal(["native", "external", "external"], queue.Ids);
        Assert.Equal(expectedIndex, queue.CurrentIndex);
        Assert.Equal(expectedPosition, queue.Position);
    }

    [Fact]
    public void QueueNativeXmlRetainsAttributeSpellingWhitespaceAndUnknownChildren()
    {
        const string native = "<song id='native'  title='Native &amp; title' future='1.00'><unknown x='keep'/></song>";
        var response = new SubsonicProxyResponse(Encoding.UTF8.GetBytes(
            "<subsonic-response xmlns='http://subsonic.org/restapi' status='ok'>" + native + "</subsonic-response>"),
            "application/xml", HttpStatusCode.OK, new Dictionary<string, string[]>());
        var entry = SubsonicPlayQueueProtocolAdapter.NativeSong(response, "xml");
        var expected = native.Replace("<song", "<entry", StringComparison.Ordinal).Replace("</song>", "</entry>", StringComparison.Ordinal);
        Assert.Equal(expected, entry);
        var queue = new SubsonicSavedQueue([new("native", null, null, entry)], 0, 12345,
            "listener", DateTimeOffset.UtcNow, "Tempo");
        var result = Assert.IsType<ContentResult>(SubsonicPlayQueueProtocolAdapter.CreateResponse(queue,
            "xml", true, new SubsonicResponseBuilder()));
        Assert.Contains(expected, result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public void QueueLegacyCurrentMustIdentifyASavedEntry(string current)
    {
        var parameters = SubsonicRequestParameters.FromDictionary(new Dictionary<string, string>
        {
            ["id"] = "external",
            ["current"] = current
        });
        Assert.Throws<ArgumentException>(() => SubsonicPlayQueueProtocolAdapter.Parse(parameters, false));
    }
}
