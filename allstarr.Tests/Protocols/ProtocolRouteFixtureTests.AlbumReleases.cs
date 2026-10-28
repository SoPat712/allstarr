using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using allstarr.Core.Settings;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task SavedAlbumCopiesKeepTheOldFavoriteIdAndReparentSongsWithoutChangingStoredHearts(string backend)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        var protocol = backend.ToLowerInvariant();
        await SeedFavoriteViewsAsync(storage, protocol);
        const string savedId = "ext-deezer-album-saved-release";
        const string copyId = "ext-qobuz-album-copy-release";
        const string cleanId = "ext-deezer-album-clean-release";
        var at = DateTimeOffset.UtcNow.AddDays(-2);
        await using (var db = storage.CreateDbContext())
        {
            var song = await db.Set<FavoriteStateRecord>().SingleAsync(item => item.OwnerUserId == FavoriteViewer && item.ItemId == "ext-deezer-song-saved");
            var jobId = await db.Set<FavoriteEventRecord>().Where(item => item.Id == song.LastEventId).Select(item => item.JobId).SingleAsync();
            song.DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, song.ItemId, "song", "Nicole Kidman", ["ADÉLA"], "PRIMA", 180000)
            { AlbumId = savedId, IsExplicit = true }, FavoriteDisplaySnapshot.JsonOptions);
            foreach (var display in new[]
            {
                new FavoriteDisplaySnapshot(1, savedId, "album", "PRIMA", ["ADÉLA"]) { Upc = "012345678905", IsExplicit = true },
                new FavoriteDisplaySnapshot(1, copyId, "album", "PRIMA", ["ADÉLA"]) { Upc = "012345678905", IsExplicit = true },
                new FavoriteDisplaySnapshot(1, cleanId, "album", "PRIMA", ["ADÉLA"]) { Upc = "012345678912", IsExplicit = false },
                new FavoriteDisplaySnapshot(1, "ext-qobuz-song-another", "song", "KGB", ["ADÉLA"], "PRIMA", 200000)
                    { AlbumId = copyId, IsExplicit = true }
            })
            {
                var eventId = Guid.CreateVersion7();
                var date = display.ItemId == savedId ? at : at.AddHours(1);
                db.Set<FavoriteEventRecord>().Add(new()
                {
                    Id = eventId,
                    OwnerUserId = FavoriteViewer,
                    Protocol = protocol,
                    BackendInstanceId = "primary",
                    BackendPrincipalId = protocol == "jellyfin" ? "user-1" : "fixture",
                    ItemId = display.ItemId,
                    Operation = FavoriteOperation.Favorite,
                    EventKey = display.ItemId,
                    SourceRevision = display.ItemId,
                    CorrelationId = "album-fixture",
                    JobId = jobId,
                    State = FavoriteEventState.Succeeded,
                    CreatedAt = date,
                    UpdatedAt = date
                });
                db.Set<FavoriteStateRecord>().Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = FavoriteViewer,
                    Protocol = protocol,
                    BackendInstanceId = "primary",
                    ItemId = display.ItemId,
                    LastEventId = eventId,
                    IsFavorite = true,
                    UpdatedAt = date,
                    DisplayMetadataJson = JsonSerializer.Serialize(display, FavoriteDisplaySnapshot.JsonOptions)
                });
            }
            await db.SaveChangesAsync();
        }
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        var automatic = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
        { UserId = FavoriteViewer };
        policies.Setup(item => item.ResolveForUserAsync(FavoriteViewer, It.IsAny<CancellationToken>())).ReturnsAsync(automatic);
        policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(automatic);
        using var factory = new ProtocolFactory(backend,
            request => backend == "Jellyfin" ? Json(200, """{"Id":"user-1","Items":[],"TotalRecordCount":0}""")
                : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
            services =>
            {
                ConfigureFavoriteViews(services, storage, Mock.Of<IProtocolProviderGateway>(MockBehavior.Strict));
                services.RemoveAll<IEffectiveProviderPolicyResolver>();
                services.AddSingleton(policies.Object);
            });
        var principal = protocol == "jellyfin" ? "user-1" : "fixture";
        var execution = new ProtocolExecutionContext(backend == "Jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
            "primary", principal, new AllstarrPrincipal(FavoriteViewer, protocol, "primary", principal, "Listener", false),
            "album-fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var reader = factory.Services.GetRequiredService<SavedLibraryReader>();
        var saved = await reader.ReadAsync(execution);
        var explicitAlbum = Assert.Single(saved, item => item.ItemId == savedId);
        Assert.Equal("PRIMA (Explicit)", explicitAlbum.Display.Title);
        Assert.Equal(2, explicitAlbum.Display.Count);
        Assert.Equal("PRIMA (Clean)", Assert.Single(saved, item => item.ItemId == cleanId).Display.Title);
        Assert.DoesNotContain(saved, item => item.ItemId == copyId);
        Assert.All(saved.Where(item => item.Display.Kind == "song" && item.Display.Album == "PRIMA"),
            item => Assert.Equal(savedId, item.Display.AlbumId));
        var provided = new Album
        {
            Id = "ext-apple-download-album-source",
            ExternalProvider = "apple-download",
            ExternalId = "source",
            Title = "PRIMA",
            Artist = "ADÉLA",
            SongCount = 11,
            Upc = "012345678905",
            IsExplicit = true
        };
        await reader.PreferSavedIdsAsync(execution, [], [provided], default);
        Assert.Equal(savedId, provided.Id);
        await using (var edit = storage.CreateDbContext())
        {
            foreach (var state in await edit.Set<FavoriteStateRecord>().Where(item => item.OwnerUserId == FavoriteViewer &&
                (item.ItemId == savedId || item.ItemId == copyId)).ToArrayAsync())
            {
                var display = FavoriteDisplaySnapshot.Read(state.ItemId, state.DisplayMetadataJson)!;
                state.DisplayMetadataJson = JsonSerializer.Serialize(display with { IsExplicit = null }, FavoriteDisplaySnapshot.JsonOptions);
            }
            await edit.SaveChangesAsync();
        }
        var unknownSavedExplicit = new Album
        {
            Id = "explicit-source",
            ExternalProvider = "apple-download",
            ExternalId = "explicit-source",
            Title = "PRIMA",
            Artist = "ADÉLA",
            Upc = "012345678905",
            IsExplicit = true
        };
        var unknownSavedClean = new Album
        {
            Id = "clean-source",
            ExternalProvider = "apple-download",
            ExternalId = "clean-source",
            Title = "PRIMA",
            Artist = "ADÉLA",
            Upc = "0012345678905",
            IsExplicit = false
        };
        await reader.PreferSavedIdsAsync(execution, [], [unknownSavedExplicit, unknownSavedClean], default);
        Assert.Equal("explicit-source", unknownSavedExplicit.Id);
        Assert.Equal("clean-source", unknownSavedClean.Id);
        await using var check = storage.CreateDbContext();
        var states = await check.Set<FavoriteStateRecord>().Where(item => item.OwnerUserId == FavoriteViewer &&
            (item.ItemId == savedId || item.ItemId == copyId || item.ItemId == cleanId)).ToArrayAsync();
        Assert.Equal(3, states.Length);
        Assert.All(states, item => Assert.True(item.IsFavorite));
        Assert.All(states, item => Assert.Equal("PRIMA", FavoriteDisplaySnapshot.Read(item.ItemId, item.DisplayMetadataJson)!.Title));
    }

    [Theory]
    [InlineData("Jellyfin", "json", false)]
    [InlineData("Jellyfin", "json", true)]
    [InlineData("Subsonic", "json", false)]
    [InlineData("Subsonic", "json", true)]
    [InlineData("Subsonic", "xml", false)]
    [InlineData("Subsonic", "xml", true)]
    public async Task AlbumSearchAndArtistBrowseCollapseCopiesAndNameExplicitAndCleanEditions(string backend, string format, bool artistPage)
    {
        Album Release(string id, string upc, bool rating) => new()
        {
            Id = "ext-deezer-album-" + id,
            ExternalProvider = "deezer",
            ExternalId = id,
            Title = "PRIMA",
            Artist = "ADÉLA",
            ArtistId = "ext-deezer-artist-artist",
            Upc = upc,
            IsExplicit = rating,
            SongCount = 11
        };
        var albums = new List<Album>
        {
            Release("explicit", "012345678905", true), Release("copy", "012345678905", true),
            Release("clean", "012345678912", false)
        };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "PRIMA",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .ReturnsAsync(new SearchResult { Albums = albums });
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "artist"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-artist", ExternalProvider = "deezer", ExternalId = "artist", Name = "ADÉLA" });
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "artist"))
            .ReturnsAsync(albums);
        using var factory = new ProtocolFactory(backend, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"verified-user"}""");
            if (path.Contains("ping", StringComparison.Ordinal)) return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            return backend == "Jellyfin" ? Json(200, """{"Items":[],"TotalRecordCount":0}""") : CatalogNative(format,
                """{"subsonic-response":{"status":"ok","searchResult3":{"album":[]}}}""",
                """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><searchResult3/></subsonic-response>""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        var url = backend == "Jellyfin"
            ? artistPage ? "/Items?api_key=fixture&ParentId=ext-deezer-artist-artist&IncludeItemTypes=MusicAlbum&Limit=100"
                : "/Items?api_key=fixture&SearchTerm=PRIMA&IncludeItemTypes=MusicAlbum&Limit=100"
            : artistPage ? $"/rest/getArtist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=ext-deezer-artist-artist"
                : $"/rest/search3.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&query=PRIMA&songCount=0&albumCount=100&artistCount=0";
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] names;
        if (format == "xml")
        {
            var root = XDocument.Parse(body).Root!;
            var ns = root.GetDefaultNamespace();
            names = root.Element(ns + (artistPage ? "artist" : "searchResult3"))!.Elements(ns + "album")
                .Select(album => album.Attribute("name")!.Value).ToArray();
        }
        else
        {
            using var document = JsonDocument.Parse(body);
            var rows = backend == "Jellyfin" ? document.RootElement.GetProperty("Items")
                : document.RootElement.GetProperty("subsonic-response").GetProperty(artistPage ? "artist" : "searchResult3").GetProperty("album");
            names = rows.EnumerateArray().Select(album => album.GetProperty(backend == "Jellyfin" ? "Name" : "name").GetString()!).ToArray();
            if (backend == "Jellyfin") Assert.Equal(2, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
        }
        Assert.Equal(backend == "Jellyfin" ? ["PRIMA (Clean) [A]", "PRIMA (Explicit) [A]"] :
            new[] { "PRIMA (Clean)", "PRIMA (Explicit)" }, names.Order(StringComparer.Ordinal));
        Assert.All(albums, album => Assert.Equal("PRIMA", album.Title));
    }
}
