using System.IO.Compression;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class ChineseWordLyricsParserTests
{
    internal const string EncryptedQrc = "EE7BEFB746F93831D42DCDF2E21F0D5D924943E250C9B32F6D731CE7C8A4CD011DCD11D72EFA5359726D5CBD32EA7DEE7F78198258600438BB41B669C2FCA264";

    [Fact]
    public void QrcCipherDecodesSyntheticXmlFixture()
    {
        const string xml = "<QrcInfos><Lyric_1 LyricContent=\"[1000,500]Short(1000,500)\" /></QrcInfos>";
        Assert.Equal(xml, ChineseWordLyricsParser.DecodeQrc(EncryptedQrc));
        Assert.Throws<FormatException>(() => ChineseWordLyricsParser.DecodeQrc("0"));
        Assert.Throws<FormatException>(() => ChineseWordLyricsParser.DecodeQrc("GGGGGGGGGGGGGGGG"));
    }

    [Theory]
    [InlineData("yrc", "[1000,1200](1000,400,0)First (1400,800,0)word")]
    [InlineData("qrc", "[1000,1200]First (1000,400)word(1400,800)")]
    [InlineData("krc", "[1000,1200]<0,400,0>First <400,800,0>word")]
    public void NativeWordsKeepSpacesAndAbsoluteMilliseconds(string format, string content)
    {
        var value = Parse(format, content).RequireValue();
        Assert.Equal(ProviderLyricsFormat.WordTimed, value.Format);
        Assert.Equal("[00:01.000]First word", value.Content);
        var line = Assert.Single(value.TimedLines!);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), line.Duration);
        Assert.Equal(new[] { "First ", "word" }, line.Segments!.Select(word => word.Text));
        Assert.Equal(new[] { 1000d, 1400d }, line.Segments!.Select(word => word.Start.TotalMilliseconds));
        Assert.Equal(new[] { 400d, 800d }, line.Segments!.Select(word => word.Duration!.Value.TotalMilliseconds));
    }

    [Theory]
    [InlineData("yrc", "[6190,4440](6190,2190,0)Synthetic (8380,540,0)words", 6190, 8380)]
    [InlineData("krc", "[1800,1640]<0,102,0>Synthetic <102,102,0>words", 1800, 1902)]
    [InlineData("qrc", "[27173,1343]Synthetic(27173,234) (0,0)words(27407,103)", 27173, 27407)]
    public void CapturedTimingExcerptsKeepOriginalBoundsWithSyntheticText(string format, string content, int first, int second)
    {
        var line = Assert.Single(Parse(format, content).RequireValue().TimedLines!);
        Assert.Equal("Synthetic words", line.Text);
        Assert.Equal(new[] { (double)first, second }, line.Segments!.Select(word => word.Start.TotalMilliseconds));
    }

    [Fact]
    public void QrcCapturedCipherBlockContainsAValidZlibHeader()
    {
        var schedule = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 16).Select(_ => new byte[6]).ToArray()).ToArray();
        QrcCipher.TripleDESKeySetup(Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL"), schedule, QrcCipher.DECRYPT);
        var block = new byte[8];
        QrcCipher.TripleDESCrypt(Convert.FromHexString("F6D80784E4ACD3FC"), block, schedule);
        Assert.Equal(8, block[0] & 15);
        Assert.Equal(0, ((block[0] << 8) + block[1]) % 31);
    }

    [Theory]
    [InlineData(" (0,0)Synthetic(1000,500)", " Synthetic")]
    [InlineData("Synthetic(1000,500) (0,0)", "Synthetic ")]
    public void QrcUntimedWhitespaceKeepsTextWithoutCreatingAWord(string body, string expected)
    {
        var line = Assert.Single(ChineseWordLyricsParser.ParseQrc("[1000,500]" + body, "qqlyrics").RequireValue().TimedLines!);
        Assert.Equal(expected, line.Text);
        Assert.Equal(expected, Assert.Single(line.Segments!).Text);
        Assert.Equal(TimeSpan.FromSeconds(1), line.Segments![0].Start);
    }

    [Fact]
    public void QrcXmlDecodesEntitiesAndRetainsLiteralParentheses()
    {
        const string xml = "<QrcInfos><LyricInfo><Lyric_1 LyricContent=\"[ti:Fixture]&#10;[ar:Artist]&#10;[1000,500](hi &amp; bye)(1000,500)\" /></LyricInfo></QrcInfos>";
        var value = ChineseWordLyricsParser.ParseQrc(xml, "qqlyrics").RequireValue();
        Assert.Equal("(hi & bye)", Assert.Single(value.TimedLines!).Text);
        Assert.Equal("Fixture", value.Metadata!.Title);
        Assert.Equal("Artist", value.Metadata.Artist);
    }

    [Fact]
    public void QrcXmlKeepsLiteralLineBreaksInContentAttribute()
    {
        const string xml = "<QrcInfos><Lyric_1 LyricContent=\"[1000,500]One(1000,500)\n[2000,500]Two(2000,500)\"/></QrcInfos>";
        var value = ChineseWordLyricsParser.ParseQrc(xml, "qqlyrics").RequireValue();
        Assert.Equal(new[] { "One", "Two" }, value.TimedLines!.Select(line => line.Text));
    }

    [Fact]
    public void KrcEnvelopeRequiresHeaderAndUsesBoundedZlibPayload()
    {
        const string text = "[1000,500]<0,500,0>Short";
        Assert.Equal(text, ChineseWordLyricsParser.DecodeKrc(Krc(text)));
        Assert.Throws<FormatException>(() => ChineseWordLyricsParser.DecodeKrc(Convert.ToBase64String("bad1"u8.ToArray())));
        Assert.Throws<FormatException>(() => ChineseWordLyricsParser.DecodeKrc(Krc(new string('x', 2_000_001))));
    }

    [Theory]
    [InlineData("yrc", "[1000,500](1000,-1,0)Bad")]
    [InlineData("yrc", "[1000,500](1000,500,1)Bad")]
    [InlineData("qrc", "[1000,500]Bad(86400001,1)")]
    [InlineData("qrc", "[1000,500]Bad(1000,500)untimed")]
    [InlineData("krc", "[1000,500]<-1,500,0>Bad")]
    [InlineData("krc", "[1000,500]<0,500,0>One<0,500,1>Bad")]
    public void MalformedNativeTimingFailsTheContract(string format, string content)
    {
        Assert.Equal("provider-contract-changed", Parse(format, content).Error!.Code);
    }

    [Fact]
    public void MetadataAndEmptyNativeContentAreUnavailable()
    {
        var value = ChineseWordLyricsParser.ParseYrc("{\"t\":0,\"c\":[{\"tx\":\"Credit\"}]}\n[ti:Fixture]", "neteaselyrics").RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, value.Availability);
        Assert.Null(value.TimedLines);
    }

    [Fact]
    public void RevisionIncludesNativeWordTiming()
    {
        var first = ChineseWordLyricsParser.ParseYrc("[1000,1000](1000,400,0)One (1400,600,0)two", "neteaselyrics").RequireValue();
        var changed = ChineseWordLyricsParser.ParseYrc("[1000,1000](1000,500,0)One (1500,500,0)two", "neteaselyrics").RequireValue();
        Assert.Equal(first.Content, changed.Content);
        Assert.NotEqual(first.Revision, changed.Revision);
    }

    [Theory]
    [InlineData("<QrcInfos><Lyric_1 LyricContent='[1,1]Bad(1,1)'/><Lyric_1 LyricContent='[1,1]Other(1,1)'/></QrcInfos>")]
    [InlineData("<!DOCTYPE QrcInfos [<!ENTITY x SYSTEM 'file:///missing'>]><QrcInfos><Lyric_1 LyricContent='&x;'/></QrcInfos>")]
    public void AmbiguousOrUnsafeQrcXmlFailsTheContract(string content)
    {
        Assert.Equal("provider-contract-changed", ChineseWordLyricsParser.ParseQrc(content, "qqlyrics").Error!.Code);
    }

    internal static string Krc(string text)
    {
        using var bytes = new MemoryStream();
        bytes.Write("krc1"u8);
        using var compressed = new MemoryStream();
        using (var stream = new ZLibStream(compressed, CompressionMode.Compress, true))
            stream.Write(Encoding.UTF8.GetBytes(text));
        byte[] key = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];
        var payload = compressed.ToArray();
        for (var index = 0; index < payload.Length; index++) bytes.WriteByte((byte)(payload[index] ^ key[index % key.Length]));
        return Convert.ToBase64String(bytes.ToArray());
    }

    private static ProviderOutcome<ProviderLyricsResult> Parse(string format, string text) => format switch
    {
        "yrc" => ChineseWordLyricsParser.ParseYrc(text, "neteaselyrics"),
        "qrc" => ChineseWordLyricsParser.ParseQrc(text, "qqlyrics"),
        _ => ChineseWordLyricsParser.ParseKrc(text, "kugoulyrics")
    };
}
