using System.Net;
using System.Text.Json;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class LyricsTranslationTests
{
    [Fact]
    public async Task MixedSongUsesSongVocabularyAndKeepsEveryLineAligned()
    {
        string[] source = ["I can feel the music", "Después bailamos toda la noche, corazón", "Un, dos, tres", "Go get a lighter, después bailamos", "O mere pin code ka number", "", "The music is in my heart", "My corazón", "My heartbeat"];
        var sent = new List<string>();
        var service = Service(request =>
        {
            Assert.Equal("translate.googleapis.com", request.RequestUri!.Host);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("auto", query["sl"].ToString());
            Assert.Equal("en", query["tl"].ToString());
            sent.Add(query["q"].ToString());
            return Google("Then we dance all night, my love\nOne, two, three\nGet a lighter and then we dance\nOh my postal code number\nMy heart", "es");
        });

        var result = await service.TranslateAsync("trk_song", source, "en", default);

        Assert.Equal(string.Join('\n', source[1..5].Append(source[7])), Assert.Single(sent));
        Assert.Equal("google", result.Provider);
        Assert.Equal(source.Length, result.Lines.Count);
        Assert.Equal(source, result.Lines.Select(line => line.Source));
        Assert.Equal(Enumerable.Range(0, source.Length), result.Lines.Select(line => line.Index));
        Assert.Null(result.Lines[0].Text);
        Assert.Equal("One, two, three", result.Lines[2].Text);
        Assert.NotNull(result.Lines[3].Text);
        Assert.NotNull(result.Lines[4].Text);
        Assert.Null(result.Lines[5].Text);
        Assert.Null(result.Lines[6].Text);
        Assert.Equal("My heart", result.Lines[7].Text);
        Assert.Null(result.Lines[8].Text);
        Assert.Equal("es", result.Lines[1].From);
    }

    [Fact]
    public async Task UnchangedAndSpellingTweaksAreDroppedButShortTranslationsRemain()
    {
        var service = Service(_ => Google("CAFE, AMOR!\nuno dos tres amour\nOne", "es"));
        var result = await service.TranslateAsync("trk_song", ["Café amor", "uno dos tres amor", "Uno"], "en", default);
        Assert.Null(result.Lines[0].Text);
        Assert.Null(result.Lines[1].Text);
        Assert.Equal("One", result.Lines[2].Text);
    }

    [Theory]
    [InlineData("uno dos tres cuatro", "uno dos cuatro")]
    [InlineData("uno dos tres cuatro", "uno dos tres cuatro cinco")]
    [InlineData("uno dos tres", "uno cinco dos tres")]
    [InlineData("uno dos tres", "dos tres")]
    [InlineData("uno dos tres", "uno dos three")]
    [InlineData("uno dos mi", "uno dos my")]
    [InlineData("No, no, adiós", "No, no, goodbye")]
    [InlineData("uno dos amor", "uno dos goodbye")]
    [InlineData("uno dos amor", "uno dos axxx")]
    [InlineData("uno dos amor", "uno dos amours")]
    [InlineData("uno dos amor", "uno dos axxr")]
    [InlineData("uno dos amor", "uno dos amo")]
    [InlineData("uno dos beautiful", "uno dos beautifuxxx")]
    [InlineData("uno dos tres", "one two tres")]
    public async Task MeaningfulWordChangesAreShown(string source, string translated)
    {
        var service = Service(_ => Google(translated, "es"));
        Assert.Equal(translated, Assert.Single((await service.TranslateAsync("trk_song", [source], "en", default)).Lines).Text);
    }

    [Theory]
    [InlineData("uno dos corazon", "UNO DOS corazón")]
    [InlineData("uno dos amor", "uno dos amour")]
    [InlineData("uno dos beautiful", "uno dos beautifull")]
    [InlineData("uno dos beautiful", "uno dos beautifuxx")]
    [InlineData("uno dos amor", "uno dos amur")]
    [InlineData("uno dos amor", "uno dos amor")]
    [InlineData("uno dos amor", "")]
    public async Task UnchangedEmptyAndNearIdenticalWordsAreHidden(string source, string translated)
    {
        var service = Service(_ => Google(translated, "es"));
        Assert.Null(Assert.Single((await service.TranslateAsync("trk_song", [source], "en", default)).Lines).Text);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("es", "es")]
    [InlineData("pt-br", "pt-BR")]
    public void TargetLanguagesDefaultAndUseCanonicalCasing(string? input, string expected)
    {
        Assert.True(LyricsTranslation.TryLanguage(input, out var to));
        Assert.Equal(expected, to);
    }

    [Fact]
    public async Task LostLineBoundariesRetryIndividuallyAndParseAllGoogleSegments()
    {
        var sent = new List<string>();
        var service = Service(request =>
        {
            var text = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query)["q"].ToString();
            sent.Add(text);
            return text.Contains('\n') ? Google("One Two", "es") : text == "Uno"
                ? Json("""[[["O","Uno",null,null],["ne","",null,null]],null,"es"]""") : Google("Two", "es");
        });
        var result = await service.TranslateAsync("trk_song", ["Uno", "Dos"], "en", default);
        Assert.Equal(new[] { "Uno\nDos", "Uno", "Dos" }, sent);
        Assert.Equal(new[] { "One", "Two" }, result.Lines.Select(line => line.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailuresFallThroughInOrder(bool libre)
    {
        var hosts = new List<string>();
        var service = Service(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (hosts.Count == 1) throw new TaskCanceledException("Provider timed out");
            if (request.RequestUri.Host == "api.mymemory.translated.net")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.Equal("autodetect|en", query["langpair"].ToString());
                return libre ? new(HttpStatusCode.BadGateway) : Json("""{"responseStatus":200,"responseData":{"translatedText":"One","detectedLanguage":"es"}}""");
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/base/translate", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("auto", body.RootElement.GetProperty("source").GetString());
            Assert.Equal("Uno", body.RootElement.GetProperty("q").GetString());
            Assert.Equal("en", body.RootElement.GetProperty("target").GetString());
            Assert.Equal("text", body.RootElement.GetProperty("format").GetString());
            return Json("""{"translatedText":"One","detectedLanguage":{"language":"es"}}""");
        }, libre ? "https://translate.invalid/base/" : "");
        var result = await service.TranslateAsync("trk_song", ["Uno"], "en", default);
        Assert.Equal(libre ? new[] { "translate.googleapis.com", "api.mymemory.translated.net", "translate.invalid" }
            : new[] { "translate.googleapis.com", "api.mymemory.translated.net" }, hosts);
        Assert.Equal(libre ? "libretranslate" : "mymemory", result.Provider);
        Assert.Equal("One", result.Lines[0].Text);
    }

    [Fact]
    public async Task MyMemorySkipsLinesOverItsLimitAndFailuresReturnNullLines()
    {
        var calls = 0;
        var service = Service(request =>
        {
            calls++;
            Assert.Equal("translate.googleapis.com", request.RequestUri!.Host);
            return new(HttpStatusCode.ServiceUnavailable);
        });
        var result = await service.TranslateAsync("trk_song", [new string('ñ', 501)], "en", default);
        Assert.Equal(1, calls);
        Assert.Null(result.Provider);
        Assert.Null(Assert.Single(result.Lines).Text);
    }

    [Fact]
    public async Task MyMemoryStillTranslatesShortLinesWhenAnotherLineExceedsItsLimit()
    {
        var service = Service(request => request.RequestUri!.Host == "translate.googleapis.com"
            ? new(HttpStatusCode.BadGateway)
            : Json("""{"responseStatus":200,"responseData":{"translatedText":"One"}}"""));
        var result = await service.TranslateAsync("trk_song", [new string('ñ', 501), "Uno"], "en", default);
        Assert.Equal("mymemory", result.Provider);
        Assert.Null(result.Lines[0].Text);
        Assert.Equal("One", result.Lines[1].Text);
    }

    [Fact]
    public async Task FinishedResponsesAreSharedAndLyricsOrLanguageChangesMissTheCache()
    {
        var calls = 0;
        var cache = new Mock<IApplicationCache>();
        var stored = new Dictionary<string, V2LyricsTranslationResponse>();
        cache.Setup(value => value.GetAsync<V2LyricsTranslationResponse>(It.IsAny<string>()))
            .ReturnsAsync((string key) => stored.GetValueOrDefault(key));
        cache.Setup(value => value.SetAsync(It.IsAny<string>(), It.IsAny<V2LyricsTranslationResponse>(), TimeSpan.FromDays(30)))
            .Callback((string key, V2LyricsTranslationResponse value, TimeSpan? _) => stored[key] = value).ReturnsAsync(true);
        var service = Service(_ => { calls++; return Google("One", "es"); }, cache: cache.Object);
        var first = await service.TranslateAsync("trk_song", ["Uno"], "en", default);
        Assert.Equal(first, await service.TranslateAsync("trk_song", ["Uno"], "en", default));
        Assert.Equal(1, calls);
        await service.TranslateAsync("trk_song", ["Dos"], "en", default);
        await service.TranslateAsync("trk_song", ["Uno"], "pt-BR", default);
        Assert.Equal(3, calls);
        Assert.Equal(3, stored.Count);
        Assert.All(stored.Keys, key => Assert.StartsWith("lyrics:translation:v2:", key));
    }

    private static LyricsTranslation Service(Func<HttpRequestMessage, HttpResponseMessage> respond, string libre = "", IApplicationCache? cache = null)
    {
        var settings = new Mock<IDurableRuntimeSettings>();
        settings.Setup(value => value.GetAsync("Lyrics:Translation:LibreTranslateUrl", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveRuntimeSetting("Lyrics:Translation:LibreTranslateUrl", RuntimeSettingValueType.String, libre, libre,
                RuntimeSettingOrigin.Bootstrap, null, null, null, null));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(LyricsTranslation.HttpClientName)).Returns(() => new HttpClient(new Handler(respond)));
        return new(factory.Object, cache ?? new TestMemoryApplicationCache(), settings.Object,
            new ApplicationCacheRequestCoalescer(new()));
    }

    private static HttpResponseMessage Google(string text, string from) => Json(JsonSerializer.Serialize(new object?[]
        { new object?[] { new object?[] { text, null, null, null } }, null, from }));

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
