using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Migrations;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class MigrationWizardFlowTests
{
    private readonly List<string> applePaths = [];
    private readonly List<Guid> appleSecretReads = [];
    private bool appleMissingToken, appleWithoutTotals;
    private async Task AppleSource()
    {
        await using (var db = factory.CreateDbContext())
        {
            await db.ProviderAccounts.Where(item => item.Id == account).ExecuteUpdateAsync(update => update.SetProperty(item => item.ProviderId, "apple-musickit"));
            var selected = await db.ProviderAccounts.SingleAsync();
            var secret = Guid.NewGuid();
            db.SecretReferences.Add(new() { Id = secret, UserId = owner, Purpose = "fixture" });
            selected.SecretReferenceId = secret;
            db.ProviderAccounts.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = other,
                ProviderId = "apple-musickit",
                Enabled = true,
                Revision = 1,
                DisplayName = "Other listener"
            });
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync((ProtocolExecutionContext _, string query, int _) =>
            {
                var match = System.Text.RegularExpressions.Regex.Match(query, @"Stream song (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var id = match.Groups[1].Value;
                return new MatchingSongSearchResult(match.Success
                    ? [new Song { Id = $"ext-deezer-song-stream{id}", ExternalProvider = "deezer", ExternalId = $"stream{id}",
                        Title = $"Stream song {id}", Artist = "Artist", Album = "Album", Duration = 180 }] : [], []);
            });
        var http = new HttpClient(new AppleProviderTestFactory.Handler(AppleResponse));
        var adapter = new AppleMusicKitPlaylistCapabilityAdapter(AppleProviderTestFactory.Client(http, new AppleSecrets(this)), http);
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new(request,
                [new(0, null!, null!, ObservedProviderCapabilities.Wrap<IProviderPlaylistCapability>(adapter, new allstarr.Core.Health.ProviderRuntimeHealth()), new(request.Actor, "apple-musickit",
                    new(account, "apple-musickit", ProviderAccountScope.Personal, revision, ownerUserId: owner, secretReferenceId: Guid.NewGuid()),
                    request.Policy, "migration", request.CorrelationId, request.Deadline, request.CancellationToken), null)],
                new(request.CorrelationId, ProviderCapabilityKind.Playlist, "apple-musickit", account, [])));
    }
    private HttpResponseMessage AppleResponse(HttpRequestMessage request)
    {
        Assert.Equal("GET", request.Method.Method);
        Assert.Equal("media-user-token=fixture-apple", request.Headers.GetValues("Cookie").Single());
        var uri = request.RequestUri!;
        applePaths.Add(uri.PathAndQuery);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var offset = query.TryGetValue("offset", out var cursor) ? int.Parse(cursor.ToString()) : 0;
        var limit = query.TryGetValue("limit", out var size) ? int.Parse(size.ToString()) : 25;
        if (expired || failSecondPage && offset > 0) return new(HttpStatusCode.Forbidden);
        var path = uri.AbsolutePath;
        object[] data;
        var total = 1;
        if (path.EndsWith("/songs", StringComparison.Ordinal) || path.EndsWith("/tracks", StringComparison.Ordinal))
        {
            total = count;
            data = Enumerable.Range(offset, Math.Min(limit, Math.Max(0, count - offset))).Select(index => (object)new
            {
                id = $"i.song{index}",
                type = "library-songs",
                attributes = new { name = index == 0 ? "Local song" : $"Stream song {index}", artistName = "Artist", albumName = "Album", durationInMillis = 180000, isrc = index == 0 ? "USAAA2600001" : null }
            }).ToArray();
        }
        else if (path.EndsWith("/albums", StringComparison.Ordinal))
            data = offset == 0 ? [new { id = "l.album", attributes = new { name = "Release", artistName = "Artist", releaseDate = "2024-01-01", trackCount = 12, upc = "012345678901", contentRating = "clean" } }] : [];
        else if (path.EndsWith("/artists", StringComparison.Ordinal))
            data = offset == 0 ? [new { id = "r.artist", attributes = new { name = "Artist" }, relationships = new { albums = new { data = new[] { new { attributes = new { name = "Release" } } } } } }] : [];
        else
            data = offset == 0 ? [new { id = "p.library", attributes = new { name = "Apple playlist", lastModifiedDate = "2026-11-01" }, relationships = new { tracks = new { meta = appleWithoutTotals ? null : new { total = count } } } }] : [];
        var body = JsonSerializer.Serialize(new { data, meta = appleWithoutTotals ? null : new { total }, next = offset + data.Length < total ? $"{path}?offset={offset + data.Length}" : null },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task AppleLibrarySongsPageIntoTheSameHeartReceiptsAndKeepTheirProviderIdentity()
    {
        await AppleSource(); count = 26;
        var run = await Selected("liked-songs");
        Assert.Equal("apple-musickit", (await Service().StatusAsync(Context(owner), run, default)).Run.ProviderId);
        await Finish(run);
        Assert.Equal(26, (await AssertEquation(run)).Total.Accepted);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.NotEmpty(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        Assert.Equal(1, nativeHearts);
        await using var db = factory.CreateDbContext();
        Assert.Equal(26, await db.FavoriteStates.CountAsync(item => item.IsFavorite));
        Assert.Equal(26, await db.PlaylistSyncEntryResults.CountAsync());
        Assert.Contains(await db.AuditEvents.Where(item => item.Category == "job-progress").ToArrayAsync(),
            item => JsonSerializer.Deserialize<JsonElement>(item.DetailsJson).GetProperty("message").GetString() == "Moving your Apple Music collection.");
        Assert.All(await db.ProviderTrackIdentities.Where(item => item.ProviderAccountId == account).ToArrayAsync(), item => Assert.Equal("apple-musickit", item.ProviderId));
        Assert.Contains(applePaths, path => path.StartsWith("/v1/me/library/songs?", StringComparison.Ordinal) && path.Contains("offset=25", StringComparison.Ordinal));
        Assert.All(appleSecretReads, id => Assert.Equal(account, id));
        Assert.Empty(await new FavoriteViewReader(factory).ReadAsync(Context(other)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplePlaylistsReuseLinkedAndOneTimeImports(bool keepInSync)
    {
        await AppleSource();
        var run = await Selected("p.library", keepInSync);
        await Finish(run);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using var db = factory.CreateDbContext();
        var link = Assert.Single(await db.PlaylistLinks.ToArrayAsync());
        Assert.Equal("apple-musickit", link.SourceProviderId);
        Assert.Equal("p.library", link.SourcePlaylistId);
        Assert.Equal(account, link.ProviderAccountId);
        Assert.Equal(keepInSync ? PlaylistImportMode.Linked : PlaylistImportMode.OneTime, link.ImportMode);
        Assert.Equal(keepInSync, link.ScheduleId != null);
        Assert.Equal(1, creates);
        Assert.NotEmpty(await db.PlaylistSyncEntryResults.ToArrayAsync());
    }

    [Theory]
    [InlineData("saved-albums")]
    [InlineData("followed-artists")]
    public async Task AppleLibraryResourcesUseTheSameMatchingMembershipAndArtistFollow(string kind)
    {
        ResourceCatalog(); await AppleSource();
        var run = await Selected(kind);
        await Finish(run);
        Assert.Equal(1, (await AssertEquation(run)).Total.Accepted);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        Assert.Single(await new FavoriteViewReader(factory).ReadAsync(Context(owner)));
        var followed = await new NewReleasesService(factory, clock, router.Object, Mock.Of<IDurableRuntimeSettings>()).ListAsync(owner);
        Assert.Equal(kind == "followed-artists" ? 1 : 0, followed.Count);
        if (kind == "followed-artists") Assert.Equal("deezer", Assert.Single(followed).ProviderId);
    }

    [Fact]
    public async Task AppleExpiredTokenPausesAtTheSavedPageAndRestoredAccountResumes()
    {
        await AppleSource(); count = 26; failSecondPage = true;
        var run = await Selected("liked-songs");
        await Step();
        Assert.Equal(DurableJobCompletionKind.Failed, (await Step()).Kind);
        var status = await Service().StatusAsync(Context(owner), run, default);
        Assert.Equal("waiting", status.Run.State);
        Assert.Equal(25, status.Total.Selected);
        Assert.Contains("Apple Music connection", status.Collections.Single(item => item.SourceId == "liked-songs").Collection.Error);
        failSecondPage = false; revision++;
        await using (var db = factory.CreateDbContext()) { (await db.ProviderAccounts.SingleAsync(item => item.Id == account)).Revision = revision; await db.SaveChangesAsync(); }
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Finish(run);
        Assert.Equal(26, (await AssertEquation(run)).Total.Accepted);
    }

    [Fact]
    public async Task AppleMissingTokenCannotStartAndNamesTheConnectionToRestore()
    {
        await AppleSource(); appleMissingToken = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Start());
        Assert.Contains("Restore the Apple Music connection", error.Message);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<CollectionMigrationRunRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task AppleMissingTokenRequiresRestoringTheSelectedAccount()
    {
        await AppleSource();
        var run = await Selected("liked-songs");
        appleMissingToken = true;
        Assert.Equal(DurableJobCompletionKind.Failed, (await Step()).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ChangeAsync(Context(owner), run, "resume", null, default));
        appleMissingToken = false;
        await Service().ChangeAsync(Context(owner), run, "resume", null, default);
        await Finish(run);
    }

    [Fact]
    public async Task AppleAccountOwnershipAndPersonalScopeAreCheckedBeforeReadingSecrets()
    {
        await AppleSource();
        await using var db = factory.CreateDbContext();
        var foreign = await db.ProviderAccounts.SingleAsync(item => item.OwnerUserId == other);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().StartAsync(Context(owner), foreign.Id, "foreign", default));
        var selected = await db.ProviderAccounts.SingleAsync(item => item.Id == account);
        selected.OwnerUserId = null;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Start());
        Assert.Empty(appleSecretReads);
    }

    [Theory]
    [InlineData("liked-songs")]
    [InlineData("p.library")]
    public async Task AppleLibraryWithoutTotalCountsIsStillPagedAndVerified(string source)
    {
        await AppleSource(); appleWithoutTotals = true; count = 26;
        var run = await Selected(source);
        await Finish(run);
        Assert.Equal(26, (await AssertEquation(run)).Total.Selected);
    }

    [Fact]
    public async Task ExistingMigrationPayloadsWithoutAProviderContinueWithSpotify()
    {
        var run = await Selected("liked-songs");
        await using (var db = factory.CreateDbContext())
        {
            var record = await db.Set<CollectionMigrationRunRecord>().SingleAsync();
            var payload = System.Text.Json.Nodes.JsonNode.Parse(record.PayloadJson)!.AsObject();
            payload.Remove("ProviderId");
            record.PayloadJson = payload.ToJsonString();
            await db.SaveChangesAsync();
        }
        await Finish(run);
        Assert.Equal("spotify", (await Service().StatusAsync(Context(owner), run, default)).Run.ProviderId);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);
        await using var hearts = factory.CreateDbContext();
        Assert.Equal(2, await hearts.FavoriteStates.CountAsync(item => item.IsFavorite));
    }

    private sealed class AppleSecrets(MigrationWizardFlowTests fixture) : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext selected, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            fixture.appleSecretReads.Add(selected.AccountId);
            Assert.Equal(fixture.account, selected.AccountId);
            return operation(Encoding.UTF8.GetBytes(fixture.appleMissingToken ? "{}" : "{\"MusicUserToken\":\"fixture-apple\",\"Storefront\":\"us\"}"));
        }
    }
}
