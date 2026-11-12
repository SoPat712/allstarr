using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_TranslationUsesTheLyricsViewAndWorksInBatchWithEitherIdSpelling()
    {
        var calls = 0;
        using var factory = TranslationFactory(() =>
        {
            calls++;
            return Json(200, """[[["One",null,null,null]],null,"es"]""");
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var view = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks/native1?views=lyrics");
        var translated = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/tracks/native1/lyrics/translation");
        Assert.Equal(V2Ids.TrackSource("native1"), translated["trackId"]!.GetValue<string>());
        Assert.Equal("en", translated["to"]!.GetValue<string>());
        Assert.Equal(view["views"]!["lyrics"]!["lines"]!.AsArray().Select(line => line!["text"]!.GetValue<string>()),
            translated["lines"]!.AsArray().Select(line => line!["source"]!.GetValue<string>()));
        Assert.Equal("One", translated["lines"]![0]!["text"]!.GetValue<string>());
        Assert.Null(translated["lines"]![1]!["text"]);
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new
        {
            requests = new[] { new { id = "translation", path = $"/allstarr/v2/tracks/{V2Ids.TrackSource("native1")}/lyrics/translation?to=en" } }
        });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, body["responses"]!["translation"]!["status"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(translated, body["responses"]!["translation"]!["body"]));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClientApiV2_TranslationSettingIsDurableAndControlsTheRouteAndCapability()
    {
        using var factory = TranslationFactory(() => throw new InvalidOperationException("Unexpected translation request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var settings = factory.Services.GetRequiredService<IDurableRuntimeSettings>();
        Assert.Equal("true", (await settings.GetAsync("Lyrics:Translation:Enabled")).NormalizedValue);
        Assert.Equal("", (await settings.GetAsync("Lyrics:Translation:LibreTranslateUrl")).NormalizedValue);
        var capability = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/capabilities");
        Assert.True(capability["features"]!["v2LyricsTranslation"]!.GetValue<bool>());
        await settings.ApplyBatchAsync([new("Lyrics:Translation:Enabled", "false")], "fixture");
        using var response = await client.GetAsync("/allstarr/v2/tracks/native1/lyrics/translation");
        await AssertClientApiError(response, 404, "not_found");
        capability = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/capabilities");
        Assert.False(capability["features"]!["v2LyricsTranslation"]!.GetValue<bool>());
        await settings.ApplyBatchAsync([new("Lyrics:Translation:LibreTranslateUrl", "https://translate.invalid/base")], "fixture");
        Assert.Equal("https://translate.invalid/base", (await settings.GetAsync("Lyrics:Translation:LibreTranslateUrl")).NormalizedValue);
        foreach (var invalid in new[] { "ftp://translate.invalid", "file:///tmp/translate", "not a url" })
            await Assert.ThrowsAsync<ArgumentException>(() => settings.ApplyBatchAsync([new("Lyrics:Translation:LibreTranslateUrl", invalid)], "fixture"));
    }

    [Theory]
    [InlineData("en_US")]
    [InlineData("english")]
    [InlineData("en--US")]
    [InlineData("en&sl=es")]
    public async Task ClientApiV2_TranslationRejectsInvalidLanguages(string to)
    {
        using var factory = TranslationFactory(() => throw new InvalidOperationException("Unexpected translation request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/tracks/native1/lyrics/translation?to=" + Uri.EscapeDataString(to));
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApiV2_TranslationWithoutLyricsIsNotFound()
    {
        using var factory = TranslationFactory(() => throw new InvalidOperationException("Unexpected translation request"), hasLyrics: false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/tracks/native1/lyrics/translation");
        await AssertClientApiError(response, 404, "not_found");
    }

    private static ProtocolFactory TranslationFactory(Func<HttpResponseMessage> translate, bool hasLyrics = true)
    {
        var lyrics = new Mock<IProtocolLyricsResolver>();
        lyrics.Setup(value => value.GetRichLyricsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()))
            .ReturnsAsync(hasLyrics ? new RichLyrics(new("fixture", "Fixture"), "line",
                [new(1000, 2000, "Uno", "lead", []), new(2000, 3000, "I can hear the music", "lead", [])]) : null);
        return ClientDetailsFactory(request => request.RequestUri!.Host switch
        {
            "translate.googleapis.com" => translate(),
            _ => request.RequestUri.AbsolutePath switch
            {
                "/Users/Me" => Json(200, """{"Id":"listener"}"""),
                "/Users/listener/Items" => Json(200, V2LibrarySong),
                "/Items" => Json(200, V2LibrarySong),
                _ => Json(404, "{}")
            }
        }, services =>
        {
            services.RemoveAll<IProtocolLyricsResolver>();
            services.AddSingleton(lyrics.Object);
        });
    }
}
