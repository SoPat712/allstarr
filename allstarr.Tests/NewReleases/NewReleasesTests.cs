using System.Net;
using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientEvents;
using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

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
    private readonly MediaAssetResolver _covers = new(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance);
    private readonly CoverHandler _coverSource = new();

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

    private NewReleasesService NewService(IClientEventPublisher? events = null) => new(_factory, _clock, _router.Object, _settings.Object, events);
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
    public async Task Shared_catalog_changes_publish_committed_inbox_counts_to_affected_listeners()
    {
        await Follow(_first);
        await Follow(_second);
        var published = new List<(Guid Owner, string Value)>();
        var publisher = new Mock<IClientEventPublisher>();
        publisher.Setup(item => item.Publish(It.IsAny<Guid>(), It.IsAny<ClientEvent>(), It.IsAny<string?>()))
            .Callback<Guid, ClientEvent, string?>((owner, value, device) =>
            {
                Assert.Null(device);
                using var db = _factory.CreateDbContext();
                Assert.True(db.ExternalMetadataSnapshots.Any(item => item.ResourceKind == "release-catalog"));
                published.Add((owner, JsonSerializer.Serialize(value)));
            });
        _service = NewService(publisher.Object);
        _albums = [Album()];
        await Poll(_first);
        Assert.Equal(new[] { _first, _second }.Order(), published.Select(item => item.Owner).Order());
        Assert.All(published, item => Assert.Equal(JsonSerializer.Serialize(ClientEvent.Releases(1)), item.Value));

        published.Clear();
        await Poll(_first);
        Assert.Empty(published);
        _clock.UtcNow += TimeSpan.FromDays(1);
        await Poll(_first);
        Assert.Empty(published);
        _clock.UtcNow += TimeSpan.FromDays(1);
        _failure = new(ProviderErrorKind.TransientFailure);
        await Poll(_first);
        Assert.Empty(published);
    }

    [Fact]
    public async Task Dismiss_follow_and_unfollow_publish_updated_counts_only_when_inbox_changes()
    {
        await Follow(_first);
        _albums = [Album()];
        await Poll(_first);
        var entry = Assert.Single((await _service.ReadAsync(_first)).Entries);
        var published = new List<(Guid Owner, string Value)>();
        var publisher = new Mock<IClientEventPublisher>();
        publisher.Setup(item => item.Publish(It.IsAny<Guid>(), It.IsAny<ClientEvent>(), It.IsAny<string?>()))
            .Callback<Guid, ClientEvent, string?>((owner, value, _) => published.Add((owner, JsonSerializer.Serialize(value))));
        _service = NewService(publisher.Object);

        Assert.False(await _service.DismissAsync(_second, entry.Id));
        Assert.Empty(published);
        await Follow(_second);
        Assert.Equal((_second, JsonSerializer.Serialize(ClientEvent.Releases(1))), Assert.Single(published));
        published.Clear();
        await Follow(_second);
        Assert.Empty(published);
        await _service.UnfollowAsync(_second, "deezer", "a");
        Assert.Equal((_second, JsonSerializer.Serialize(ClientEvent.Releases(0))), Assert.Single(published));

        published.Clear();
        Assert.True(await _service.DismissAsync(_first, entry.Id));
        Assert.Equal((_first, JsonSerializer.Serialize(ClientEvent.Releases(0))), Assert.Single(published));
        published.Clear();
        Assert.False(await _service.DismissAsync(_first, entry.Id));
        Assert.Empty(published);
    }

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
        Assert.IsType<NotFoundResult>(await controller.Tracks("not-in-the-inbox", default));
    }

    [Fact]
    public async Task Album_keep_counts_only_new_jobs_and_can_retry_changed_metadata_after_failure()
    {
        var song = ReleaseSong("track");
        var (controller, entry, _) = await KeepControllerAsync(true, [song]);
        var first = JsonSerializer.SerializeToElement(Assert.IsType<AcceptedResult>(
            await controller.KeepAlbum(entry, default)).Value);
        Assert.Equal(1, first.GetProperty("queued").GetInt32());
        Assert.Equal(0, first.GetProperty("skipped").GetInt32());

        var repeated = JsonSerializer.SerializeToElement(Assert.IsType<AcceptedResult>(
            await controller.KeepAlbum(entry, default)).Value);
        Assert.Equal(0, repeated.GetProperty("queued").GetInt32());
        Assert.Equal(1, repeated.GetProperty("skipped").GetInt32());
        await using (var db = _factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(item => item.Type == RecordingKeptFiles.ExplicitKeepJobType);
            job.State = DurableJobState.Failed;
            job.CompletedAt = _clock.UtcNow;
            job.UpdatedAt = _clock.UtcNow;
            job.Revision++;
            await db.SaveChangesAsync();
        }
        song.Title = "Changed title";
        song.Duration++;

        var retried = JsonSerializer.SerializeToElement(Assert.IsType<AcceptedResult>(
            await controller.KeepAlbum(entry, default)).Value);
        Assert.Equal(1, retried.GetProperty("queued").GetInt32());
        Assert.Equal(0, retried.GetProperty("skipped").GetInt32());
        await using var verify = _factory.CreateDbContext();
        var jobs = await verify.Jobs.Where(item => item.Type == RecordingKeptFiles.ExplicitKeepJobType).ToArrayAsync();
        Assert.Equal(2, jobs.Length);
        Assert.Equal(2, jobs.Select(item => item.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task Album_keep_skips_invalid_songs_and_continues()
    {
        var invalid = ReleaseSong("invalid");
        invalid.Track = 0;
        var (controller, entry, _) = await KeepControllerAsync(true, [invalid, ReleaseSong("valid", 2)]);

        var response = JsonSerializer.SerializeToElement(Assert.IsType<AcceptedResult>(
            await controller.KeepAlbum(entry, default)).Value);

        Assert.Equal(1, response.GetProperty("queued").GetInt32());
        Assert.Equal(1, response.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Album_keep_rejects_listeners_before_reading_provider_tracks()
    {
        var (controller, entry, gateway) = await KeepControllerAsync(false, [ReleaseSong("track")]);

        var response = Assert.IsType<ObjectResult>(await controller.KeepAlbum(entry, default));

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("Administrator", JsonSerializer.Serialize(response.Value));
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Add_to_apps_obeys_the_publish_setting_and_explains_large_releases()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var publish = false;
        _settings.Setup(item => item.GetAsync(PersonalMixService.PublishKey, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new EffectiveRuntimeSetting(PersonalMixService.PublishKey,
                RuntimeSettingValueType.Boolean, publish, publish.ToString(), RuntimeSettingOrigin.Durable, 1, null, null, null)));
        var controller = ReleaseController(gateway.Object, mixes: Mixes(gateway.Object), settings: _settings.Object);
        var refused = Assert.IsType<ObjectResult>(await controller.AddToApps("missing", default));
        Assert.Equal(403, refused.StatusCode);
        Assert.Contains("turned off", JsonSerializer.Serialize(refused.Value));
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        publish = true;
        await Follow(_first);
        _albums = [Album()];
        await Poll(_first);
        var entry = Assert.Single((await _service.ReadAsync(_first)).Entries);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(new Album { Songs = Enumerable.Range(1, 101).Select(index => ReleaseSong(index.ToString(), index)).ToList() });
        var tooLarge = Assert.IsType<ConflictObjectResult>(await controller.AddToApps(entry.Id, default));
        Assert.Contains("more than 100 songs", JsonSerializer.Serialize(tooLarge.Value));
    }

    [Fact]
    public async Task Add_to_apps_save_race_returns_a_retryable_conflict()
    {
        var response = Assert.IsType<ConflictObjectResult>(await NewReleasesController.SaveReleaseAsync(
            () => Task.FromException<PersonalMix>(new InvalidOperationException("Save again."))));

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("Try again", JsonSerializer.Serialize(response.Value));
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

    [Fact]
    public async Task Cached_releases_link_a_cover_from_their_source_album_without_polling_again()
    {
        await Follow(_first);
        _albums = [Album()];
        await Poll(_first);
        var entry = Assert.Single((await NewService().ReadAsync(_first)).Entries);
        Assert.Equal("/api/admin/releases/artwork/deezer/one", entry.ArtworkUrl);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Release_cover_uses_kept_album_details_before_the_source_and_is_kept_once_found()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "kept"))
            .ReturnsAsync(new Album { CoverArtUrl = "https://covers.example.test/kept.jpg" });
        gateway.Setup(item => item.GetAlbumAsync(It.Is<ProtocolExecutionContext>(context => context.VerifiedBackendPrincipalId == _first.ToString()),
                "deezer", "one"))
            .ReturnsAsync(new Album { CoverArtUrl = "https://covers.example.test/one.jpg" });
        var listener = Listener(_first, _first.ToString());
        Assert.Equal("image/png", Assert.IsType<FileContentResult>(await Artwork(gateway.Object, listener).Get("deezer", "kept", default)).ContentType);
        var controller = Artwork(gateway.Object, listener);
        Assert.IsType<FileContentResult>(await controller.Get("deezer", "one", default));
        Assert.Equal("private, max-age=604800", controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<FileContentResult>(await Artwork(gateway.Object, listener).Get("deezer", "one", default));
        Assert.Equal(["https://covers.example.test/kept.jpg", "https://covers.example.test/one.jpg"],
            _coverSource.Requests.Select(item => item.AbsoluteUri));
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "kept"), Times.Never);
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/api/admin/providers/deezer/cover")]
    [InlineData("http://127.0.0.1/cover.jpg")]
    public async Task Release_without_a_usable_cover_answers_empty_so_the_placeholder_stays(string? cover)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(new Album { CoverArtUrl = cover });
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "off"))
            .ThrowsAsync(new UnauthorizedAccessException());
        foreach (var album in new[] { "one", "off", "missing" })
        {
            var controller = Artwork(gateway.Object, Listener(_first, _first.ToString()));
            Assert.IsType<NoContentResult>(await controller.Get("deezer", album, default));
            Assert.Equal("private, max-age=3600", controller.Response.Headers.CacheControl.ToString());
        }
        Assert.Empty(_coverSource.Requests);
    }

    [Fact]
    public async Task Release_cover_requires_a_linked_enabled_listener()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        Assert.IsType<UnauthorizedResult>(await Artwork(gateway.Object, null).Get("deezer", "one", default));
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Artwork(gateway.Object, Listener(null, "unlinked")).Get("deezer", "one", default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Artwork(gateway.Object, Listener(_first, _second.ToString())).Get("deezer", "one", default)).StatusCode);
        Assert.IsType<BadRequestResult>(await Artwork(gateway.Object, Listener(_first, _first.ToString())).Get("deezer", "one\ntwo", default));
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == _second)).Enabled = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Artwork(gateway.Object, Listener(_second, _second.ToString())).Get("deezer", "one", default)).StatusCode);
        Assert.Empty(_coverSource.Requests);
    }

    private async Task<(NewReleasesController Controller, string EntryId, Mock<IProtocolProviderGateway> Gateway)>
        KeepControllerAsync(bool administrator, List<Song> songs)
    {
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == _first)).IsAdmin = administrator;
            await db.SaveChangesAsync();
        }
        await Follow(_first);
        _albums = [Album()];
        await Poll(_first);
        var entry = Assert.Single((await _service.ReadAsync(_first)).Entries);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(new Album { Songs = songs });
        gateway.Setup(item => item.RememberTrackAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var recordingIds = songs.ToDictionary(song => song.Id, _ => Guid.CreateVersion7());
        var identities = new Mock<ITrackIdentityService>();
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((ProviderActorContext _, string publicId, CancellationToken _) =>
                Task.FromResult<CanonicalRecordingIdentity?>(recordingIds.TryGetValue(publicId, out var id)
                    ? new(id, _first, null, null, 1) { PublicId = publicId }
                    : null));
        var workspace = new ProviderDownloadWorkspaceOptions
        { RootPath = Path.Combine(Path.GetTempPath(), "allstarr-release-keep", Guid.NewGuid().ToString("N")) };
        var artifacts = new ProviderDownloadArtifactResolver(new EfProviderDownloadArtifactStore(_factory), workspace);
        var placement = new ManagedTrackPlacementOptions
        {
            RootId = Guid.CreateVersion7(),
            RootPath = Path.Combine(workspace.RootPath, "kept"),
            PathTemplate = "{artist}/{title}"
        };
        var kept = new RecordingKeptFiles(_factory, artifacts, Mock.Of<IServiceScopeFactory>(), placement,
            identities.Object, _clock, NullLogger<RecordingKeptFiles>.Instance, _queue);
        return (ReleaseController(gateway.Object, kept: kept, administrator: administrator), entry.Id, gateway);
    }

    private NewReleasesController ReleaseController(IProtocolProviderGateway gateway, PersonalMixService? mixes = null,
        RecordingKeptFiles? kept = null, IDurableRuntimeSettings? settings = null, bool administrator = false)
    {
        var controller = new NewReleasesController(_service, new(_factory, _clock), gateway, mixes, kept, settings)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "release-actions",
            UserId = _first.ToString(),
            UserName = "Listener",
            IsAdministrator = administrator,
            BackendType = "subsonic",
            BackendInstanceId = "main",
            AllstarrUserId = _first,
            JellyfinAccessToken = "",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return controller;
    }

    private PersonalMixService Mixes(IProtocolProviderGateway gateway)
    {
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "Lossless", .07));
        var tags = new SharedTagStore(_factory, _clock);
        var taste = new TasteService(_factory, tags, _clock);
        return new(_factory, _clock, tags, taste, new MusicMixService(gateway), gateway, _service,
            _settings.Object, policies.Object, Mock.Of<IBackendLibraryAccessResolver>());
    }

    private static Song ReleaseSong(string id, int track = 1) => new()
    {
        Id = "ext-deezer-song-" + id,
        ExternalProvider = "deezer",
        ExternalId = id,
        Title = "Song " + id,
        Artist = "Artist",
        Album = "Album",
        Duration = 180,
        Track = track
    };

    private ReleaseArtworkController Artwork(IProtocolProviderGateway gateway, AdminAuthSession? session)
    {
        var controller = new ReleaseArtworkController(new(_factory, _clock), gateway, _covers, new CoverClients(_coverSource),
            NullLogger<ReleaseArtworkController>.Instance);
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        if (session != null) controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return controller;
    }

    private static AdminAuthSession Listener(Guid? user, string principal) => new()
    {
        SessionId = "fixture",
        UserId = principal,
        UserName = "Listener",
        IsAdministrator = false,
        BackendType = "subsonic",
        BackendInstanceId = "main",
        AllstarrUserId = user,
        JellyfinAccessToken = "",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero); }
    private sealed class CoverClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private sealed class CoverHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            using var bitmap = new SKBitmap(32, 32);
            bitmap.Erase(SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var content = new ByteArrayContent(png.ToArray());
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
