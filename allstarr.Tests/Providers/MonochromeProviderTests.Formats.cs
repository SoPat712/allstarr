using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Monochrome;

namespace allstarr.Tests.Providers;

public sealed partial class MonochromeProviderTests
{
    [Theory]
    [InlineData("alac", "audio/mp4", true)]
    [InlineData("mp4a", "audio/mp4", true)]
    [InlineData("mp4a", "audio/mp4", false)]
    [InlineData("alac", "application/octet-stream", true)]
    public async Task Stream_InspectsMp4SampleDescriptionAndHonorsMinimumQuality(string codec, string type, bool allowLossy)
    {
        var body = Mp4(codec);
        var calls = 0;
        using var http = Client(request =>
        {
            calls++;
            var range = request.Headers.Range!.Ranges.Single();
            var start = (int)range.From!.Value;
            var end = (int)Math.Min(range.To!.Value, body.Length - 1);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[start..(end + 1)]) };
            response.Content.Headers.ContentType = new(type);
            response.Content.Headers.ContentRange = new(start, end, body.Length);
            return response;
        });
        var original = Context();
        var context = new ProviderExecutionContext(original.Actor, "monochrome", null,
            new(new(allowLossy ? ProviderAudioQuality.Any : ProviderAudioQuality.Lossless, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, false, false), "format-test", "format-test", original.Deadline, default);
        var result = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(context, Request());
        Assert.Equal(allowLossy || codec == "alac", result.IsSuccess);
        Assert.True(calls >= 2);
        if (!result.IsSuccess) return;
        Assert.Equal("mp4", result.RequireValue().Media.Container);
        Assert.Equal(codec == "mp4a" ? "aac" : "alac", result.RequireValue().Media.Codec);
        Assert.Equal("audio/mp4", result.RequireValue().Media.MimeType);
        if (codec == "mp4a") Assert.Contains("lossy", result.RequireValue().QualityDowngradeReason!);
    }

    [Fact]
    public async Task Stream_DoesNotTreatCodecTextOutsideSampleDescriptionAsAudio()
    {
        var body = Box("ftyp", "M4A \0\0\0\0M4A "u8.ToArray()).Concat(Box("free", "alac mp4a"u8.ToArray())).ToArray();
        using var http = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new("audio/mp4");
            return response;
        });
        var result = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request());
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, result.Error!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_InspectsMovieMetadataAfterMediaDataAndLargeSampleTables(bool largeMovie)
    {
        var original = Mp4("alac");
        var movie = original[20..];
        if (largeMovie) movie = Box("moov", movie[8..].Concat(Box("free", new byte[80000])).ToArray());
        var body = original[..20].Concat(Box("mdat", new byte[100000])).Concat(movie).ToArray();
        var ranges = new List<string>();
        using var http = Client(request =>
        {
            ranges.Add(request.Headers.Range!.ToString());
            var range = request.Headers.Range.Ranges.Single();
            var start = (int)range.From!.Value;
            var end = (int)Math.Min(range.To!.Value, body.Length - 1);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[start..(end + 1)]) };
            response.Content.Headers.ContentType = new("audio/mp4");
            response.Content.Headers.ContentRange = new(start, end, body.Length);
            return response;
        });
        var result = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request());
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal("alac", result.RequireValue().Media.Codec);
        Assert.InRange(ranges.Count, 3, 4);
    }

    private static byte[] Mp4(string codec) => Box("ftyp", "M4A \0\0\0\0M4A "u8.ToArray()).Concat(
        Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stsd",
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }.Concat(Box(codec, new byte[28])).ToArray()))))))).ToArray();

    private static byte[] Box(string type, byte[] content)
    {
        var result = new byte[content.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(result, result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        content.CopyTo(result, 8);
        return result;
    }
}
