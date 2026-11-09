using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Theory]
    [InlineData(true, false, "admins", true)]
    [InlineData(true, false, "admins-and-trusted-listeners", true)]
    [InlineData(false, true, "admins", false)]
    [InlineData(false, true, "admins-and-trusted-listeners", true)]
    [InlineData(false, false, "admins", false)]
    [InlineData(false, false, "admins-and-trusted-listeners", false)]
    public async Task PromotionStart_UsesStoredRolesAndHouseholdApprovalSetting(bool admin, bool trusted, string setting, bool direct)
    {
        await SetPromotionRoleAsync(admin, trusted);
        var administrator = await AddPromotionUserAsync(true);
        await new PromotionProfiles(factory).SaveHouseholdAsync(administrator, new(new(), setting));
        var batch = RequestService();
        Assert.Equal(direct, (await batch.PreviewAsync(Context, Request())).CanAddDirectly);
        var result = await batch.StartOrRequestAsync(Context, Request());
        Assert.Equal(direct, result.Run != null);
        Assert.Equal(!direct, result.Request != null);
        await using var db = factory.CreateDbContext();
        Assert.Equal(direct ? 1 : 0, await db.Set<PromotionRunRecord>().CountAsync());
        if (!direct)
        {
            var pending = Assert.Single(await db.PromotionRequests.ToArrayAsync());
            Assert.Equal("pending", pending.State);
            Assert.Equal("album", pending.Unit);
            Assert.Equal("Album", pending.Title);
            Assert.Equal("Artist", pending.Artist);
            Assert.Equal(2022, pending.Year);
            Assert.Equal(1, pending.SongCount);
            Assert.Equal("cover-reference", pending.CoverReference);
        }
    }

    [Fact]
    public async Task PromotionRequests_DedupeAcrossKeysAndSurviveServiceRestart()
    {
        await SetPromotionRoleAsync(false, false);
        var batch = RequestService();
        var first = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var second = (await RequestService().StartOrRequestAsync(Context, Request("another-key"))).Request!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, Assert.Single(await batch.ListRequestsAsync(Context, "pending")).Id);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<PromotionRunRecord>().ToArrayAsync());
        Assert.Single(await db.PromotionRequests.ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_ExplicitSongAndPrivateReceiptsRemainIndependent()
    {
        await SetPromotionRoleAsync(false, false);
        var other = await AddPromotionUserAsync(false);
        var batch = RequestService();
        var album = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var song = (await batch.StartOrRequestAsync(other, Request("other") with { Unit = "song" })).Request!;
        Assert.Equal("song", song.Unit);
        Assert.Equal("Song", song.Title);
        Assert.NotEqual(album.Id, song.Id);
        Assert.Equal(song.Id, Assert.Single(await batch.ListRequestsAsync(other, null)).Id);
        Assert.Equal(album.Id, Assert.Single(await batch.ListRequestsAsync(Context, null)).Id);
        Assert.Equal(album.Id, (await batch.StartOrRequestAsync(Context, Request("song") with { Unit = "song" })).Request!.Id);
    }

    [Fact]
    public async Task PromotionRequests_RevokedRequesterLibraryAccessPreventsApproval()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.ApproveRequestAsync(administrator, pending.Id));
        await using var verify = factory.CreateDbContext();
        Assert.Equal("pending", (await verify.PromotionRequests.SingleAsync()).State);
        Assert.Empty(await verify.Set<PromotionRunRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_DirectStartReturnsCoveringRunAcrossRequestKeys()
    {
        var batch = RequestService();
        var first = await batch.StartAsync(Context, Request());
        Assert.Equal(first.Id, (await batch.StartOrRequestAsync(Context, Request("same-album"))).Run!.Id);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<PromotionRunRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_TrustedListenerReusesSharedRunWithPrivateStatusReceipt()
    {
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var running = await batch.StartAsync(administrator, Request("admin-start"));
        var result = await batch.StartOrRequestAsync(Context, Request());
        Assert.Equal(running.Id, result.Run!.Id);
        Assert.Empty(result.Run.Tracks);
        Assert.Equal(string.Empty, result.Run.RootPath);
        var receipt = Assert.Single(await batch.ListRequestsAsync(Context, null));
        Assert.Equal(owner, receipt.RequesterUserId);
        Assert.Equal("approved", receipt.State);
        Assert.Equal(running.Id, receipt.RunId);
        Assert.Equal(running.Id, (await batch.StartOrRequestAsync(Context, Request("repeat"))).Run!.Id);
        Assert.Equal(receipt.Id, Assert.Single(await batch.ListRequestsAsync(Context, null)).Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.GetAsync(Context, running.Id));
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<PromotionRunRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_ConcurrentStatusReadsRecordOneCompletionEvent()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var approved = await batch.ApproveRequestAsync(administrator, pending.Id);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<PromotionRunRecord>().SingleAsync(item => item.Id == approved.RunId)).Outcome = "Failed";
            await db.SaveChangesAsync();
        }
        var reads = await Task.WhenAll(batch.GetRequestAsync(Context, pending.Id), RequestService().GetRequestAsync(Context, pending.Id));
        Assert.All(reads, request => Assert.Equal("failed", request.State));
        await using var verify = factory.CreateDbContext();
        Assert.Single(await verify.AuditEvents.Where(item => item.Category == "library-promotion" && item.Action == "request-failed").ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_ApprovalUsesRequestersCurrentProfileAndRecordsApproverOnce()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        await new PromotionProfiles(factory).SaveMineAsync(Context, new() { NamingScript = "Requester/%title%.%_extension%" });
        await new PromotionProfiles(factory).SaveMineAsync(administrator, new() { NamingScript = "Administrator/%title%.%_extension%" });
        var approved = await batch.ApproveRequestAsync(administrator, pending.Id);
        Assert.Equal("approved", approved.State);
        Assert.Equal(administrator.Principal!.UserId, approved.DecidedByUserId);
        Assert.NotNull(approved.DecidedAt);
        Assert.Equal(approved.RunId, (await batch.ApproveRequestAsync(administrator, pending.Id)).RunId);
        await using var db = factory.CreateDbContext();
        var run = Assert.Single(await db.Set<PromotionRunRecord>().ToArrayAsync());
        Assert.Equal(owner, run.OwnerUserId);
        Assert.Equal(owner, (await db.Jobs.SingleAsync(item => item.Id == run.JobId)).OwnerUserId);
        Assert.Contains("Requester/", run.PlanJson);
        Assert.DoesNotContain("Administrator/", run.PlanJson);
        Assert.Null(run.ApprovedByUserId);
        var notice = Assert.Single(await db.AuditEvents.Where(item => item.Category == "library-promotion").ToArrayAsync());
        Assert.Equal(owner, notice.ActorUserId);
        Assert.Contains("approved", notice.DetailsJson);
        Assert.Equal(administrator.Principal.UserId, JsonDocument.Parse(notice.DetailsJson).RootElement.GetProperty("decidedByUserId").GetGuid());
    }

    [Fact]
    public async Task PromotionRequests_ApprovalUsesDurableTracklistWhenCatalogIsUnavailable()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var second = Guid.NewGuid();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "First", Album = "Album" },
                new Song { CanonicalRecordingId = second, Title = "Second", Album = "Album" } });
        var batch = BatchService(catalog.Object, Mock.Of<IPromotionAcquisition>());
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        Assert.Equal(2, pending.SongCount);
        catalog.Reset();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The catalog is unavailable."));
        var approved = await batch.ApproveRequestAsync(administrator, pending.Id);
        var run = await batch.GetAsync(Context, approved.RunId!.Value);
        Assert.Equal(new[] { recordingId, second }, run.Tracks.Select(track => track.RecordingId));
        Assert.Equal(owner, run.OwnerUserId);
    }

    [Fact]
    public async Task PromotionRequests_DeclineRetainsShortNoteAndNotifiesOnlyRequester()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        await Assert.ThrowsAsync<ArgumentException>(() => batch.DeclineRequestAsync(administrator, pending.Id, new string('x', 501)));
        var declined = await batch.DeclineRequestAsync(administrator, pending.Id, "  We already have this edition.  ");
        Assert.Equal("declined", declined.State);
        Assert.Equal("We already have this edition.", declined.DeclineNote);
        await Assert.ThrowsAsync<PromotionException>(() => batch.ApproveRequestAsync(administrator, pending.Id));
        await using var db = factory.CreateDbContext();
        var notice = Assert.Single(await db.AuditEvents.Where(item => item.Category == "library-promotion").ToArrayAsync());
        Assert.Equal(owner, notice.ActorUserId);
        Assert.Contains("We already have this edition.", notice.DetailsJson);
        Assert.Empty(await db.Set<PromotionRunRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_CancelOwnPendingAndAllowNewRequest()
    {
        await SetPromotionRoleAsync(false, false);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var cancelled = await batch.CancelRequestAsync(Context, pending.Id);
        Assert.Equal("cancelled", cancelled.State);
        Assert.Equal(owner, cancelled.DecidedByUserId);
        Assert.NotEqual(pending.Id, (await batch.StartOrRequestAsync(Context, Request("again"))).Request!.Id);
        Assert.Single(await batch.ListRequestsAsync(Context, "cancelled"));
    }

    [Fact]
    public async Task PromotionRequests_ListenersCannotReadCancelOrDecideAnotherListenersRequest()
    {
        await SetPromotionRoleAsync(false, false);
        var other = await AddPromotionUserAsync(false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        Assert.Empty(await batch.ListRequestsAsync(other, null));
        Assert.Single(await batch.ListRequestsAsync(administrator, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.GetRequestAsync(other, pending.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.CancelRequestAsync(other, pending.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.ApproveRequestAsync(other, pending.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.DeclineRequestAsync(other, pending.Id, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.ApproveRequestAsync(Context, pending.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.DeclineRequestAsync(Context, pending.Id, null));
        await batch.ApproveRequestAsync(administrator, pending.Id);
        await Assert.ThrowsAsync<PromotionException>(() => batch.CancelRequestAsync(Context, pending.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => batch.ListRequestsAsync(Context, "unknown"));
    }

    [Fact]
    public async Task PromotionRequests_ApprovalLinksCoveringRunWithoutExposingItsOwner()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var running = await batch.StartAsync(administrator, Request("admin-start"));
        var approved = await batch.ApproveRequestAsync(administrator, pending.Id);
        Assert.Equal(running.Id, approved.RunId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.GetAsync(Context, running.Id));
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<PromotionRunRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_ApprovalRecognizesSharedReleaseAcrossListenerRecordingIdentities()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var secondRecording = Guid.NewGuid();
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .Returns<ProtocolExecutionContext, Guid, string, CancellationToken>((context, _, _, _) => Task.FromResult<IReadOnlyList<Song>>(
                [new() { CanonicalRecordingId = context.Principal!.UserId == owner ? recordingId : secondRecording,
                    Title = "Song", Album = "Album", AlbumId = "release", ExternalProvider = "source" }]));
        var batch = BatchService(catalog.Object, Mock.Of<IPromotionAcquisition>());
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var running = await batch.StartAsync(administrator, Request("admin-start"));
        Assert.Equal(running.Id, (await batch.ApproveRequestAsync(administrator, pending.Id)).RunId);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<PromotionRunRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task PromotionRequests_AlreadyInLibraryDoesNotAcquireOrQueueAnotherRun()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                BackendInstanceId = "backend",
                BackendLibraryId = "music",
                BackendItemId = "existing",
                CanonicalRecordingId = recordingId,
                Protocol = "jellyfin",
                Title = "Song"
            });
            await db.SaveChangesAsync();
        }
        var result = await batch.ApproveRequestAsync(administrator, pending.Id);
        Assert.Equal("added", result.State);
        Assert.Null(result.RunId);
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Set<PromotionRunRecord>().ToArrayAsync());
        Assert.Equal(2, await verify.AuditEvents.CountAsync(item => item.Category == "library-promotion"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionRequests_RunCompletionNotifiesOnce(bool fail)
    {
        await SetPromotionRoleAsync(false, false);
        await SaveBatchProfileAsync();
        var administrator = await AddPromotionUserAsync(true);
        var source = await WaveSourceAsync();
        var acquisition = new Mock<IPromotionAcquisition>();
        acquisition.Setup(item => item.AcquireAsync(owner, It.IsAny<Guid>(), It.IsAny<PromotionTrack>(), It.IsAny<PromotionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionAcquisitionResult(source, false, "Verified source."));
        var batch = RequestService(acquisition.Object);
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var approved = await batch.ApproveRequestAsync(administrator, pending.Id);
        if (fail) Directory.Delete(Path.Combine(directory, "library", "music"));
        await ExecuteBatchAsync(batch);
        var completed = await batch.GetRequestAsync(Context, pending.Id);
        Assert.Equal(fail ? "failed" : "added", completed.State);
        await batch.GetRequestAsync(Context, pending.Id);
        await using var db = factory.CreateDbContext();
        var notices = await db.AuditEvents.Where(item => item.Category == "library-promotion").ToArrayAsync();
        Assert.Equal(2, notices.Length);
        Assert.All(notices, item => Assert.Equal(owner, item.ActorUserId));
        Assert.Contains(notices, item => item.DetailsJson.Contains(fail ? "could not be added" : "added to your library", StringComparison.Ordinal));
        Assert.NotNull(approved.RunId);
    }

    [Fact]
    public async Task PromotionRequests_StartEndpointReturnsAcceptedRequestForOrdinaryListener()
    {
        await SetPromotionRoleAsync(false, false);
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            AllstarrUserId = owner,
            UserId = "principal",
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            SessionId = "request-session",
            UserName = "Owner",
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var controller = new LibraryPromotionsController(RequestService(), roots, new(factory, new SystemPlatformClock()))
        { ControllerContext = new() { HttpContext = http } };
        var accepted = Assert.IsType<AcceptedResult>(await controller.Promote(Request(), default));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(accepted.Value));
        Assert.True(json.RootElement.GetProperty("requested").GetBoolean());
        Assert.Equal("pending", json.RootElement.GetProperty("request").GetProperty("State").GetString());
        Assert.DoesNotContain("RequestJson", json.RootElement.GetRawText());
        Assert.DoesNotContain("SongsJson", json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromotionRequests_ControllerChecksStoredIdentityAndReturnsOnlySafeReceipts(bool administrator)
    {
        await SetPromotionRoleAsync(administrator, !administrator);
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            AllstarrUserId = owner,
            UserId = "principal",
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            SessionId = "request-session",
            UserName = "Owner",
            IsAdministrator = administrator,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var controller = new LibraryPromotionsController(RequestService(), roots, new(factory, new SystemPlatformClock()))
        { ControllerContext = new() { HttpContext = http } };
        using (var direct = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<AcceptedResult>(await controller.Promote(Request(), default)).Value)))
        {
            Assert.True(direct.RootElement.TryGetProperty("JobId", out _));
            Assert.False(direct.RootElement.TryGetProperty("requested", out _));
        }
        await using (var db = factory.CreateDbContext())
        {
            var run = await db.Set<PromotionRunRecord>().SingleAsync();
            run.Outcome = "Cancelled";
            await db.SaveChangesAsync();
        }
        using var requested = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<AcceptedResult>(
            await controller.CreateRequest(Request("explicit-request"), default)).Value));
        var id = requested.RootElement.GetProperty("request").GetProperty("Id").GetGuid();
        Assert.True(requested.RootElement.GetProperty("requested").GetBoolean());
        if (!administrator)
        {
            Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.ApproveRequest(id, default)).StatusCode);
            Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.DeclineRequest(id, null, default)).StatusCode);
        }
        http.Items.Remove(AdminAuthSessionService.HttpContextSessionItemKey);
        Assert.IsType<UnauthorizedResult>(await controller.Requests(null, default));
    }

    private LibraryPromotionService RequestService(IPromotionAcquisition? acquisition = null)
    {
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), recordingId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song", Artist = "Artist", Album = "Album", Year = 2022,
                AlbumId = "release", ExternalProvider = "source", CoverArtUrl = "cover-reference" } });
        return BatchService(catalog.Object, acquisition ?? Mock.Of<IPromotionAcquisition>());
    }

    private async Task SetPromotionRoleAsync(bool admin, bool trusted)
    {
        await using var db = factory.CreateDbContext();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        user.IsAdmin = admin;
        user.IsTrustedListener = trusted;
        await db.SaveChangesAsync();
    }

    private async Task<ProtocolExecutionContext> AddPromotionUserAsync(bool admin)
    {
        var id = Guid.CreateVersion7();
        var principal = id.ToString("N");
        await using var db = factory.CreateDbContext();
        db.Users.Add(new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = principal,
            DisplayName = "Other",
            IsAdmin = admin
        });
        await db.SaveChangesAsync();
        return new(ProtocolKind.Jellyfin, "backend", principal,
            new allstarr.Core.Identity.AllstarrPrincipal(id, "jellyfin", "backend", principal, "Other", admin),
            "request-test", DateTimeOffset.UtcNow.AddMinutes(5), default);
    }
}
