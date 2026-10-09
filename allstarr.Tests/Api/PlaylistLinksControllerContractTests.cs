using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Reflection;

namespace allstarr.Tests;

public sealed class PlaylistLinksControllerContractTests
{
    [Fact]
    public void Routes_MatchTheWebUiContract()
    {
        var controller = typeof(PlaylistLinksController);
        Assert.Equal("api/admin/playlist-links", controller.GetCustomAttributes(typeof(RouteAttribute), true)
            .Cast<RouteAttribute>().Single().Template);
        var expected = new Dictionary<string, string?>
        {
            [nameof(PlaylistLinksController.List)] = null,
            [nameof(PlaylistLinksController.Details)] = "{id:guid}",
            [nameof(PlaylistLinksController.Create)] = null,
            [nameof(PlaylistLinksController.Update)] = "{id:guid}",
            [nameof(PlaylistLinksController.Refresh)] = "{id:guid}/refresh",
            [nameof(PlaylistLinksController.Preview)] = "{id:guid}/preview",
            [nameof(PlaylistLinksController.Run)] = "{id:guid}/run",
            [nameof(PlaylistLinksController.PreviewRematch)] = "rematch/preview",
            [nameof(PlaylistLinksController.ApplyRematch)] = "rematch/apply",
            [nameof(PlaylistLinksController.PreviewSourceUpdate)] = "{id:guid}/source-update/preview",
            [nameof(PlaylistLinksController.ApplySourceUpdate)] = "{id:guid}/source-update/apply",
            [nameof(PlaylistLinksController.SetOverride)] = "matches/{externalSnapshotId:guid}/override",
            [nameof(PlaylistLinksController.ClearOverride)] = "matches/overrides/{overrideId:guid}",
            [nameof(PlaylistLinksController.CreateSchedule)] = "{id:guid}/schedules",
            [nameof(PlaylistLinksController.UpdateSchedule)] = "schedules/{scheduleId:guid}",
        };
        foreach (var (name, template) in expected)
        {
            var method = controller.GetMethod(name)!;
            var route = method.GetCustomAttributes(true).OfType<HttpMethodAttribute>().Single();
            Assert.Equal(template, route.Template);
        }
    }

    [Fact]
    public async Task List_RejectsMissingOrUnlinkedAdminSessionBeforeStorageAccess()
    {
        var controller = Controller();
        Assert.IsType<UnauthorizedObjectResult>(await controller.List(CancellationToken.None));

        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "backend-user",
            UserName = "User",
            IsAdministrator = false,
            JellyfinAccessToken = "not-used-by-playlist-api",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        var forbidden = Assert.IsType<ObjectResult>(await controller.List(CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public void List_IsScopedByAuthenticatedOwnerWithoutRequestScopeIds()
    {
        var parameters = typeof(PlaylistLinksController).GetMethod(nameof(PlaylistLinksController.List))!
            .GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(CancellationToken), parameters[0].ParameterType);
    }

    [Theory]
    [InlineData("active", StatusCodes.Status200OK)]
    [InlineData("listener", StatusCodes.Status403Forbidden)]
    [InlineData("revoked", StatusCodes.Status409Conflict)]
    [InlineData("disabled", StatusCodes.Status409Conflict)]
    [InlineData("wrong-instance", StatusCodes.Status409Conflict)]
    [InlineData("wrong-purpose", StatusCodes.Status409Conflict)]
    [InlineData("foreign-owner", StatusCodes.Status409Conflict)]
    [InlineData("forged-principal", StatusCodes.Status403Forbidden)]
    public async Task Update_SubsonicLinkUsesOwnersConsentOnlyForAuthorizedManagement(string scenario, int expectedStatus)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var now = DateTimeOffset.UtcNow;
        var administrator = new UserRecord
        {
            Id = Guid.CreateVersion7(),
            BackendType = "subsonic",
            BackendInstanceId = "backend",
            BackendPrincipalId = "administrator-a",
            DisplayName = "Administrator A",
            Enabled = true,
            IsAdmin = scenario != "listener",
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        };
        var owner = new UserRecord
        {
            Id = Guid.CreateVersion7(),
            BackendType = "subsonic",
            BackendInstanceId = scenario == "wrong-instance" ? "other-backend" : "backend",
            BackendPrincipalId = "listener-b",
            DisplayName = "Listener B",
            Enabled = scenario != "disabled",
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        };
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner.Id,
            ProviderId = "spotify",
            DisplayName = "Owner source",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var grant = new SecretReferenceRecord
        {
            Id = Guid.CreateVersion7(),
            UserId = scenario == "foreign-owner" ? administrator.Id : owner.Id,
            Purpose = scenario == "wrong-purpose" ? "unrelated" : BackendCredentialScope.SubsonicPurpose,
            ActiveVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
            RevokedAt = scenario == "revoked" ? now : null
        };
        var link = new PlaylistLinkRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner.Id,
            ProviderAccountId = account.Id,
            SourceProviderId = "spotify",
            SourcePlaylistId = "source-list",
            SourcePlaylistIdHash = new string('a', 64),
            TargetProtocol = "subsonic",
            TargetBackendInstanceId = "backend",
            TargetPlaylistId = "target-list",
            Mode = PlaylistLinkMode.Materialized,
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            RuleVersion = "rules",
            PolicyVersion = "policy",
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.AddRange(administrator, owner);
            db.ProviderAccounts.Add(account);
            db.SecretReferences.Add(grant);
            db.PlaylistLinks.Add(link);
            await db.SaveChangesAsync();
        }
        var persistence = new Mock<IPlaylistPersistenceService>(MockBehavior.Strict);
        persistence.Setup(service => service.UpdateLinkAsync(It.IsAny<ProtocolExecutionContext>(), link.Id,
                It.IsAny<PlaylistLinkUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<ProtocolExecutionContext, Guid, PlaylistLinkUpdate, CancellationToken>((execution, _, update, _) =>
            {
                Assert.Equal(administrator.Id, execution.RequireActor().EffectiveUserId);
                Assert.Equal(owner.Id, link.OwnerUserId);
                Assert.Equal(grant.Id, update.TargetCredentialReferenceId);
            })
            .ReturnsAsync(link);
        var clock = new SystemPlatformClock();
        var controller = Controller(factory, persistence.Object, clock, new AdminProtocolExecutionContextFactory(factory, clock));
        var caller = scenario == "forged-principal" ? owner : administrator;
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            AllstarrUserId = caller.Id,
            UserId = scenario == "forged-principal" ? "forged-listener" : caller.BackendPrincipalId,
            UserName = caller.DisplayName,
            BackendType = "Subsonic",
            BackendInstanceId = "backend",
            IsAdministrator = caller.IsAdmin,
            JellyfinAccessToken = string.Empty,
            ExpiresAtUtc = now.UtcDateTime.AddHours(1)
        };
        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.Update(link.Id,
            new UpdatePlaylistLinkRequest(1, "materialized", "reconcile", null, "target-list", null,
                false, true, true, true, true), CancellationToken.None));
        Assert.Equal(expectedStatus, result.StatusCode);
        persistence.Verify(service => service.UpdateLinkAsync(It.IsAny<ProtocolExecutionContext>(), link.Id,
                It.IsAny<PlaylistLinkUpdate>(), It.IsAny<CancellationToken>()),
            scenario == "active" ? Times.Once() : Times.Never());
        persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void ListAndDetailsShareTheDurableProjectionReader()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "PlaylistLinksController.cs"));
        var playback = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Playlists", "PlaylistVirtualizationService.cs"));

        Assert.Contains("projections.ReadByLinkIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("projections.ReadByLinkIdAsync", source, StringComparison.Ordinal);
        Assert.Contains("playlists.ReadPreviewAsync", source, StringComparison.Ordinal);
        Assert.Contains("virtualization.ReadAsync", source, StringComparison.Ordinal);
        Assert.Contains("projections.ReadByLinkIdAsync", playback, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackClassifier.Classify", playback, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMetrics(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestContracts_ContainStableReferencesAndNoRawCredentialFields()
    {
        var create = typeof(CreatePlaylistLinkRequest).GetProperties().Select(item => item.Name).ToArray();
        Assert.Contains("TargetCredentialReferenceId", create);
        Assert.Contains("ProjectionMode", create);
        Assert.Contains("ImportMode", create);
        Assert.Contains("TrackRetention", create);
        Assert.Equal(
            "resolved",
            typeof(CreatePlaylistLinkRequest).GetConstructors().Single().GetParameters()
                .Single(item => item.Name == "ProjectionMode").DefaultValue);
        Assert.Equal(
            new[] { PlaylistProjectionMode.Resolved, PlaylistProjectionMode.Source, PlaylistProjectionMode.Target },
            Enum.GetValues<PlaylistProjectionMode>());
        Assert.Equal("linked", typeof(CreatePlaylistLinkRequest).GetConstructors().Single().GetParameters()
            .Single(item => item.Name == "ImportMode").DefaultValue);
        Assert.Equal("onDemand", typeof(CreatePlaylistLinkRequest).GetConstructors().Single().GetParameters()
            .Single(item => item.Name == "TrackRetention").DefaultValue);
        var controllerSource = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "PlaylistLinksController.cs"));
        Assert.Contains("ProjectionMode target requires TargetPlaylistId", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain(create, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                                               name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                                               name.Contains("Cookie", StringComparison.OrdinalIgnoreCase) ||
                                               name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Decision", "LibraryTrackId", "Reason", "ExpectedAuthority" },
            typeof(SetMatchOverrideRequest).GetProperties().Select(item => item.Name));
    }

    [Fact]
    public void PreviewContract_ExposesSourceMetadataRouteEligibilityAndOneOutcomeCode()
    {
        var row = new PersistedPlaylistPreviewEntry(
            0,
            Guid.NewGuid(),
            TrackMatchState.Accepted,
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            "source-entry",
            new PlaylistSourceIdentity("spotify", Guid.NewGuid(), "source-hash", "source-revision", 1, "provider-track"),
            new PlaylistSourceMetadata("Title", ["Artist"], "Album", 1234, "spotify", "isrc", false, "artwork-key"),
            new PlaylistResolvedRoute(TrackRouteKind.Local, Guid.NewGuid(), "backend-item", "backend", "jellyfin", "music"),
            true,
            PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem,
            0,
            PlaylistPreviewEntryStatus.Included);
        var preview = new PlaylistPreview(Guid.NewGuid(), Guid.NewGuid(), "List", null, null, [row])
        {
            SourceRevision = "source-revision",
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "backend"
        };
        var method = typeof(PlaylistLinksController).GetMethod(
            "ToPreviewDto", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(method.Invoke(null, [preview])));
        var value = json.RootElement;
        var entry = Assert.Single(value.GetProperty("entries").EnumerateArray());
        Assert.Equal("source-revision", value.GetProperty("sourceRevision").GetString());
        Assert.Equal("spotify", entry.GetProperty("sourceIdentity").GetProperty("providerId").GetString());
        Assert.Equal("provider-track", entry.GetProperty("sourceIdentity").GetProperty("externalId").GetString());
        Assert.Equal("Title", entry.GetProperty("sourceMetadata").GetProperty("title").GetString());
        Assert.Equal("local", entry.GetProperty("resolvedRoute").GetProperty("kind").GetString());
        Assert.True(entry.GetProperty("targetEligible").GetBoolean());
        Assert.Equal("included_native_backend_item", entry.GetProperty("outcomeCode").GetString());
        Assert.False(entry.TryGetProperty("inclusionReason", out _));
    }

    [Fact]
    public void DetailsContract_SeparatesClientOrderFromSourceMetricsAndShowsEligibility()
    {
        var linkId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var externalSnapshotId = Guid.NewGuid();
        var projection = new DurablePlaylistProjection(
            linkId, snapshotId, 1, "Source name", "spotify", "source-list",
            Guid.NewGuid(), "jellyfin", "target-list", null, DateTimeOffset.UtcNow,
            null, null,
            [new(0, externalSnapshotId, "source-track", "Source title", ["Artist"], null,
                null, 1_000, null, null, TrackMatchState.Accepted, "native-a", "local", null, [])]);
        var client = new VirtualPlaylistReadModel(
            PlaylistVirtualizationService.CreateProtocolId(linkId), linkId, snapshotId,
            "Client name", null, null, "spotify", "source-list", "revision-1",
            PlaylistLinkMode.Hybrid,
            [new(0, "native-a", "Native title", "Artist", null, null, 1_000, null,
                TrackMatchState.Accepted)],
            PlaylistProjectionMode.Target,
            "target-list");
        IReadOnlyDictionary<int, PersistedPlaylistPreviewEntry> preview =
            new Dictionary<int, PersistedPlaylistPreviewEntry>
            {
                [0] = new(0, externalSnapshotId, TrackMatchState.Accepted, Guid.NewGuid(), null,
                    TargetEligible: false,
                    OutcomeCode: PlaylistMaterializationOutcomeCodes.SkippedWrongBackendOrLibrary,
                    Status: PlaylistPreviewEntryStatus.WrongBackend)
            };
        var method = typeof(PlaylistLinksController).GetMethod(
            "ToProjectionDto", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            method.Invoke(null, new object?[] { projection, null, client, preview })));

        Assert.Equal(1, json.RootElement.GetProperty("trackCount").GetInt32());
        var clientProjection = json.RootElement.GetProperty("clientProjection");
        Assert.Equal("target", clientProjection.GetProperty("projectionMode").GetString());
        Assert.Equal("native-a", clientProjection.GetProperty("tracks")[0].GetProperty("itemId").GetString());
        var track = json.RootElement.GetProperty("tracks")[0];
        Assert.False(track.GetProperty("targetEligible").GetBoolean());
        Assert.Equal(PlaylistMaterializationOutcomeCodes.SkippedWrongBackendOrLibrary,
            track.GetProperty("outcomeCode").GetString());
    }

    [Fact]
    public void SourceDiscovery_ExcludesNonOperationalProvidersAndUsesPersonalOrSharedAccounts()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "PlaylistLinksController.cs"));

        Assert.Contains("includeNonOperational: false", source, StringComparison.Ordinal);
        Assert.Contains("item.OwnerUserId == null || item.OwnerUserId == session.AllstarrUserId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowsGlobalAccount", source, StringComparison.Ordinal);
        Assert.Contains("session.IsAdministrator", source, StringComparison.Ordinal);
        Assert.Contains("Response.Headers.RetryAfter", source, StringComparison.Ordinal);
        Assert.Contains("retryAfterSeconds", source, StringComparison.Ordinal);
        Assert.Contains("BuildProviderPlaylistDiscoveryKey", source, StringComparison.Ordinal);
        Assert.Contains("PlaylistDiscoveryPageCacheEntry", source, StringComparison.Ordinal);
        Assert.Contains("requestCoalescer.RunAsync", source, StringComparison.Ordinal);
        Assert.Contains("applicationCache.SetAsync(", source, StringComparison.Ordinal);
        Assert.Contains("ImageConditionalRequestHelper.ComputeStrongETag(asset.Bytes)", source, StringComparison.Ordinal);
        Assert.Contains("ImageConditionalRequestHelper.MatchesIfNoneMatch(Request.Headers, etag)", source, StringComparison.Ordinal);
        Assert.Contains("ProviderOrderPolicyCatalog.Find(ProviderCapabilityKind.Playlist)", source,
            StringComparison.Ordinal);
        Assert.Contains("effectivePolicies.ResolveForUserAsync(session.AllstarrUserId!.Value, cancellationToken)", source,
            StringComparison.Ordinal);
        Assert.Contains("effectivePolicy?.ApplyProviderAvailability(", source, StringComparison.Ordinal);
        Assert.Contains("configuredProviderOrder.GetValueOrDefault", source, StringComparison.Ordinal);
        Assert.DoesNotContain("value.Artwork?.PublicUri", source, StringComparison.Ordinal);
        Assert.Equal(2, source.Split("new MediaAssetIdentity(", StringSplitOptions.None).Length - 1);
    }

    private static PlaylistLinksController Controller(
        IDbContextFactory<AllstarrDbContext>? contextFactory = null,
        IPlaylistPersistenceService? playlists = null,
        IPlatformClock? clock = null,
        AdminProtocolExecutionContextFactory? protocolContexts = null)
    {
        var controller = new PlaylistLinksController(
            contextFactory!, playlists!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, clock!, null!, protocolContexts!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }


    private static string FindRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(segments)} from {AppContext.BaseDirectory}.");
    }
}
