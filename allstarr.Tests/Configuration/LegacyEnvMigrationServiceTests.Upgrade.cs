using System.Text.Json;
using allstarr.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class LegacyEnvMigrationServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upgrade_AccountsAreCheckedBeforeWriterAndOnlyHealthyAccountsEnable(bool healthy)
    {
        var verifier = new UpgradeVerifier(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            Assert.Empty(await db.ProviderAccounts.ToListAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET \"DisplayName\"='Verified admin'");
            return healthy;
        });
        var service = CreateService(verifier: verifier);
        var preview = await service.PreviewAsync(Source("DEEZER_ARL=fixture-private-arl"), Actor());
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        var replay = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(replay.AlreadyApplied);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(healthy, Assert.Single(result.AccountChecks).Enabled);
        Assert.DoesNotContain("fixture-private-arl", JsonSerializer.Serialize(result));
        await using var saved = await _factory.CreateDbContextAsync();
        var account = await saved.ProviderAccounts.SingleAsync();
        Assert.Equal(healthy, account.Enabled);
        Assert.Equal(_userId, account.CreatedByUserId);
        Assert.Equal(0, account.Revision);
        Assert.NotNull(account.SecretReferenceId);
    }

    private sealed class UpgradeVerifier(Func<Task<bool>> verify) : ILegacyAccountVerifier
    {
        public int Calls;
        public Task<bool> VerifyAsync(string providerId, Guid accountId, ReadOnlyMemory<byte> credential, CancellationToken token)
        {
            Calls++;
            return verify();
        }
    }

    [Fact]
    public void Upgrade_EmptyDuplicateNeverErasesAnEarlierCredential()
    {
        var document = LegacyEnvParser.Parse(Source("DEEZER_ARL=fixture-private-arl\nDEEZER_ARL=\n"));
        Assert.Equal("fixture-private-arl", Assert.Single(document.Entries).Value);
        Assert.Equal(1, document.Entries[0].LineNumber);
        Assert.Equal([2], document.Entries[0].OverriddenLineNumbers);
    }

    [Fact]
    public async Task Upgrade_LastOrderedPlaylistKeepsItsExistingTargetAndSourceOrder()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            SPOTIFY_API_SESSION_COOKIE=fixture-cookie
            SPOTIFY_IMPORT_PLAYLISTS=[["Playlist","source-id","target-id","last","0 8 * * *"]]
            """), Actor());
        Assert.Equal("import_playlist_link", Assert.Single(preview.PlaylistHandoffs).Action);
        Assert.Contains("source order", Assert.Single(preview.PlaylistHandoffs).Reason);
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.Equal(1, result.PlaylistLinksCreated);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("target-id", (await db.PlaylistLinks.SingleAsync()).TargetPlaylistId);
    }

    [Fact]
    public async Task Upgrade_BackendMismatchBlocksApply()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("BACKEND_TYPE=Subsonic"), Actor());
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Conflicts, item => item.Contains("backend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Upgrade_VerificationCannotImportAfterAdministratorRevocation()
    {
        var verifier = new UpgradeVerifier(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET \"Enabled\"=0");
            return true;
        });
        var service = CreateService(verifier: verifier);
        var preview = await service.PreviewAsync(Source("DEEZER_ARL=fixture-private-arl"), Actor());
        await Assert.ThrowsAsync<LegacyEnvMigrationException>(() => service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor()));
        await using var saved = await _factory.CreateDbContextAsync();
        Assert.Empty(await saved.ProviderAccounts.ToListAsync());
        Assert.Empty(await saved.SecretReferences.ToListAsync());
        Assert.Empty(await saved.LegacyEnvImports.ToListAsync());
    }

    [Fact]
    public async Task Upgrade_RegistryIsReviewedImportedDisabledAndReplayedOnce()
    {
        var service = CreateService();
        var bytes = Source("EXTENSION_REPOSITORIES=https://registry.example.test/index.json");
        var preview = await service.PreviewAsync(bytes, Actor());
        Assert.Equal("import_registry", Assert.Single(preview.Items).Action);
        await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        var restart = CreateService();
        preview = await restart.PreviewAsync(bytes, Actor());
        var replay = await restart.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(replay.AlreadyApplied);
        await using var db = await _factory.CreateDbContextAsync();
        var registry = Assert.Single(await db.ExtensionRegistries.ToListAsync());
        Assert.Equal("https://registry.example.test/index.json", registry.RegistryUrl);
        Assert.False(registry.Enabled);
    }

    [Fact]
    public void Upgrade_RetiresOldAppleEndpointAndExplainsSquidWtf()
    {
        var document = LegacyEnvParser.Parse(Source("""
            APPLE_MUSIC_AIO_URL=http://old-apple:8000
            MUSIC_SERVICE=SquidWTF
            MULTI_PROVIDER_ENABLED_SEARCH=spotify,applemusic,deezer,qobuz,squidwtf
            CACHE_SPOTIFY_MATCHED_TRACKS_DAYS=30
            CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS=168
            SQUIDWTF_QUALITY=LOSSLESS
            SQUIDWTF_MIN_REQUEST_INTERVAL_MS=200
            MAX_CONCURRENT_DOWNLOADS=2
            COMPOSE_BAKE=true
            """));
        Assert.DoesNotContain(document.Entries, item => item.Disposition == LegacyEnvDisposition.Unknown);
        Assert.Equal("setup_apple", document.Entries.Single(item => item.Key == "APPLE_MUSIC_AIO_URL").Action);
        Assert.Null(document.Entries.Single(item => item.Key == "APPLE_MUSIC_AIO_URL").DurableKey);
        Assert.Equal("choose_source", document.Entries.Single(item => item.Key == "MUSIC_SERVICE").Action);
        Assert.DoesNotContain("squidwtf", document.Entries.Single(item => item.Key == "MULTI_PROVIDER_ENABLED_SEARCH").Value);
    }

    [Fact]
    public void Upgrade_PublicExampleHasNoUnclassifiedKeys()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "allstarr.sln"))) root = root.Parent;
        var source = File.ReadAllBytes(Path.Combine(root!.FullName, "allstarr.Tests/Fixtures/LegacyV2/example.env"));
        var document = LegacyEnvParser.Parse(source);
        Assert.Empty(document.Entries.Where(item => item.Disposition == LegacyEnvDisposition.Unknown).Select(item => item.Key));
    }
}
