using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class NewReleasesTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private Factory _factory = null!;
    private readonly Clock _clock = new();
    private readonly Guid _first = Guid.CreateVersion7();
    private readonly Guid _second = Guid.CreateVersion7();
    private readonly Mock<IProviderRouter> _router = new();
    private readonly Mock<IDurableRuntimeSettings> _settings = new();
    private readonly Mock<IProviderMetadataCapability> _metadata = new();
    private NewReleasesService _service = null!;
    private DurableJobQueue _queue = null!;
    private ProviderError? _failure;
    private readonly Dictionary<Guid, ProviderAccountContext> _accounts = new();
    private ProviderAlbumMetadata[] _albums = [];
    private int _calls;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = _factory.CreateDbContext();
        foreach (var id in new[] { _first, _second })
            db.Users.Add(new()
            {
                Id = id,
                Enabled = true,
                BackendType = "subsonic",
                BackendInstanceId = "main",
                BackendPrincipalId = id.ToString(),
                DisplayName = "Listener",
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                LastSeenAt = _clock.UtcNow
            });
        await db.SaveChangesAsync();
        _settings.Setup(value => value.GetPreferencesAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new(), new(), true, "1"));
        _metadata.Setup(value => value.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .Returns((ProviderExecutionContext context, ProviderArtistItemsRequest _) =>
            {
                _calls++;
                return Task.FromResult(_failure == null
                    ? ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new(context.ProviderId, _albums))
                    : ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Failure(_failure));
            });
        _router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .Returns((ProviderRouteRequest request) =>
            {
                var provider = request.ProviderPriority.Single();
                var descriptor = new ProviderDescriptor(provider, provider, "Catalog fixture", ProviderOrigin.BuiltIn, "1", "1",
                    [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                        "1", ["getArtistAlbums"])], new());
                var context = new ProviderExecutionContext(request.Actor, provider, _accounts.GetValueOrDefault(request.Actor.EffectiveUserId!.Value), request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                return Task.FromResult(new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(0, descriptor, descriptor.Capabilities.Single(), _metadata.Object, context, null)],
                    new(request.CorrelationId, request.Capability, provider, null, [])));
            });
        var options = new DurableJobOptions();
        _queue = new(_factory, options, new(options), _clock);
        _service = NewService();
    }

    private NewReleasesService NewService() => new(_factory, _clock, _router.Object, _settings.Object);
    private Task Follow(Guid user, string provider = "deezer", string artist = "a") =>
        _service.FollowAsync(user, new(provider, artist, "Artist"));
    private Task<DurableJobCompletion> Poll(Guid user) => _service.PollAsync(user, Guid.CreateVersion7(), default);
    private ProviderAlbumMetadata Album(string provider = "deezer", string id = "one", string? upc = "0123456789012",
        bool? explicitValue = false, string? edition = null, string? type = "album", string? date = "2026-10-20",
        string? exclusion = null, string[]? isrcs = null) => new(new(provider, ProviderResourceKind.Album, id),
            "Release", [new("Artist", new(provider, ProviderResourceKind.Artist, "a"))],
            trackCount: isrcs?.Length, releaseDate: date, isExplicit: explicitValue, upc: upc, edition: edition,
            releaseType: type, exclusionReason: exclusion,
            tracks: isrcs?.Select((isrc, index) => new ProviderTrackMetadata(new(provider, ProviderResourceKind.Track, index.ToString()),
                "Track " + index, [new("Artist")], isrc: isrc, isExplicit: explicitValue)));

    [Fact]
    public async Task Proven_copies_group_but_unknown_identity_and_editions_stay_separate()
    {
        await Follow(_first);
        _albums = [Album(), Album(id: "clean", explicitValue: true), Album(id: "deluxe", edition: "Deluxe"),
            Album(id: "unproven", upc: null)];
        await Poll(_first);
        await Follow(_first, "qobuz");
        _clock.UtcNow += TimeSpan.FromSeconds(3);
        _albums = [Album("qobuz"), Album("qobuz", "other", upc: null)];
        await Poll(_first);
        var inbox = await _service.ReadAsync(_first);
        Assert.Equal(5, inbox.Entries.Count);
        Assert.Equal(2, inbox.Entries.Single(item => item.Sources.Count == 2).Sources.Count);
    }

    [Fact]
    public async Task Complete_isrc_sets_group_across_providers_and_dismissals_survive_later_grouping()
    {
        await Follow(_first);
        _albums = [Album(upc: null, isrcs: ["USAAA2600001", "USAAA2600002"])];
        await Poll(_first);
        var entry = Assert.Single((await _service.ReadAsync(_first)).Entries);
        Assert.True(await _service.DismissAsync(_first, entry.Id));
        await Follow(_first, "qobuz");
        _clock.UtcNow += TimeSpan.FromSeconds(3);
        _albums = [Album("qobuz", upc: null, isrcs: ["USAAA2600002", "USAAA2600001"])];
        await Poll(_first);
        Assert.Empty((await _service.ReadAsync(_first)).Entries);
        Assert.False(await _service.DismissAsync(_second, entry.Id));
    }

    [Fact]
    public async Task Excluded_types_are_retained_and_dates_keep_their_precision()
    {
        await Follow(_first);
        _albums = [Album(id: "comp", exclusion: "compilation"), Album(id: "guest", exclusion: "appearance"),
            Album(id: "remaster", exclusion: "reissue"), Album(id: "ep", type: "ep", date: "2026-10"),
            Album(id: "single", type: "single", date: "2026-10-21"), Album(id: "old", date: "2026-08-01")];
        await Poll(_first);
        var inbox = await _service.ReadAsync(_first);
        Assert.Equal(["appearance", "compilation", "reissue"], inbox.Excluded.Select(item => item.ExclusionReason).Order(StringComparer.Ordinal));
        Assert.Equal(["single", "ep"], inbox.Entries.Select(item => item.Type));
        Assert.Equal("2026-10", inbox.Entries[1].ReleaseDate);
        Assert.Equal("month", inbox.Entries[1].DatePrecision);
    }

    [Fact]
    public async Task Polling_is_shared_cached_and_daily_schedule_uses_the_durable_queue()
    {
        await Follow(_first);
        await Follow(_second);
        _albums = [Album()];
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Poll(_first)).Kind);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await NewService().PollAsync(_second, Guid.CreateVersion7(), default)).Kind);
        Assert.Equal(1, _calls);
        Assert.Single((await _service.ReadAsync(_second)).Entries);
        var engine = new DurableScheduleEngine(_factory, _queue, _clock);
        Assert.Equal(2, (await engine.TickAsync()).Enqueued);
        Assert.Equal(0, (await engine.TickAsync()).Enqueued);
        await using (var db = _factory.CreateDbContext())
            Assert.All(await db.Jobs.ToArrayAsync(), job => Assert.Equal(10000, job.MaxDeferrals));
        _clock.UtcNow += TimeSpan.FromDays(1);
        await Poll(_second);
        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task Rate_limit_backoff_and_request_spacing_survive_service_restart()
    {
        await Follow(_first);
        await Follow(_first, artist: "b");
        _failure = new(ProviderErrorKind.RateLimited, TimeSpan.FromMinutes(10));
        var result = await Poll(_first);
        Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
        Assert.True(result.RetryDelay >= TimeSpan.FromMinutes(10));
        await NewService().PollAsync(_first, Guid.CreateVersion7(), default);
        Assert.Equal(1, _calls);
        _clock.UtcNow += TimeSpan.FromMinutes(10);
        _failure = null;
        _albums = [Album()];
        result = await Poll(_first);
        Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
        Assert.Equal(2, _calls);
        _clock.UtcNow += TimeSpan.FromSeconds(3);
        await Poll(_first);
        Assert.Equal(3, _calls);
    }

    [Fact]
    public async Task Follows_dismissals_and_clean_only_are_listener_scoped()
    {
        await Follow(_first);
        await Follow(_second);
        _albums = [Album(), Album(id: "explicit", explicitValue: true), Album(id: "unknown", explicitValue: null)];
        await Poll(_first);
        await Poll(_second);
        _settings.Setup(value => value.GetPreferencesAsync(_second, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new("CleanOnly"), new(), false, "2"));
        var clean = Assert.Single((await _service.ReadAsync(_second)).Entries);
        Assert.True(await _service.DismissAsync(_second, clean.Id));
        Assert.Empty((await _service.ReadAsync(_second)).Entries);
        Assert.Equal(3, (await _service.ReadAsync(_first)).Entries.Count);
        await _service.UnfollowAsync(_first, "deezer", "a");
        Assert.Empty(await _service.ListAsync(_first));
        Assert.Empty((await _service.ReadAsync(_first)).Entries);
        Assert.Single(await _service.ListAsync(_second));
        await using var db = _factory.CreateDbContext();
        (await db.Users.SingleAsync(item => item.Id == _second)).BackendInstanceId = "other";
        await db.SaveChangesAsync();
        Assert.Empty(await _service.ListAsync(_second));
    }

    [Fact]
    public async Task Missing_catalog_match_has_unknown_coverage()
    {
        await Follow(_first);
        _failure = new(ProviderErrorKind.NotFound);
        await Poll(_first);
        var inbox = await _service.ReadAsync(_first);
        Assert.Empty(inbox.Entries);
        Assert.Equal("unknown", Assert.Single(inbox.Coverage).Status);
        _clock.UtcNow += TimeSpan.FromDays(1);
        _failure = null;
        await Poll(_first);
        Assert.Equal("unknown", Assert.Single((await _service.ReadAsync(_first)).Coverage).Status);
    }

    [Fact]
    public async Task Hearted_artist_action_is_explicit_capped_and_uses_only_the_listener_accepted_hearts()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var job = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.process", "fixture-hearts", new { }, _first));
            var otherJob = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("favorite.process", "fixture-hearts", new { }, _second));
            for (var index = 0; index < 203; index++)
            {
                var owner = index == 202 ? _second : _first;
                var id = "ext-deezer-song-" + index;
                var display = new FavoriteDisplaySnapshot(1, id, "song", "Song", ["Artist " + index])
                { ArtistIds = ["ext-deezer-artist-" + index] };
                var accepted = new FavoriteEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    Protocol = "subsonic",
                    BackendInstanceId = "main",
                    BackendPrincipalId = owner.ToString(),
                    ItemId = id,
                    Operation = index == 201 ? FavoriteOperation.Unfavorite : FavoriteOperation.Favorite,
                    EventKey = index.ToString().PadLeft(64, '0'),
                    SourceRevision = index.ToString(),
                    JobId = owner == _first ? job.JobId : otherJob.JobId,
                    Revision = 1,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                };
                db.FavoriteEvents.Add(accepted);
                db.FavoriteStates.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    Protocol = "subsonic",
                    BackendInstanceId = "main",
                    ItemId = id,
                    IsFavorite = index != 201,
                    LastEventId = accepted.Id,
                    DisplayMetadataJson = JsonSerializer.Serialize(display, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Revision = 1,
                    UpdatedAt = _clock.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        Assert.Empty(await _service.ListAsync(_first));
        Assert.Equal(200, await _service.FollowHeartedArtistsAsync(_first));
        Assert.Equal(200, (await _service.ListAsync(_first)).Count);
        Assert.DoesNotContain(await _service.ListAsync(_first), item => item.ArtistId is "201" or "202");
        Assert.Empty(await _service.ListAsync(_second));
        _metadata.Verify(value => value.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()), Times.Never);
    }

    [Fact]
    public async Task Incomplete_pages_resume_after_backoff_and_refresh_on_the_next_calendar_day()
    {
        await Follow(_first);
        _metadata.Setup(value => value.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .Returns((ProviderExecutionContext context, ProviderArtistItemsRequest request) =>
            {
                _calls++;
                return Task.FromResult(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new(context.ProviderId,
                    [Album(id: request.Page.Cursor == null ? "first" : "second", upc: null)],
                    request.Page.Cursor == null ? "next" : null)));
            });
        Assert.Equal(DurableJobCompletionKind.Deferred, (await Poll(_first)).Kind);
        Assert.Equal("partial", Assert.Single((await _service.ReadAsync(_first)).Coverage).Status);
        _clock.UtcNow += TimeSpan.FromSeconds(3);
        await Poll(_first);
        Assert.Equal(2, (await _service.ReadAsync(_first)).Entries.Count);
        _clock.UtcNow = new DateTimeOffset(_clock.UtcNow.UtcDateTime.Date.AddDays(1).AddHours(4), TimeSpan.Zero);
        Assert.Equal(DurableJobCompletionKind.Deferred, (await Poll(_first)).Kind);
        _clock.UtcNow += TimeSpan.FromSeconds(3);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Poll(_first)).Kind);
        Assert.Equal(4, _calls);
    }

    [Fact]
    public async Task Api_uses_the_session_listener_and_requires_a_linked_identity()
    {
        var controller = new NewReleasesController(_service) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>(await controller.Inbox(default));
        var session = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            IsAdministrator = false,
            AllstarrUserId = _second,
            JellyfinAccessToken = "",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        Assert.IsType<NoContentResult>(await controller.Follow(new("deezer", "a", "Artist"), default));
        Assert.Empty(await _service.ListAsync(_first));
        Assert.Single(await _service.ListAsync(_second));
        Assert.IsType<BadRequestObjectResult>(await controller.Follow(new("deezer", "a", ""), default));
    }

    [Fact]
    public async Task Follow_keeps_only_an_existing_enriched_artist_identity()
    {
        var mbid = Guid.NewGuid().ToString();
        await using (var db = _factory.CreateDbContext())
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("a"))).ToLowerInvariant();
            var json = JsonSerializer.Serialize(new { ProviderIds = new Dictionary<string, string> { ["musicbrainzartist"] = mbid } });
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _first,
                ProviderId = "deezer",
                ResourceKind = "artist",
                ExternalIdHash = hash,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadJson = json,
                PayloadSha256 = new string('a', 64),
                Protocol = "subsonic",
                BackendInstanceId = "main",
                BackendPrincipalId = _first.ToString(),
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await _service.FollowAsync(_first, new("deezer", "a", "Artist", Guid.NewGuid().ToString()));
        await Follow(_second);
        Assert.Equal(mbid, Assert.Single(await _service.ListAsync(_first)).MusicBrainzArtistId);
        Assert.Null(Assert.Single(await _service.ListAsync(_second)).MusicBrainzArtistId);
    }

    [Fact]
    public async Task Guest_credits_are_recorded_as_appearances_and_title_editions_remain_distinct()
    {
        await Follow(_first);
        _albums = [Album(), new(new("deezer", ProviderResourceKind.Album, "guest"), "Guest release",
            [new("Other", new("deezer", ProviderResourceKind.Artist, "other")), new("Artist", new("deezer", ProviderResourceKind.Artist, "a"))],
            releaseDate: "2026-10-20", releaseType: "single", isExplicit: false),
            new(new("deezer", ProviderResourceKind.Album, "deluxe"), "Release (Deluxe)", [new("Artist")],
                releaseDate: "2026-10-20", releaseType: "album", upc: "0123456789012", isExplicit: false)];
        await Poll(_first);
        var inbox = await _service.ReadAsync(_first);
        Assert.Equal(2, inbox.Entries.Count);
        Assert.Equal("appearance", Assert.Single(inbox.Excluded).ExclusionReason);
    }

    [Fact]
    public async Task Proven_provider_aliases_group_with_normalized_provider_names()
    {
        await Follow(_first);
        _albums = [new(new("deezer", ProviderResourceKind.Album, "first"), "Release", [new("Artist")],
            releaseDate: "2026-10-20", releaseType: "album", isExplicit: false,
            providerIds: new Dictionary<string, string> { ["Apple"] = "apple:111" })];
        await Poll(_first);
        await Follow(_first, "qobuz");
        _albums = [new(new("qobuz", ProviderResourceKind.Album, "second"), "Release", [new("Artist")],
            releaseDate: "2026-10-20", releaseType: "album", isExplicit: false,
            providerIds: new Dictionary<string, string> { ["apple-musickit"] = "111" })];
        await Poll(_first);
        Assert.Equal(2, Assert.Single((await _service.ReadAsync(_first)).Entries).Sources.Count);
    }

    [Fact]
    public async Task A_provider_backoff_does_not_block_other_artist_catalogs()
    {
        await Follow(_first);
        await Follow(_first, "qobuz");
        _metadata.Setup(value => value.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .Returns((ProviderExecutionContext context, ProviderArtistItemsRequest _) => Task.FromResult(context.ProviderId == "deezer"
                ? ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Failure(new(ProviderErrorKind.RateLimited, TimeSpan.FromHours(1)))
                : ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("qobuz", [Album("qobuz")]))));
        Assert.Equal(DurableJobCompletionKind.Deferred, (await Poll(_first)).Kind);
        var inbox = await _service.ReadAsync(_first);
        Assert.Single(inbox.Entries);
        Assert.Equal("known", inbox.Coverage.Single(item => item.Artist.ProviderId == "qobuz").Status);
        Assert.Equal("unknown", inbox.Coverage.Single(item => item.Artist.ProviderId == "deezer").Status);
    }

    [Fact]
    public async Task A_release_is_included_when_its_primary_and_guest_artists_are_both_followed()
    {
        await Follow(_first, artist: "guest");
        await Follow(_first);
        _albums = [Album()];
        await Poll(_first);
        _clock.UtcNow += TimeSpan.FromSeconds(3);
        await Poll(_first);
        var inbox = await _service.ReadAsync(_first);
        Assert.Single(inbox.Entries);
        Assert.Empty(inbox.Excluded);
    }

    [Fact]
    public async Task Dismissed_release_stays_dismissed_when_a_proven_copy_replaces_the_original_source()
    {
        await Follow(_first);
        _albums = [Album(), Album(id: "explicit", explicitValue: true), Album(id: "deluxe", edition: "Deluxe")];
        await Poll(_first);
        var entry = (await _service.ReadAsync(_first)).Entries.Single(item => item.IsExplicit == false && item.Edition == "");
        Assert.True(await _service.DismissAsync(_first, entry.Id));
        await _service.UnfollowAsync(_first, "deezer", "a");
        await Follow(_first, "qobuz");
        _albums = [Album("qobuz"), Album("qobuz", "explicit", explicitValue: true), Album("qobuz", "deluxe", edition: "Deluxe")];
        await Poll(_first);
        var inbox = await _service.ReadAsync(_first);
        Assert.Equal(2, inbox.Entries.Count);
        Assert.All(inbox.Entries, item => Assert.True(item.IsExplicit == true || item.Edition == "Deluxe"));
    }

    [Fact]
    public async Task Personal_catalogs_and_account_revisions_do_not_share_cached_releases()
    {
        foreach (var owner in new[] { _first, _second })
        {
            _accounts[owner] = new(Guid.CreateVersion7(), "deezer", ProviderAccountScope.Personal, 1, ownerUserId: owner);
            await Follow(owner);
        }
        _albums = [Album()];
        await Poll(_first);
        Assert.Empty((await _service.ReadAsync(_second)).Entries);
        _albums = [Album(id: "second")];
        await Poll(_second);
        Assert.Equal(2, _calls);
        Assert.Equal("one", Assert.Single((await _service.ReadAsync(_first)).Entries).Sources.Single().AlbumId);
        var account = _accounts[_first];
        _accounts[_first] = new(account.AccountId, "deezer", ProviderAccountScope.Personal, 2, ownerUserId: _first);
        Assert.Empty((await _service.ReadAsync(_first)).Entries);
        _clock.UtcNow += TimeSpan.FromSeconds(3);
        await Poll(_first);
        Assert.Equal(3, _calls);
    }

    [Fact]
    public async Task Partial_dates_are_preserved_and_missing_dates_are_recorded_as_excluded()
    {
        await Follow(_first);
        _albums = [Album(id: "year", date: "2026"), Album(id: "undated", date: null),
            Album(id: "future", date: "2026-11-01")];
        await Poll(_first);
        var inbox = await _service.ReadAsync(_first);
        var entry = Assert.Single(inbox.Entries);
        Assert.Equal("2026", entry.ReleaseDate);
        Assert.Equal("year", entry.DatePrecision);
        Assert.Equal("date-unknown", Assert.Single(inbox.Excluded).ExclusionReason);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
