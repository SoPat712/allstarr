using allstarr.Core.Identity;
using Microsoft.EntityFrameworkCore;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CanonicalKeptLyricsSurviveSourceAccountRetirementAndIgnoreSongLocalPath()
    {
        UsePhysicalKeptLyricsRoot();
        var published = await Service().PublishAsync(Request());
        var sidecar = Path.ChangeExtension(published.Path, ".lrc");
        await File.WriteAllTextAsync(sidecar, "[offset:-1250]\n[00:01.234]Household words");
        await RetireSourceAccountAsync();

        var song = new Song
        {
            Id = "forged-client-id",
            CanonicalRecordingId = recordingId,
            Title = "Retained title",
            Artist = "Retained artist",
            Album = "Retained album",
            LocalPath = Path.Combine(root, "untrusted", "client.mp3")
        };
        var reader = CreateKeptLyricsReader(Path.Combine(root, "unrelated-download-root"));

        var result = await reader.ReadAsync(LyricsContext(other), song,
            [new KeptLyricsIdentity("deezer", "source-track")]);
        var household = await reader.ReadHouseholdAsync(LyricsContext(other), song,
            [new KeptLyricsIdentity("deezer", "source-track")]);

        Assert.Equal("kept", result?.Source);
        Assert.Equal("[offset:-1250]\n[00:01.234]Household words", result?.SyncedLyrics);
        Assert.Equal(song.Title, result?.TrackName);
        Assert.Equal(result?.SyncedLyrics, household?.SyncedLyrics);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ProviderAliasKeptLyricsUsePublishedRouteWithoutMappingsOrSourceCredentials()
    {
        UsePhysicalKeptLyricsRoot();
        var published = await Service().PublishAsync(Request());
        await File.WriteAllTextAsync(Path.ChangeExtension(published.Path, ".lrc"), "[00:02.500]Alias words");
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var identity = await db.ProviderTrackIdentities.SingleAsync();
            identity.Scope = ProviderIdentityScope.Account;
            identity.ProviderAccountId = account;
            var providerAccount = await db.ProviderAccounts.SingleAsync(item => item.Id == account);
            providerAccount.Enabled = false;
            providerAccount.Revision++;
            await db.SaveChangesAsync();
            Assert.Empty(await db.DownloadedSongMappings.ToArrayAsync());
        }

        var song = new Song
        {
            Id = "ext-deezer-song-source-track",
            ExternalProvider = "deezer",
            ExternalId = "source-track",
            Title = "Alias title",
            Artist = "Alias artist"
        };
        var reader = CreateKeptLyricsReader(Path.Combine(root, "no-configured-kept-root"));

        var result = await reader.ReadHouseholdAsync(LyricsContext(other), song,
            [new KeptLyricsIdentity("deezer", "source-track")]);

        Assert.NotNull(result);
        Assert.Equal("[00:02.500]Alias words", result.SyncedLyrics);
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeptLyricsRejectForeignBackendAndUnlinkedActors()
    {
        UsePhysicalKeptLyricsRoot();
        var published = await Service().PublishAsync(Request());
        await File.WriteAllTextAsync(Path.ChangeExtension(published.Path, ".lrc"), "[00:00.000]Private route");
        var reader = CreateKeptLyricsReader(root);
        var song = new Song { CanonicalRecordingId = recordingId, LocalPath = published.Path };

        var publicContext = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin, "fixture", "unlinked", null, "kept-lyrics-public",
            DateTimeOffset.UtcNow.AddMinutes(1), default);
        var foreignBackend = new ProtocolExecutionContext(
            ProtocolKind.Subsonic, "other-instance", other.ToString("N"),
            new AllstarrPrincipal(other, "subsonic", "other-instance", other.ToString("N"), "Other", false),
            "kept-lyrics-foreign", DateTimeOffset.UtcNow.AddMinutes(1), default);

        Assert.Null(await reader.ReadAsync(publicContext, song, []));
        Assert.Null(await reader.ReadAsync(foreignBackend, song, []));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeptSidecarMustBeRegularExactSiblingButProofMayUseCustomRoot()
    {
        UsePhysicalKeptLyricsRoot();
        var published = await Service().PublishAsync(Request());
        var externalSidecar = Path.Combine(root, "external", "lyrics.lrc");
        Directory.CreateDirectory(Path.GetDirectoryName(externalSidecar)!);
        await File.WriteAllTextAsync(externalSidecar, "[00:00.000]Outside");
        var sidecar = Path.ChangeExtension(published.Path, ".lrc");
        File.CreateSymbolicLink(sidecar, externalSidecar);
        var reader = CreateKeptLyricsReader(Path.Combine(root, "does-not-contain-placement"));
        var song = new Song { CanonicalRecordingId = recordingId, Title = "Kept", Artist = "Artist" };

        Assert.Null(await reader.ReadAsync(LyricsContext(other), song, []));

        File.Delete(sidecar);
        await File.WriteAllTextAsync(sidecar, "[00:00.000]Exact sibling");
        var result = await reader.ReadAsync(LyricsContext(other), song, []);
        Assert.Equal("[00:00.000]Exact sibling", result?.SyncedLyrics);
    }

    private void UsePhysicalKeptLyricsRoot()
    {
        Directory.CreateDirectory(placement.RootPath);
        placement.RootPath = Resolve(new DirectoryInfo(placement.RootPath));
        static string Resolve(DirectoryInfo directory) =>
            directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
            (directory.Parent == null ? directory.FullName : Path.Combine(Resolve(directory.Parent), directory.Name));
    }

    private KeptLyricsReader CreateKeptLyricsReader(string downloadRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = downloadRoot
            })
            .Build();
        return new KeptLyricsReader(factory, configuration, Service());
    }

    private ProtocolExecutionContext LyricsContext(Guid userId) => new(
        ProtocolKind.Jellyfin,
        "fixture",
        userId.ToString("N"),
        new AllstarrPrincipal(userId, "jellyfin", "fixture", userId.ToString("N"), "Listener", false),
        "kept-lyrics",
        DateTimeOffset.UtcNow.AddMinutes(1),
        default);

    private async Task RetireSourceAccountAsync()
    {
        await using var db = new AllstarrDbContext(database.Options);
        var source = await db.ProviderAccounts.SingleAsync(item => item.Id == account);
        source.Enabled = false;
        source.Revision++;
        await db.SaveChangesAsync();
    }
}
