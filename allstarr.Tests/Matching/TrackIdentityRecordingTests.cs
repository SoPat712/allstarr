using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task RememberSource_MergesPriorPublicIdWithVerifiedIsrcAndKeepsEarliestId()
    {
        var actor = Actor(_userA);
        var earlier = new TrackIdentityService(_factory, _storageState, new FakeClock(_clock.UtcNow.AddMinutes(-1)));
        var first = await earlier.CreateRecordingAsync(actor, "first", null, null, publicId: "ext-spotify-song-track");
        var other = await _service.CreateRecordingAsync(actor, "isrc", "USRC17607839", null, publicId: "ext-deezer-song-other");

        Assert.Equal(first.Recording.Id, await _service.RememberSourceAsync(Context(actor, "spotify"),
            Track("spotify", "track"), first.Recording.PublicId, ProviderIdentityScope.Catalog, "USRC17607839"));
        Assert.Equal(first.Recording.Id, (await _service.ResolveRecordingAsync(actor, other.Recording.PublicId))!.Id);
        Assert.Equal("USRC17607839", (await _service.ResolveRecordingAsync(actor, first.Recording.PublicId))!.Isrc);
    }

    [Fact]
    public async Task RememberSource_ReportsAChangedIsrcAsAMergeConflict()
    {
        var actor = Actor(_userA);
        await _service.CreateRecordingAsync(actor, "first", "USRC17607839", null, publicId: "ext-spotify-song-track");

        await Assert.ThrowsAsync<RecordingMergeConflictException>(() => _service.RememberSourceAsync(
            Context(actor, "spotify"), Track("spotify", "track"), "ext-spotify-song-track",
            ProviderIdentityScope.Catalog, "GBAYE6800011"));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ProviderTrackIdentities.ToListAsync());
    }

    [Fact]
    public async Task RememberSource_RetainsFirstLiteralIdAcrossRestartAndLaterProvider()
    {
        var actor = Actor(_userA);
        var first = await _service.RememberSourceAsync(Context(actor, "deezer"), Track("deezer", "first"),
            "ext-deezer-first", ProviderIdentityScope.Catalog, "USRC17607839");
        var restarted = new TrackIdentityService(_factory, _storageState, _clock);
        Assert.Equal(first, await restarted.RememberSourceAsync(Context(actor, "deezer"), Track("deezer", "first"),
            "ext-deezer-song-first", ProviderIdentityScope.Catalog));
        Assert.Equal(first, await restarted.RememberSourceAsync(Context(actor, "qobuz"), Track("qobuz", "second"),
            "ext-qobuz-song-second", ProviderIdentityScope.Catalog, "USRC17607839"));
        Assert.Equal("ext-deezer-first", (await restarted.ResolveRecordingAsync(actor, "ext-qobuz-song-second"))!.PublicId);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.CanonicalRecordings.ToListAsync());
        Assert.Equal(2, await db.ProviderTrackIdentities.CountAsync());
        Assert.Single(await db.RecordingIdentifiers.ToListAsync());
    }

    [Theory]
    [InlineData(ProviderIdentityVerification.Pinned, "manual")]
    [InlineData(ProviderIdentityVerification.Verified, "automatic-suggestion")]
    public async Task RememberSource_DoesNotReplaceExistingAssociation(ProviderIdentityVerification verification, string method)
    {
        var execution = Context(Actor(_userA), "deezer");
        var recording = await _service.CreateRecordingAsync(execution.Actor, "existing");
        await _service.LinkAsync(execution, new(recording.Recording.Id, Track("deezer", "track"),
            ProviderIdentityScope.Catalog, verification, method, 1));
        Assert.Equal(recording.Recording.Id, await _service.RememberSourceAsync(execution, Track("deezer", "track"),
            "ext-deezer-song-track", ProviderIdentityScope.Catalog));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.CanonicalRecordings.ToListAsync());
        var link = await db.ProviderTrackIdentities.SingleAsync();
        Assert.Equal(verification, link.Verification);
        Assert.Equal(method, link.VerificationMethod);
    }

    [Fact]
    public async Task RememberSource_RejectsAnotherUsersAccountWithoutCreatingAnOrphan()
    {
        var account = await SeedUserAccount("qobuz", _userA);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RememberSourceAsync(
            Context(Actor(_userB), "qobuz", account), Track("qobuz", "private"), "ext-qobuz-song-private",
            ProviderIdentityScope.Account));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.CanonicalRecordings.ToListAsync());
        Assert.Empty(await db.ProviderTrackIdentities.ToListAsync());
    }

    [Fact]
    public async Task RecordingLookup_ResolvesRegisteredProviderAliases()
    {
        var actor = Actor(_userA);
        var recording = await _service.CreateRecordingAsync(actor, "original");
        await Link(recording.Recording.Id, Context(actor, "current-provider"), "track");
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.ResolveProviderId("legacy-provider")).Returns("current-provider");
        var service = new TrackIdentityService(_factory, _storageState, _clock, registry.Object);
        Assert.Equal(recording.Recording.Id,
            (await service.ResolveRecordingAsync(actor, "ext-legacy-provider-song-track"))?.Id);
    }

    [Fact]
    public async Task PublicIdReads_AreReadOnlyAndPersonalTargetsStayScoped()
    {
        var actor = Actor(_userA);
        var account = await SeedUserAccount("qobuz", _userA);
        var record = await _service.CreateRecordingAsync(actor, "first", null, null, "ext-deezer-first");
        await Link(record.Recording.Id, Context(actor, "deezer"), "first");
        await Link(record.Recording.Id, Context(actor, "qobuz", account), "personal", ProviderIdentityScope.Account);
        await using var before = await _factory.CreateDbContextAsync();
        var counts = (await before.CanonicalRecordings.CountAsync(), await before.ProviderTrackIdentities.CountAsync(),
            await before.AuditEvents.CountAsync(), await before.Jobs.CountAsync());
        var resolved = await _service.ResolveManyAsync([
            new(Context(actor, "deezer"), Track("deezer", "first")),
            new(Context(actor, "qobuz", account), Track("qobuz", "personal"))]);
        Assert.All(resolved, item => Assert.Equal("ext-deezer-first", item?.PublicId));
        var allowed = await _service.ResolveTargetAsync(Context(actor, "qobuz", account), record.Recording.Id, new("qobuz"));
        Assert.Equal("personal", allowed?.ExternalId.Value);
        Assert.Equal(record.Recording.PublicId, allowed?.PublicId);
        Assert.Null(await _service.ResolveTargetAsync(Context(Actor(_userB), "qobuz"), record.Recording.Id, new("qobuz")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ResolveTargetAsync(
            Context(Actor(_userB), "qobuz", account), record.Recording.Id, new("qobuz")));
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(counts, (await after.CanonicalRecordings.CountAsync(), await after.ProviderTrackIdentities.CountAsync(),
            await after.AuditEvents.CountAsync(), await after.Jobs.CountAsync()));
    }

    [Fact]
    public async Task Merge_KeepsEarliestPublicIdAndAllAliasesAcrossRestart()
    {
        var actor = Actor(_userA);
        var younger = await _service.CreateRecordingAsync(actor, "younger", "USRC17607839", null, "ext-qobuz-song-second");
        var earlierService = new TrackIdentityService(_factory, _storageState, new FakeClock(_clock.UtcNow.AddMinutes(-1)));
        var earlier = await earlierService.CreateRecordingAsync(actor, "earlier", "GBAYE0601498", null, "ext-deezer-first");
        await Link(earlier.Recording.Id, Context(actor, "deezer"), "first");
        await Link(younger.Recording.Id, Context(actor, "qobuz"), "second");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ListeningEvents.Add(new ListeningEventRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                OccurrenceKey = "before-merge",
                CanonicalRecordingId = younger.Recording.Id,
                TrackReference = younger.Recording.PublicId,
                UpdatedAt = _clock.UtcNow
            });
            db.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                Protocol = "jellyfin",
                BackendInstanceId = "fixture",
                BackendLibraryId = "music",
                BackendItemId = "native",
                CanonicalRecordingId = younger.Recording.Id,
                Title = "Recording",
                Artist = "Artist",
                IndexedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                SourceModifiedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var merged = await _service.MergeRecordingsAsync(actor, younger.Recording.Id, earlier.Recording.Id, "merge");
        var restarted = new TrackIdentityService(_factory, _storageState, _clock);
        foreach (var alias in new[] { earlier.Recording.PublicId, younger.Recording.PublicId,
                     younger.Recording.Id.ToString("D"), CanonicalCatalogKeys.DefaultPublicId(younger.Recording.Id),
                     "ext-deezer-song-first", "ext-qobuz-second" })
        {
            var resolved = await restarted.ResolveRecordingAsync(actor, alias);
            Assert.Equal(earlier.Recording.Id, resolved?.Id);
            Assert.Equal("ext-deezer-first", resolved?.PublicId);
        }
        Assert.Equal(earlier.Recording.Id, merged.Id);
        await Link(younger.Recording.Id, Context(actor, "spotify"), "later-provider");
        Assert.Equal(merged.Id, (await restarted.ResolveAsync(Context(actor, "spotify"), Track("spotify", "later-provider")))?.CanonicalRecordingId);
        var existing = await restarted.CreateRecordingAsync(actor, "second-isrc", "USRC17607839");
        Assert.False(existing.Created);
        Assert.Equal(merged.PublicId, existing.Recording.PublicId);
        var repeated = await restarted.MergeRecordingsAsync(actor, younger.Recording.Id, earlier.Recording.Id, "repeat");
        Assert.Equal(merged.Id, repeated.Id);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.CanonicalRecordings.CountAsync());
        Assert.Equal(merged.Id, (await verify.CanonicalRecordings.SingleAsync(item => item.Id == younger.Recording.Id)).MergedIntoId);
        Assert.All(await verify.ProviderTrackIdentities.ToArrayAsync(), item => Assert.Equal(merged.Id, item.CanonicalRecordingId));
        Assert.All(await verify.RecordingIdentifiers.ToArrayAsync(), item => Assert.Equal(merged.Id, item.RecordingId));
        Assert.Equal(merged.Id, (await verify.LibraryTracks.SingleAsync()).CanonicalRecordingId);
        var history = await verify.ListeningEvents.SingleAsync();
        Assert.Equal(merged.Id, history.CanonicalRecordingId);
        Assert.Equal(younger.Recording.PublicId, history.TrackReference);
        Assert.Single(await verify.AuditEvents.Where(item => item.Action == "canonical-recording.merge").ToArrayAsync());
    }

    [Fact]
    public async Task Merge_PreservesPersonalRouteAuthorization()
    {
        var actorA = Actor(_userA);
        var actorB = Actor(_userB);
        var accountA = await SeedUserAccount("qobuz", _userA);
        var first = await _service.CreateRecordingAsync(actorA, "public", null, null, "ext-deezer-song-shared");
        var second = await _service.CreateRecordingAsync(actorA, "personal", null, null, "ext-qobuz-song-personal");
        await Link(first.Recording.Id, Context(actorA, "deezer"), "shared");
        await Link(second.Recording.Id, Context(actorA, "qobuz", accountA), "personal", ProviderIdentityScope.Account);
        await _service.MergeRecordingsAsync(actorA, first.Recording.Id, second.Recording.Id, "merge");

        var authorized = await _service.TranslateAsync(Context(actorA, "deezer"), Track("deezer", "shared"),
            Context(actorA, "qobuz", accountA), new ProviderTrackIdentityTarget("qobuz"));
        var unauthorized = await _service.TranslateAsync(Context(actorB, "deezer"), Track("deezer", "shared"),
            Context(actorB, "qobuz"), new ProviderTrackIdentityTarget("qobuz"));
        Assert.Equal(TrackIdentityTranslationStatus.Translated, authorized.Status);
        Assert.Equal(TrackIdentityTranslationStatus.TargetNotLinked, unauthorized.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.TranslateAsync(
            Context(actorB, "deezer"), Track("deezer", "shared"), Context(actorB, "qobuz", accountA), new ProviderTrackIdentityTarget("qobuz")));
        await using var db = await _factory.CreateDbContextAsync();
        var personal = await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "qobuz");
        Assert.Equal(ProviderIdentityScope.Account, personal.Scope);
        Assert.Equal(accountA.Id, personal.ProviderAccountId);
    }

    [Fact]
    public async Task Merge_RejectsConflictingMusicBrainzIdentitiesWithoutChanges()
    {
        var actor = Actor(_userA);
        var first = await _service.CreateRecordingAsync(actor, "first", musicBrainzRecordingId: "11111111-1111-4111-8111-111111111111");
        var second = await _service.CreateRecordingAsync(actor, "second", musicBrainzRecordingId: "22222222-2222-4222-8222-222222222222");
        await Assert.ThrowsAsync<RecordingMergeConflictException>(() =>
            _service.MergeRecordingsAsync(actor, first.Recording.Id, second.Recording.Id, "conflict"));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.All(await db.CanonicalRecordings.ToArrayAsync(), item => Assert.Null(item.MergedIntoId));
        Assert.Equal(2, await db.RecordingIdentifiers.CountAsync());
        Assert.Empty(await db.AuditEvents.Where(item => item.Action == "canonical-recording.merge").ToArrayAsync());
    }

    [Fact]
    public async Task FirstSeenPublicId_IsStableWhenLaterAliasUsesTheSameIsrc()
    {
        var actor = Actor(_userA);
        var first = await _service.CreateRecordingAsync(actor, "first", "USRC17607839", null, "ext-deezer-song-first");
        var restarted = new TrackIdentityService(_factory, _storageState, _clock);
        var later = await restarted.CreateRecordingAsync(actor, "later", "USRC17607839", null, "ext-qobuz-song-later");
        Assert.True(first.Created);
        Assert.False(later.Created);
        Assert.Equal(first.Recording.PublicId, later.Recording.PublicId);
        Assert.Equal(first.Recording.Id, later.Recording.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.CreateRecordingAsync(actor, "bad", null, null, "ext-deezer-album-first"));
    }
}
