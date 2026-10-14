using System.Net;
using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("artist", false)]
    [InlineData("artist", true)]
    [InlineData("album", false)]
    [InlineData("album", true)]
    public async Task JellyfinNativeCatalog_AppendsMissingProviderChildrenAndPreservesNativeFields(string kind, bool userScoped)
    {
        var gateway = JellyfinCatalogGateway(kind, false);
        const string nativeAlbum = """{"Id":"native-child","Name":"Visions","Type":"MusicAlbum","Future":{"value":[1,true,"x"]},"ImageTags":{"Primary":"original"}}""";
        const string nativeSong = """{"Id":"native-child","Name":"Oblivion","Type":"Audio","Future":{"value":[1,true,"x"]},"UserData":{"PlayCount":7}}""";
        var native = kind == "artist" ? nativeAlbum : nativeSong;
        using var factory = JellyfinCatalogFactory(gateway.Object, kind,
            $$"""{ "Items":[{{native}}], "TotalRecordCount":1, "StartIndex":0, "FutureEnvelope":true }""");
        using var client = factory.CreateClient();
        if (kind == "album")
        {
            using var parent = await client.GetAsync("/Users/user-1/Items/native-parent?api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, parent.StatusCode);
        }
        var prefix = userScoped ? "/Users/user-1" : "";
        var filter = kind == "artist" ? "ArtistIds=native-parent&IncludeItemTypes=MusicAlbum" : "ParentId=native-parent";

        using var response = await client.GetAsync($"{prefix}/Items?{filter}&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(CanonicalJson(JsonDocument.Parse(native).RootElement), CanonicalJson(rows[0]));
        Assert.True(body.RootElement.GetProperty("FutureEnvelope").GetBoolean());
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(kind == "artist" ? "ext-deezer-album-missing" : "ext-deezer-song-missing", rows[1].GetProperty("Id").GetString());
        Assert.Equal(kind == "artist" ? "Art Angels [A]" : "Genesis [A]", rows[1].GetProperty("Name").GetString());
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("artist", false)]
    [InlineData("album", false)]
    [InlineData("artist", true)]
    [InlineData("album", true)]
    public async Task JellyfinNativeCatalog_EmptyOrFailedProviderKeepsResponseBytes(string kind, bool fails)
    {
        var gateway = JellyfinCatalogGateway(kind, true, fails);
        var name = kind == "artist" ? "Visions" : "Oblivion";
        var type = kind == "artist" ? "MusicAlbum" : "Audio";
        var native = $$"""{ "Items": [ {"Id":"native-child","Name":"{{name}}","Type":"{{type}}","Future":true} ], "TotalRecordCount":1 }""";
        using var factory = JellyfinCatalogFactory(gateway.Object, kind, native);
        using var client = factory.CreateClient();
        if (kind == "album")
        {
            using var parent = await client.GetAsync("/Users/user-1/Items/native-parent?api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, parent.StatusCode);
        }
        var filter = kind == "artist" ? "ArtistIds=native-parent&IncludeItemTypes=MusicAlbum" : "ParentId=native-parent";

        using var response = await client.GetAsync($"/Items?{filter}&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("artist", true)]
    [InlineData("artist", false)]
    [InlineData("album", true)]
    [InlineData("album", false)]
    public async Task JellyfinNativeCatalog_PagesAfterOwnedItemsAndHonorsLabelPreference(string kind, bool showLabel)
    {
        var gateway = JellyfinCatalogGateway(kind, false);
        var name = kind == "artist" ? "Visions" : "Oblivion";
        var type = kind == "artist" ? "MusicAlbum" : "Audio";
        var owned = $$"""{"Items":[{"Id":"native-child","Name":"{{name}}","Type":"{{type}}"}],"TotalRecordCount":1}""";
        using var factory = JellyfinCatalogFactory(gateway.Object, kind,
            """{"Items":[],"TotalRecordCount":1,"StartIndex":1}""",
            request => QueryHelpers.ParseQuery(request.RequestUri!.Query)["StartIndex"] == "0" ? owned : null,
            showLabel);
        using var client = factory.CreateClient();
        if (kind == "album")
        {
            using var parent = await client.GetAsync("/Users/user-1/Items/native-parent?api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, parent.StatusCode);
        }
        var filter = kind == "artist" ? "ArtistIds=native-parent&IncludeItemTypes=MusicAlbum" : "ParentId=native-parent";

        using var response = await client.GetAsync($"/Items?{filter}&StartIndex=1&Limit=1&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal((kind == "artist" ? "Art Angels" : "Genesis") + (showLabel ? " [A]" : ""), row.GetProperty("Name").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("StartIndex").GetInt32());
        if (kind == "artist")
            Assert.Equal("native-parent", Assert.Single(row.GetProperty("ArtistItems").EnumerateArray()).GetProperty("Id").GetString());
        gateway.VerifyAll();
    }

    private static Mock<IProtocolProviderGateway> JellyfinCatalogGateway(string kind, bool empty, bool fails = false)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var search = gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(),
            kind == "artist" ? "Grimes" : "Grimes Visions", 0, kind == "artist" ? 0 : 10, kind == "artist" ? 20 : 0, null));
        if (fails) search.ThrowsAsync(new HttpRequestException("fixture"));
        else search.ReturnsAsync(empty ? new SearchResult() : kind == "artist"
            ? new SearchResult { Artists = [CatalogArtist("real", 2)] }
            : new SearchResult { Albums = [CatalogAlbum("real", "Visions")] });
        if (!empty && kind == "artist")
            gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "real"))
                .ReturnsAsync([CatalogAlbum("owned", "Visions"), CatalogAlbum("missing", "Art Angels")]);
        if (!empty && kind == "album")
            gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "real"))
                .ReturnsAsync(new Album
                {
                    Title = "Visions",
                    Artist = "Grimes",
                    Songs =
                    [
                        new Song { Id = "ext-deezer-song-owned", Title = "Oblivion", Artist = "Grimes", ExternalProvider = "deezer", ExternalId = "owned" },
                        new Song { Id = "ext-deezer-song-missing", Title = "Genesis", Artist = "Grimes", ExternalProvider = "deezer", ExternalId = "missing" }
                    ]
                });
        return gateway;
    }

    private ProtocolFactory JellyfinCatalogFactory(IProtocolProviderGateway gateway, string kind, string native,
        Func<HttpRequestMessage, string?>? page = null, bool? showLabel = null) =>
        new("Jellyfin", request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/Users/user-1/Items/native-parent" => Json(200, kind == "artist"
                ? """{"Id":"native-parent","Type":"MusicArtist","Name":"Grimes"}"""
                : """{"Id":"native-parent","Type":"MusicAlbum","Name":"Visions","AlbumArtist":"Grimes"}"""),
            "/Items" or "/Users/user-1/Items" => Json(200,
                QueryHelpers.ParseQuery(request.RequestUri.Query).ContainsKey("ids")
                    ? kind == "artist"
                        ? """{"Items":[{"Id":"native-parent","Type":"MusicArtist","Name":"Grimes"}],"TotalRecordCount":1}"""
                        : """{"Items":[{"Id":"native-parent","Type":"MusicAlbum","Name":"Visions","AlbumArtist":"Grimes"}],"TotalRecordCount":1}"""
                    : page?.Invoke(request) ?? native),
            _ => throw new InvalidOperationException($"Unexpected fixture path: {request.RequestUri.AbsolutePath}")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway);
            if (showLabel.HasValue)
            {
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, SonicPrincipalStartupFilter>();
                var policies = new Mock<IEffectiveProviderPolicyResolver>();
                policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                        ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                        ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                    {
                        UserId = user,
                        Preferences = new ListeningPreferences { ShowExternalLabel = showLabel.Value }
                    });
                services.RemoveAll<IEffectiveProviderPolicyResolver>();
                services.AddSingleton(policies.Object);
            }
        });
}
