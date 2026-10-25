using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Middleware;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class ProblemReportsTests
{
    [Fact]
    public async Task GeneralReportIsLocalRedactedAndIdempotentAcrossChangingEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = new CreateProblemReportRequest(Guid.NewGuid(), "other",
            "  Can't play. password=private-value https://media.example.test/private-path?sig=private-value  ");
        var first = await fixture.Service.CreateAsync(fixture.Listener, request, default);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        var second = await fixture.Service.CreateAsync(fixture.Listener, request, default);
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        var detail = await fixture.Service.DetailAsync(fixture.Admin, first.Id, default);
        Assert.DoesNotContain("private-value", detail.Note);
        Assert.DoesNotContain("private-path", detail.Note);
        Assert.Null(detail.Context.Song);
        Assert.Null(detail.Context.Playback);
        Assert.Empty(detail.Context.Checks);
        var conflict = await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.CreateAsync(
            fixture.Listener, request with { Note = "Different report" }, default));
        Assert.Equal("report_request_conflict", conflict.Code);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.ProblemReports.ToArrayAsync());
        Assert.Empty(await db.Jobs.ToArrayAsync());
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        fixture.Access.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConcurrentIdenticalSubmissionsCreateOneReceiptAndDoNotMergeActors()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = new CreateProblemReportRequest(Guid.NewGuid(), "other", "Concurrent fixture");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            fixture.Service.CreateAsync(fixture.Listener, request, default)));
        Assert.Single(results.Select(item => item.Id).Distinct());
        Assert.Single(results, item => !item.Replayed);
        var other = await fixture.Service.CreateAsync(fixture.Admin, request, default);
        Assert.NotEqual(results[0].Id, other.Id);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("note-empty")]
    [InlineData("note-large")]
    [InlineData("request-empty")]
    [InlineData("selector-empty")]
    [InlineData("selector-both")]
    [InlineData("selector-url")]
    [InlineData("device-large")]
    public async Task InvalidRequestHasNoPersistentEffects(string invalid)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = new CreateProblemReportRequest(Guid.NewGuid(), "other", "Fixture");
        request = invalid switch
        {
            "kind" => request with { Kind = "invented" },
            "note-empty" => request with { Note = "  " },
            "note-large" => request with { Note = new string('x', 2001) },
            "request-empty" => request with { RequestId = Guid.Empty },
            "selector-empty" => request with { Selector = new() },
            "selector-both" => request with { Selector = new("item", Guid.NewGuid()) },
            "selector-url" => request with { Selector = new("https://media.example.test/secret") },
            _ => request with { Selector = new("item", Playback: new(fixture.Listener.AllstarrUserId!.Value, new string('x', 201))) }
        };
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemReportException>(() =>
            fixture.Service.CreateAsync(fixture.Listener, request, default))).StatusCode);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.ProblemReports.ToArrayAsync());
    }

    [Theory]
    [InlineData("{\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"kind\":\"other\",\"note\":\"fixture\",\"clientName\":\"Forged\"}")]
    [InlineData("{\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"kind\":\"other\",\"note\":\"fixture\",\"selector\":{\"itemId\":\"item\",\"title\":\"Forged\"}}")]
    [InlineData("{\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"kind\":\"other\",\"note\":\"fixture\",\"selector\":{\"itemId\":\"item\",\"playback\":{\"subjectUserId\":\"00000000-0000-0000-0000-000000000002\",\"deviceId\":\"device\",\"reason\":\"Forged\"}}}")]
    public void BrowserCannotSupplyUntrustedContext(string json) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize<CreateProblemReportRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Theory]
    [InlineData("POST", "/api/admin/problem-reports", true)]
    [InlineData("POST", "/api/admin/problem-reports/context", true)]
    [InlineData("GET", "/api/admin/ui/listener-context", true)]
    [InlineData("GET", "/api/admin/problem-reports", false)]
    [InlineData("GET", "/api/admin/problem-reports/00000000-0000-0000-0000-000000000001", false)]
    [InlineData("PATCH", "/api/admin/problem-reports/00000000-0000-0000-0000-000000000001/status", false)]
    [InlineData("POST", "/api/admin/problem-reports/context/extra", false)]
    public void ListenerMiddlewareAllowsOnlyCaptureAndBootstrapRoutes(string method, string path, bool allowed)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;
        var policy = typeof(AdminAuthenticationMiddleware).GetMethod("IsAllowedForNonAdministrator", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(allowed, (bool)policy.Invoke(null, [request])!);
    }

    [Fact]
    public async Task SnapshotScopeAndNativeLibraryPermissionsAreEnforced()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync(fixture.Listener.AllstarrUserId!.Value);
        var context = await fixture.Service.ContextAsync(fixture.Listener, new(ExternalSnapshotId: snapshot), default);
        Assert.Equal("Catalog song", context.Song!.Title);
        Assert.Equal("spotify", context.Song.CatalogProviderId);
        Assert.Null(context.Playback);
        Assert.Null(context.SubjectUserId);
        var foreign = await fixture.SnapshotAsync(fixture.Admin.AllstarrUserId!.Value);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ContextAsync(
            fixture.Listener, new(ExternalSnapshotId: foreign), default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ContextAsync(
            fixture.Listener, new("native-item"), default))).StatusCode);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await db.ExternalMetadataSnapshots.CountAsync());
        Assert.Empty(await db.Jobs.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainSnapshotChecksStayWithinExactPublicOrCurrentAccountScope(bool managed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        Guid? account = managed ? Guid.NewGuid() : null;
        if (account.HasValue)
        {
            await using var db = fixture.Factory.CreateDbContext();
            db.ProviderAccounts.Add(new() { Id = account.Value, ProviderId = "spotify", OwnerUserId = owner, Enabled = true, DisplayName = "Fixture", Revision = 2 });
            await db.SaveChangesAsync();
        }
        var snapshot = await fixture.SnapshotAsync(owner, account);
        var key = account.HasValue ? ProviderRuntimeStatusKey.CreateManaged("spotify", "metadata", account.Value) :
            ProviderRuntimeStatusKey.CreateAccountFree("spotify", "metadata");
        fixture.Health.Record(key, account.HasValue ? 2 : null, false, 1, "timeout");
        fixture.Health.Record(ProviderRuntimeStatusKey.CreateManaged("spotify", "lyrics", Guid.NewGuid()), 2, false, 1, "forbidden");
        var context = await fixture.Service.ContextAsync(fixture.Listener, new(ExternalSnapshotId: snapshot), default);
        Assert.Null(context.Playback);
        Assert.Null(context.SubjectUserId);
        Assert.Equal("timeout", Assert.Single(context.Checks).ReasonCode);
        if (account.HasValue)
        {
            await using var db = fixture.Factory.CreateDbContext();
            await db.ProviderAccounts.Where(item => item.Id == account.Value).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Revision, 3));
            Assert.Empty((await fixture.Service.ContextAsync(fixture.Listener, new(ExternalSnapshotId: snapshot), default)).Checks);
        }
    }

    [Fact]
    public async Task PlaybackContextKeepsActorSubjectCatalogAndServingSourceSeparate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        fixture.Activity.States = [new("private-device", "ext-spotify-song-track", 123,
            fixture.Clock.UtcNow.UtcDateTime, owner, "private-backend-principal", "Listener", "Feishin", "Private device")];
        await fixture.SnapshotAsync(owner);
        fixture.Delivery.Setup(item => item.StreamFor(owner, "private-device", "ext-spotify-song-track"))
            .Returns(new PlaybackStreamSource("qobuz", "private-provider-reference", null, "jellyfin", "fixture", false,
                fixture.Clock.UtcNow.AddSeconds(-1), "provider-priority:account-free:remote")
            { SeekRung = "byte-range" });
        fixture.Health.Record(ProviderRuntimeStatusKey.CreateAccountFree("qobuz", "streaming"), null, false, 50, "timeout");
        var selector = new ProblemReportSelector("ext-spotify-song-track", Playback: new(owner, "private-device"));
        var receipt = await fixture.Service.CreateAsync(fixture.Admin,
            new(Guid.NewGuid(), "seek", "Fixture", selector), default);
        var report = await fixture.Service.DetailAsync(fixture.Admin, receipt.Id, default);
        Assert.Equal("Administrator", report.ReporterName);
        Assert.Equal("Listener", report.SubjectName);
        Assert.Equal(owner, report.Context.SubjectUserId);
        Assert.Equal("spotify", report.Context.Song!.CatalogProviderId);
        Assert.Equal("qobuz", report.Context.Playback!.ProviderId);
        Assert.True(report.Context.Playback.SourceConfirmed);
        Assert.Equal("Feishin", report.Context.Playback.ClientName);
        Assert.Equal("timeout", Assert.Single(report.Context.Checks).ReasonCode);
        var stored = JsonSerializer.Serialize(report);
        foreach (var secret in new[] { "private-device", "private-backend-principal", "private-provider-reference", "Private device" })
            Assert.DoesNotContain(secret, stored);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ContextAsync(
            fixture.Listener, selector with { Playback = new(fixture.Admin.AllstarrUserId!.Value, "private-device") }, default))).StatusCode);
        fixture.Delivery.Verify(item => item.StreamFor(owner, "private-device", "ext-spotify-song-track"), Times.Once);
    }

    [Fact]
    public async Task MissingServingEvidenceNeverConfirmsCatalogOrInventsClient()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        await fixture.SnapshotAsync(owner);
        fixture.Activity.States = [new("device", "ext-spotify-song-track", 0, fixture.Clock.UtcNow.UtcDateTime, owner)];
        var context = await fixture.Service.ContextAsync(fixture.Listener,
            new("ext-spotify-song-track", Playback: new(owner, "device")), default);
        Assert.Equal("spotify", context.Song!.CatalogProviderId);
        Assert.False(context.Playback!.SourceConfirmed);
        Assert.Null(context.Playback.ProviderId);
        Assert.Null(context.Playback.ClientName);
        Assert.Empty(context.Checks);
        fixture.Activity.States = fixture.Activity.States.Select(state => state with { LastActivity = state.LastActivity.AddMinutes(-6) }).ToArray();
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ContextAsync(fixture.Listener,
            new("ext-spotify-song-track", Playback: new(owner, "device")), default))).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServingAccountAndCheckEvidenceMustHaveCurrentExactRevision(bool matchingRevision)
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        var account = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.ProviderAccounts.Add(new() { Id = account, ProviderId = "qobuz", OwnerUserId = owner, DisplayName = "Private account", Enabled = true, Revision = 2 });
            await db.SaveChangesAsync();
        }
        await fixture.SnapshotAsync(owner);
        fixture.Activity.States = [new("device", "ext-spotify-song-track", 0, fixture.Clock.UtcNow.UtcDateTime, owner)];
        fixture.Delivery.Setup(item => item.StreamFor(owner, "device", "ext-spotify-song-track"))
            .Returns(new PlaybackStreamSource("qobuz", "track", account, "jellyfin", "fixture", false, fixture.Clock.UtcNow.AddSeconds(-1), "provider-priority")
            { AccountRevision = matchingRevision ? 2 : 1 });
        fixture.Health.Record(ProviderRuntimeStatusKey.CreateManaged("qobuz", "streaming", account), 1, false, 1, "timeout");
        fixture.Health.Record(ProviderRuntimeStatusKey.CreateManaged("qobuz", "lyrics", account), 2, false, 1, "password=private-value");
        var context = await fixture.Service.ContextAsync(fixture.Listener,
            new("ext-spotify-song-track", Playback: new(owner, "device")), default);
        Assert.Equal(matchingRevision, context.Playback!.SourceConfirmed);
        Assert.Equal(matchingRevision ? account : null, context.Playback.ProviderAccountId);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(context));
        if (matchingRevision)
        {
            var check = Assert.Single(context.Checks);
            Assert.Equal("lyrics", check.Capability);
            Assert.Null(check.ReasonCode);
        }
        else Assert.Empty(context.Checks);
    }

    [Fact]
    public async Task NativeContextUsesOnlyAccessibleIndexAndObservedClientAndLeavesNativeMetadataUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        var track = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.LibraryTracks.Add(new()
            {
                Id = track,
                OwnerUserId = owner,
                BackendItemId = "native-item",
                BackendLibraryId = "allowed",
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                Title = "Native song",
                Artist = "Artist",
                FilePath = "/private/original/library.flac"
            });
            await db.SaveChangesAsync();
        }
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new AllstarrPrincipal(owner, "jellyfin", "fixture", "listener", "Listener", false), "fixture", fixture.Clock.UtcNow.AddMinutes(1), default);
        fixture.Access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(execution, new(true, ["allowed"])));
        fixture.Activity.States = [new("device", "native-item", 0, fixture.Clock.UtcNow.UtcDateTime, owner)];
        var context = await fixture.Service.ContextAsync(fixture.Listener, new("native-item", Playback: new(owner, "device")), default);
        Assert.Equal("Native song", context.Song!.Title);
        Assert.Equal(track, context.Song.LibraryTrackId);
        Assert.Equal("jellyfin", context.Playback!.ProviderId);
        Assert.True(context.Playback.SourceConfirmed);
        Assert.Null(context.Playback.ClientName);
        Assert.DoesNotContain("/private/original", JsonSerializer.Serialize(context));
        await using var verify = fixture.Factory.CreateDbContext();
        Assert.Equal("/private/original/library.flac", (await verify.LibraryTracks.SingleAsync()).FilePath);
        var wrongViewer = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "administrator",
            new AllstarrPrincipal(fixture.Admin.AllstarrUserId!.Value, "jellyfin", "fixture", "administrator", "Administrator", true),
            "fixture", fixture.Clock.UtcNow.AddMinutes(1), default);
        fixture.Access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(wrongViewer, new(true, ["allowed"])));
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ContextAsync(fixture.Listener, new("native-item"), default))).StatusCode);
        fixture.Access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(execution, new(true, ["other"])));
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ContextAsync(fixture.Listener, new("native-item"), default))).StatusCode);
    }

    [Fact]
    public async Task PersistedDecisionAndEffectiveManualAuthorityAreCapturedWithoutCandidatePayloads()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        var snapshot = await fixture.SnapshotAsync(owner);
        var decision = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync();
            db.TrackMatches.Add(new()
            {
                Id = decision,
                OwnerUserId = owner,
                ExternalSnapshotId = snapshot,
                State = TrackMatchState.Unresolved,
                Confidence = .4,
                Threshold = .9,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                PolicyVersion = "fixture",
                DecidedAt = fixture.Clock.UtcNow,
                ReasonsJson = "[\"below_suggestion_threshold\",\"raw-private-reason\"]",
                CandidateResultsJson = "[]"
            });
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ExternalSnapshotId = snapshot,
                SourceProviderId = "spotify",
                SourceExternalIdHash = source.ExternalIdHash,
                Decision = ManualOverrideDecision.Reject,
                Reason = "Private manual note",
                DecisionVersion = 1,
                CreatedAt = fixture.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var context = await fixture.Service.ContextAsync(fixture.Listener, new(ExternalSnapshotId: snapshot), default);
        Assert.Equal(decision, context.Match!.DecisionId);
        Assert.Equal("personal", context.Match.AuthorityScope);
        Assert.Equal("rejected", context.Match.State);
        Assert.Equal("below_suggestion_threshold", Assert.Single(context.Match.ReasonCodes));
        var json = JsonSerializer.Serialize(context);
        Assert.DoesNotContain("raw-private-reason", json);
        Assert.DoesNotContain("Private manual note", json);
    }

    [Theory]
    [InlineData("personal-pin", "pinned", "personal")]
    [InlineData("personal-reject", "rejected", "personal")]
    [InlineData("household-pin", "pinned", "household")]
    public async Task SavedProviderAuthorityIsReportedWithoutAPlayabilityOrRoutingClaim(string choice, string expectedState, string expectedScope)
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        var snapshot = await fixture.SnapshotAsync(owner);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync();
            var personal = choice.StartsWith("personal", StringComparison.Ordinal);
            var householdDecision = choice == "personal-pin" ? ManualOverrideDecision.Reject : ManualOverrideDecision.Pin;
            ManualTrackOverrideRecord Authority(Guid? user, ManualOverrideDecision decision) => new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user,
                ExternalSnapshotId = snapshot,
                SourceProviderId = "spotify",
                SourceExternalIdHash = source.ExternalIdHash,
                Decision = decision,
                TargetProviderId = decision == ManualOverrideDecision.Pin ? "qobuz" : null,
                TargetExternalId = decision == ManualOverrideDecision.Pin ? "saved-target" : null,
                Reason = "Private authority reason",
                DecisionVersion = 1,
                CreatedAt = fixture.Clock.UtcNow
            };
            db.ManualTrackOverrides.Add(Authority(null, householdDecision));
            if (personal) db.ManualTrackOverrides.Add(Authority(owner,
                choice == "personal-pin" ? ManualOverrideDecision.Pin : ManualOverrideDecision.Reject));
            await db.SaveChangesAsync();
        }
        var context = await fixture.Service.ContextAsync(fixture.Listener, new(ExternalSnapshotId: snapshot), default);
        Assert.Equal(expectedState, context.Match!.State);
        Assert.Equal(expectedScope, context.Match.AuthorityScope);
        Assert.Null(context.Match.DecisionId);
        Assert.Null(context.Playback);
        Assert.DoesNotContain("Private authority reason", JsonSerializer.Serialize(context));
        fixture.Access.VerifyNoOtherCalls();
        fixture.Delivery.VerifyNoOtherCalls();
        await using var verify = fixture.Factory.CreateDbContext();
        Assert.Empty(await verify.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task ExternalProviderPinNeverBorrowsAnOldAutomaticNativeCandidatesIdentityOrDisplayMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = fixture.Listener.AllstarrUserId!.Value;
        var snapshot = await fixture.SnapshotAsync(owner);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync();
            var local = Guid.NewGuid();
            db.LibraryTracks.Add(new()
            {
                Id = local,
                OwnerUserId = owner,
                BackendItemId = "old-native",
                BackendLibraryId = "allowed",
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                Title = "Wrong native title",
                Artist = "Wrong native artist",
                Album = "Wrong album",
                DurationMilliseconds = 123000
            });
            db.TrackMatches.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ExternalSnapshotId = snapshot,
                LibraryTrackId = local,
                State = TrackMatchState.Accepted,
                Confidence = 1,
                Threshold = .9,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                PolicyVersion = "fixture",
                DecidedAt = fixture.Clock.UtcNow
            });
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ExternalSnapshotId = snapshot,
                SourceProviderId = "spotify",
                SourceExternalIdHash = source.ExternalIdHash,
                Decision = ManualOverrideDecision.Pin,
                TargetProviderId = "qobuz",
                TargetExternalId = "chosen-track",
                Reason = "Fixture",
                DecisionVersion = 1,
                CreatedAt = fixture.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var context = await fixture.Service.ContextAsync(fixture.Listener, new(ExternalSnapshotId: snapshot), default);
        Assert.Equal("Catalog song", context.Song!.Title);
        Assert.Equal("Artist", context.Song.Artist);
        Assert.Null(context.Song.LibraryTrackId);
        Assert.Null(context.Song.Album);
        Assert.Null(context.Song.DurationMilliseconds);
        Assert.Equal("pinned", context.Match!.State);
        Assert.Null(context.Playback);
        fixture.Access.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("token=private-value")]
    [InlineData("{\"access_token\":\"private-value\"}")]
    [InlineData("Cookie: a=private-value; b=also-private")]
    [InlineData("Authorization: Bearer private-value")]
    [InlineData("//media.example.test/private-value?sig=also-private")]
    public async Task NotesNeverPersistCredentialAssignmentsOrUrls(string note)
    {
        await using var fixture = await Fixture.CreateAsync();
        var receipt = await fixture.Service.CreateAsync(fixture.Listener, new(Guid.NewGuid(), "other", note), default);
        var report = await fixture.Service.DetailAsync(fixture.Admin, receipt.Id, default);
        Assert.DoesNotContain("private-value", report.Note);
        Assert.DoesNotContain("also-private", report.Note);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("role")]
    [InlineData("principal")]
    [InlineData("instance")]
    [InlineData("backend")]
    public async Task ExactActorIdentityIsRequiredForEveryOperation(string changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(item => item.Id == fixture.Admin.AllstarrUserId);
            if (changed == "disabled") user.Enabled = false;
            if (changed == "role") user.IsAdmin = false;
            if (changed == "principal") user.BackendPrincipalId = "changed";
            if (changed == "instance") user.BackendInstanceId = "changed";
            if (changed == "backend") user.BackendType = "subsonic";
            await db.SaveChangesAsync();
        }
        foreach (var operation in new Func<Task>[]
        {
            async () => await fixture.Service.ContextAsync(fixture.Admin, null, default),
            async () => await fixture.Service.CreateAsync(fixture.Admin, new(Guid.NewGuid(), "other", "Fixture"), default),
            async () => await fixture.Service.ListAsync(fixture.Admin, null, null, 50, default),
            async () => await fixture.Service.DetailAsync(fixture.Admin, Guid.NewGuid(), default),
            async () => await fixture.Service.UpdateStatusAsync(fixture.Admin, Guid.NewGuid(), new("seen", 1), default)
        }) Assert.Equal(401, (await Assert.ThrowsAsync<ProblemReportException>(operation)).StatusCode);
    }

    [Fact]
    public async Task InboxIsAdministratorOnlyAndStatusChangesUseAtomicRevisionAndSafeAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var receipt = await fixture.Service.CreateAsync(fixture.Listener, new(Guid.NewGuid(), "other", "Private note"), default);
        Assert.Equal(403, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ListAsync(fixture.Listener, null, null, 50, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.DetailAsync(fixture.Listener, receipt.Id, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.UpdateStatusAsync(fixture.Listener, receipt.Id, new("fixed", 1), default))).StatusCode);
        var changed = await fixture.Service.UpdateStatusAsync(fixture.Admin, receipt.Id, new("seen", 1), default);
        Assert.Equal(2, changed.Revision);
        Assert.Equal("seen", changed.Status);
        Assert.Equal("report_changed", (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.UpdateStatusAsync(fixture.Admin, receipt.Id, new("seen", 1), default))).Code);
        Assert.Equal(2, (await fixture.Service.UpdateStatusAsync(fixture.Admin, receipt.Id, new("seen", 2), default)).Revision);
        Assert.Equal("fixed", (await fixture.Service.UpdateStatusAsync(fixture.Admin, receipt.Id, new("fixed", 2), default)).Status);
        Assert.Equal("new", (await fixture.Service.UpdateStatusAsync(fixture.Admin, receipt.Id, new("new", 3), default)).Status);
        await using var db = fixture.Factory.CreateDbContext();
        var audit = await db.AuditEvents.ToArrayAsync();
        Assert.Equal(3, audit.Length);
        Assert.All(audit, item => Assert.DoesNotContain("Private note", item.DetailsJson));
    }

    [Fact]
    public async Task ConcurrentStatusUpdatesAllowOnlyOneExpectedRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var receipt = await fixture.Service.CreateAsync(fixture.Listener, new(Guid.NewGuid(), "other", "Fixture"), default);
        async Task<string> Save(string state)
        {
            try { return (await fixture.Service.UpdateStatusAsync(fixture.Admin, receipt.Id, new(state, 1), default)).Status; }
            catch (ProblemReportException error) { return error.Code; }
        }
        var outcomes = await Task.WhenAll(Save("seen"), Save("fixed"));
        Assert.Single(outcomes, value => value == "report_changed");
        Assert.Single(outcomes, value => value is "seen" or "fixed");
        Assert.Equal(2, (await fixture.Service.DetailAsync(fixture.Admin, receipt.Id, default)).Revision);
    }

    [Fact]
    public async Task CursorHasStableUpperBoundAndRejectsMalformedOrChangedFilter()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = new List<Guid>();
        for (var index = 0; index < 4; index++)
            ids.Add((await fixture.Service.CreateAsync(fixture.Listener, new(Guid.NewGuid(), "other", $"Fixture {index}"), default)).Id);
        var first = await fixture.Service.ListAsync(fixture.Admin, "new", null, 2, default);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        var later = await fixture.Service.CreateAsync(fixture.Listener, new(Guid.NewGuid(), "other", "Later"), default);
        var second = await fixture.Service.ListAsync(fixture.Admin, "new", first.NextCursor, 2, default);
        Assert.Equal(4, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());
        Assert.DoesNotContain(later.Id, second.Items.Select(item => item.Id));
        Assert.Null(second.NextCursor);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ListAsync(fixture.Admin, "fixed", first.NextCursor, 2, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service.ListAsync(fixture.Admin, null, "malformed", 2, default))).StatusCode);
    }

    [Fact]
    public async Task ControllerPreservesAdminPortAndSafeErrorAndReceiptStatuses()
    {
        var filter = Assert.Single(typeof(ProblemReportsController).GetCustomAttributes<ServiceFilterAttribute>());
        Assert.Equal(typeof(AdminPortFilter), filter.ServiceType);
        await using var fixture = await Fixture.CreateAsync();
        var controller = new ProblemReportsController(fixture.Service, null!)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Preview(null, default));
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = fixture.Listener;
        var request = new CreateProblemReportRequest(Guid.NewGuid(), "other", "Fixture");
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.Create(request, default)).StatusCode);
        var replay = Assert.IsType<ObjectResult>(await controller.Create(request, default));
        Assert.Equal(200, replay.StatusCode);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(replay.Value));
        Assert.True(wire.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal("new", wire.RootElement.GetProperty("status").GetString());
        var preview = Assert.IsType<OkObjectResult>(await controller.Preview(null, default));
        using var contextWire = JsonDocument.Parse(JsonSerializer.Serialize(preview.Value));
        Assert.Equal(1, contextWire.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, contextWire.RootElement.GetProperty("song").ValueKind);
        var rejected = Assert.IsType<ObjectResult>(await controller.Create(request with { Note = "Changed" }, default));
        Assert.Equal(409, rejected.StatusCode);
        Assert.DoesNotContain("Changed", JsonSerializer.Serialize(rejected.Value));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteTestDatabase database;
        public Factory Factory { get; }
        public Clock Clock { get; } = new();
        public Mock<IBackendLibraryAccessResolver> Access { get; } = new(MockBehavior.Strict);
        public Mock<IPlaybackDeliveryActivitySource> Delivery { get; } = new();
        public Activity Activity { get; } = new();
        public ProviderRuntimeHealth Health { get; }
        public AdminAuthSession Listener { get; } = Session(false);
        public AdminAuthSession Admin { get; } = Session(true);
        public ProblemReportService Service { get; }

        private Fixture(SqliteTestDatabase database)
        {
            this.database = database;
            Factory = new(database.Options);
            Health = new(clock: Clock);
            Service = new(Factory, Clock, Access.Object, [Activity], Delivery.Object, Health);
            Access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BackendLibraryAccessContext.Unavailable);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await SqliteTestDatabase.CreateAsync());
            await using var db = fixture.Factory.CreateDbContext();
            foreach (var session in new[] { fixture.Listener, fixture.Admin }) db.Users.Add(new()
            {
                Id = session.AllstarrUserId!.Value,
                DisplayName = session.UserName,
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = session.UserId,
                IsAdmin = session.IsAdministrator
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task<Guid> SnapshotAsync(Guid owner, Guid? account = null)
        {
            await using var db = Factory.CreateDbContext();
            var user = await db.Users.SingleAsync(item => item.Id == owner);
            var id = Guid.NewGuid();
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = id,
                OwnerUserId = owner,
                ProviderAccountId = account,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = user.BackendPrincipalId,
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("track"))),
                SnapshotVersion = 1,
                ProviderRevision = "fixture",
                PayloadSha256 = new string('a', 64),
                PayloadJson = "{\"title\":\"Catalog song\",\"artist\":\"Artist\"}",
                RetrievedAt = Clock.UtcNow
            });
            await db.SaveChangesAsync();
            return id;
        }

        private static AdminAuthSession Session(bool admin) => new()
        {
            SessionId = "fixture",
            UserId = admin ? "administrator" : "listener",
            UserName = admin ? "Administrator" : "Listener",
            IsAdministrator = admin,
            AllstarrUserId = Guid.NewGuid(),
            BackendType = "Jellyfin",
            BackendInstanceId = "fixture",
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; }
    private sealed class Activity : IPlaybackActivitySource
    {
        public PlaybackActivityState[] States { get; set; } = [];
        public IReadOnlyList<PlaybackActivityState> GetActivePlaybackStates(TimeSpan maxAge) => States;
    }
}
