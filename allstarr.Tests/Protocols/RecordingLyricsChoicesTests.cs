using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class RecordingLyricsChoicesTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private TrackIdentityService _identities = null!;
    private RecordingLyricsChoices _choices = null!;
    private FakeClock _clock = null!;
    private Guid _adminId;
    private Guid _userAId;
    private Guid _userBId;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        _adminId = Guid.CreateVersion7();
        _userAId = Guid.CreateVersion7();
        _userBId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);
        _clock = new FakeClock(now);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(
                User(_adminId, "admin", isAdmin: true, now),
                User(_userAId, "user-a", isAdmin: false, now),
                User(_userBId, "user-b", isAdmin: false, now));
            await db.SaveChangesAsync();
        }

        var options = new StorageOptions
        {
            DataDirectory = _database.StorageOptions.DataDirectory,
            DatabaseFileName = _database.StorageOptions.DatabaseFileName
        };
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "lyrics-choice-fixture");
        _identities = new TrackIdentityService(_factory, state, _clock);
        _choices = new RecordingLyricsChoices(_factory, _identities, _clock);
    }

    [Fact]
    public async Task RecordingMergePreservesPersonalAndHouseholdChoicesAcrossSavedIds()
    {
        var first = await CreateRecordingAsync(_adminId);
        var second = await CreateRecordingAsync(_adminId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.CanonicalRecordings.SingleAsync(item => item.Id == second)).CreatedAt = _clock.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync();
        }
        await _choices.SetAsync(Context(_adminId, isAdministrator: true), first, "lrclib", "household", new());
        var personal = await _choices.SetAsync(Context(_userAId), second, "spotify", "personal", new());
        var merged = await _identities.MergeRecordingsAsync(Context(_adminId, isAdministrator: true).RequireActor(), first, second, "merge");
        Assert.Equal(first, merged.Id);
        var saved = await _choices.GetAsync(Context(_userAId), second);
        Assert.Equal("spotify", saved.Effective?.SourceId);
        Assert.Equal("lrclib", saved.Household?.SourceId);
        Assert.Equal(personal.Personal!.Id, saved.Personal?.Id);
        Assert.Equal(2, saved.Personal?.Revision);
        Assert.Equal(first, saved.Personal?.CanonicalRecordingId);
        Assert.Equal("lrclib", (await _choices.GetAsync(Context(_userBId), second)).Effective?.SourceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordingMergeCannotSilentlyReplaceAnExistingChoice(bool sameSource)
    {
        var first = await CreateRecordingAsync(_adminId);
        var second = await CreateRecordingAsync(_adminId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.CanonicalRecordings.SingleAsync(item => item.Id == second)).CreatedAt = _clock.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync();
        }
        var firstChoice = await _choices.SetAsync(Context(_adminId, isAdministrator: true), first, "lrclib", "household", new());
        await _choices.SetAsync(Context(_adminId, isAdministrator: true), second, sameSource ? "lrclib" : "spotify", "household", new());
        if (sameSource)
        {
            await _identities.MergeRecordingsAsync(Context(_adminId, isAdministrator: true).RequireActor(), first, second, "merge");
            Assert.Equal(firstChoice.Household!.Id, (await _choices.GetAsync(Context(_userAId), second)).Household?.Id);
            await using var db = await _factory.CreateDbContextAsync();
            Assert.Single(await db.RecordingLyricsChoices.Where(item => item.RevokedAt == null).ToArrayAsync());
            Assert.NotNull((await db.RecordingLyricsChoices.SingleAsync(item => item.CanonicalRecordingId == second)).RevokedAt);
        }
        else
        {
            await Assert.ThrowsAsync<RecordingMergeConflictException>(() => _identities.MergeRecordingsAsync(
                Context(_adminId, isAdministrator: true).RequireActor(), first, second, "merge"));
            await using var db = await _factory.CreateDbContextAsync();
            Assert.All(await db.CanonicalRecordings.ToArrayAsync(), item => Assert.Null(item.MergedIntoId));
            Assert.Equal(2, await db.RecordingLyricsChoices.CountAsync(item => item.RevokedAt == null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationRechecksRoleAndRecordingInsideItsWriteTransaction(bool clear)
    {
        var recording = await CreateRecordingAsync(_adminId);
        var first = await _choices.SetAsync(Context(_adminId, isAdministrator: true), recording, "lrclib", "household", new());
        var factory = new BeforeWriteFactory(_factory, async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            (await db.Users.SingleAsync(user => user.Id == _adminId)).IsAdmin = false;
            await db.SaveChangesAsync();
        });
        var raced = new RecordingLyricsChoices(factory, _identities, _clock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => clear
            ? raced.ClearAsync(Context(_adminId, isAdministrator: true), recording, "household", first.Revision)
            : raced.SetAsync(Context(_adminId, isAdministrator: true), recording, "spotify", "household", first.Revision));
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal("lrclib", (await verify.RecordingLyricsChoices.SingleAsync()).SourceId);
    }

    [Fact]
    public async Task MutationDoesNotAttachAChoiceToARecordingMergedAfterAuthorization()
    {
        var survivor = await CreateRecordingAsync(_adminId);
        var recording = await CreateRecordingAsync(_adminId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.CanonicalRecordings.SingleAsync(item => item.Id == recording)).CreatedAt = _clock.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync();
        }
        var factory = new BeforeWriteFactory(_factory, async () =>
        {
            await _identities.MergeRecordingsAsync(Context(_adminId, isAdministrator: true).RequireActor(),
                survivor, recording, "race");
        });
        var raced = new RecordingLyricsChoices(factory, _identities, _clock);
        await Assert.ThrowsAsync<LyricsChoiceConflictException>(() => raced.SetAsync(
            Context(_adminId, isAdministrator: true), recording, "lrclib", "personal", new()));
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.RecordingLyricsChoices.ToArrayAsync());
    }

    private sealed class BeforeWriteFactory(IDbContextFactory<AllstarrDbContext> inner, Func<Task> beforeWrite)
        : IDbContextFactory<AllstarrDbContext>
    {
        private int calls;
        public AllstarrDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 2) await beforeWrite();
            return await inner.CreateDbContextAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task PersonalLayerWinsOverHouseholdAndExposesBothRevisionStamps()
    {
        var recordingId = await CreateRecordingAsync(_adminId);
        var household = await _choices.SetAsync(
            Context(_adminId, isAdministrator: true), recordingId, "backend", "household", new());
        Assert.NotNull(household.Household);

        var personal = await _choices.SetAsync(
            Context(_userAId), recordingId, "lyricsplus", "personal", household.Revision);

        Assert.NotNull(personal.Personal);
        Assert.Equal("lyricsplus", personal.Personal!.SourceId);
        Assert.Equal(personal.Personal, personal.Effective);
        Assert.Equal(personal.Personal.Id, personal.Revision.Personal!.Id);
        Assert.Equal(personal.Household!.Id, personal.Revision.Household!.Id);
    }

    [Fact]
    public async Task PersonalChoicesAreIsolatedBetweenListeners()
    {
        var recordingId = await CreateRecordingAsync(_userAId);
        var first = await _choices.SetAsync(
            Context(_userAId), recordingId, "spotify", "personal", new());
        var second = await _choices.SetAsync(
            Context(_userBId), recordingId, "deezer", "personal", new());

        Assert.Equal("spotify", first.Personal!.SourceId);
        Assert.Equal("deezer", second.Personal!.SourceId);
        Assert.NotEqual(first.Personal.Id, second.Personal!.Id);
    }

    [Fact]
    public async Task HouseholdMutationRequiresCurrentAdministratorRole()
    {
        var recordingId = await CreateRecordingAsync(_adminId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _choices.SetAsync(
            Context(_userAId), recordingId, "lyricsplus", "household", new()));

        // The database role is not enough when an old session does not carry the
        // administrator claim that was verified by the backend boundary.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _choices.SetAsync(
            Context(_adminId, isAdministrator: false), recordingId, "lyricsplus", "household", new()));

        var result = await _choices.SetAsync(
            Context(_adminId, isAdministrator: true), recordingId, "lyricsplus", "household", new());
        Assert.Equal("lyricsplus", result.Household!.SourceId);
    }

    [Fact]
    public async Task StaleRevisionVectorConflictsAndDoesNotOverwrite()
    {
        var recordingId = await CreateRecordingAsync(_adminId);
        var first = await _choices.SetAsync(
            Context(_adminId, isAdministrator: true), recordingId, "lyricsplus", "household", new());

        await Assert.ThrowsAsync<LyricsChoiceConflictException>(() => _choices.SetAsync(
            Context(_adminId, isAdministrator: true), recordingId, "spotify", "household", new()));

        var current = await _choices.GetAsync(Context(_userAId), recordingId);
        Assert.Equal(first.Household, current.Household);
    }

    [Fact]
    public async Task ClearingPersonalChoiceRestoresHouseholdInheritance()
    {
        var recordingId = await CreateRecordingAsync(_adminId);
        var household = await _choices.SetAsync(
            Context(_adminId, isAdministrator: true), recordingId, "backend", "household", new());
        var personal = await _choices.SetAsync(
            Context(_userAId), recordingId, "lyricsplus", "personal", household.Revision);

        var cleared = await _choices.ClearAsync(Context(_userAId), recordingId, "personal", personal.Revision);

        Assert.Null(cleared.Personal);
        Assert.Equal("backend", cleared.Effective!.SourceId);
        Assert.Equal(household.Household!.Id, cleared.Household!.Id);
    }

    [Fact]
    public async Task ChoiceSurvivesServiceRestart()
    {
        var recordingId = await CreateRecordingAsync(_userAId);
        var saved = await _choices.SetAsync(
            Context(_userAId), recordingId, "spotify", "personal", new());
        var restarted = new RecordingLyricsChoices(_factory, _identities, _clock);

        var loaded = await restarted.GetAsync(Context(_userAId), recordingId);

        Assert.Equal(saved.Personal, loaded.Personal);
    }

    [Fact]
    public async Task RejectsMalformedScopeSourceAndUnknownRecording()
    {
        var recordingId = await CreateRecordingAsync(_userAId);
        await Assert.ThrowsAsync<ArgumentException>(() => _choices.SetAsync(
            Context(_userAId), recordingId, "invalid source", "personal", new()));
        await Assert.ThrowsAsync<ArgumentException>(() => _choices.SetAsync(
            Context(_userAId), recordingId, "spotify", "global", new()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _choices.GetAsync(
            Context(_userAId), Guid.CreateVersion7()));
    }

    [Fact]
    public async Task RequiresLinkedEnabledUserWithMatchingBackendIdentity()
    {
        var recordingId = await CreateRecordingAsync(_userAId);
        var unlinked = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "fixture",
            "anonymous",
            null,
            "unlinked-choice",
            _clock.UtcNow.AddMinutes(1),
            CancellationToken.None);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _choices.GetAsync(unlinked, recordingId));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(item => item.Id == _userAId);
            user.Enabled = false;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _choices.GetAsync(Context(_userAId), recordingId));

        var mismatched = Context(_userAId, backendType: "subsonic");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _choices.GetAsync(mismatched, recordingId));
    }

    [Fact]
    public async Task HonorsCancellationBeforeOpeningStorage()
    {
        var recordingId = await CreateRecordingAsync(_userAId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _choices.GetAsync(Context(_userAId, cancellationToken: cancellation.Token), recordingId));
    }

    [Fact]
    public async Task RepeatedSameSourceIsANoopAndAuditsSafeIdentifiersOnly()
    {
        var recordingId = await CreateRecordingAsync(_userAId);
        var first = await _choices.SetAsync(
            Context(_userAId), recordingId, "spotify", "personal", new());
        var second = await _choices.SetAsync(
            Context(_userAId), recordingId, "spotify", "personal", first.Revision);

        Assert.Equal(first.Personal, second.Personal);
        await using var db = await _factory.CreateDbContextAsync();
        var audits = await db.AuditEvents.AsNoTracking()
            .Where(item => item.Category == "lyrics" && item.Action == "choice.set")
            .ToListAsync();
        Assert.Single(audits);
        Assert.Contains(recordingId.ToString("D"), audits[0].DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", audits[0].DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", audits[0].DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentSameVectorHasAtMostOneWinner()
    {
        var recordingId = await CreateRecordingAsync(_adminId);
        var expected = new LyricsChoiceRevision();
        var attempts = await Task.WhenAll(
            AttemptSetAsync(recordingId, "spotify", expected),
            AttemptSetAsync(recordingId, "deezer", expected));

        Assert.Equal(1, attempts.Count(item => item is null));
        Assert.Equal(1, attempts.Count(item => item is LyricsChoiceConflictException));
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private async Task<Exception?> AttemptSetAsync(
        Guid recordingId,
        string sourceId,
        LyricsChoiceRevision expected)
    {
        try
        {
            await _choices.SetAsync(
                Context(_adminId, isAdministrator: true), recordingId, sourceId, "household", expected);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task<Guid> CreateRecordingAsync(Guid ownerUserId)
    {
        var created = await _identities.CreateRecordingAsync(
            Context(ownerUserId, isAdministrator: ownerUserId == _adminId).RequireActor(),
            "recording-lyrics-choice-fixture");
        return created.Recording.Id;
    }

    private ProtocolExecutionContext Context(
        Guid userId,
        bool isAdministrator = false,
        CancellationToken cancellationToken = default,
        string backendType = "jellyfin")
    {
        var principal = new AllstarrPrincipal(
            userId,
            backendType,
            "fixture",
            userId.ToString("N"),
            userId == _adminId ? "Admin" : "Listener",
            isAdministrator);
        return new ProtocolExecutionContext(
            backendType == "subsonic" ? ProtocolKind.Subsonic : ProtocolKind.Jellyfin,
            "fixture",
            principal.BackendPrincipalId,
            principal,
            "lyrics-choice-fixture",
            _clock.UtcNow.AddMinutes(1),
            cancellationToken);
    }

    private static UserRecord User(
        Guid id,
        string name,
        bool isAdmin,
        DateTimeOffset now) => new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = id.ToString("N"),
            DisplayName = name,
            IsAdmin = isAdmin,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        };

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
