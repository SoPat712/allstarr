using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class KeptLyricsReaderTests
{
    [Theory]
    [InlineData("anonymous", false, true)]
    [InlineData("shared", false, true)]
    [InlineData("personal", false, false)]
    [InlineData("shared", true, false)]
    public async Task HouseholdPublicationNeverReadsPersonalAccountsOrOwnedFiles(string accountScope, bool personalFile, bool expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = Guid.CreateVersion7();
        await fixture.AddUserAsync(user);
        Guid? account = accountScope == "anonymous" ? null : Guid.CreateVersion7();
        if (account.HasValue) await fixture.AddAccountAsync(account.Value, "fixture", accountScope == "personal" ? user : null);
        var path = await fixture.WriteAudioAsync("kept/track.mp3", "[00:01.234]Shared words");
        await fixture.AddMappingAsync("fixture", "track", path, account);
        if (personalFile) await fixture.AddManagedFileAsync(path, user);
        var result = await fixture.Reader.ReadHouseholdAsync(fixture.Context(user, admin: true),
            new Song { Title = "Fixture", Artist = "Artist" }, [new("fixture", "track")]);
        Assert.Equal(expected, result != null);
        if (expected) Assert.Equal("[00:01.234]Shared words", result!.SyncedLyrics);
    }

    [Fact]
    public async Task TimedCandidateWinsOverEarlierPlainSidecarWithoutWritingEither()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = Guid.CreateVersion7();
        await fixture.AddUserAsync(user);
        var plain = await fixture.WriteAudioAsync("kept/plain.mp3", "Plain words");
        var timed = await fixture.WriteAudioAsync("permanent/timed.mp3", "\uFEFF[00:01.234]Timed words");
        await fixture.AddMappingAsync("fixture", "plain", plain);
        await fixture.AddMappingAsync("fixture", "timed", timed);
        var result = await fixture.Reader.ReadAsync(fixture.Context(user), new Song { Title = "Fixture", Artist = "Artist" },
            [new("fixture", "plain"), new("fixture", "timed")]);
        Assert.Equal("[00:01.234]Timed words", result?.SyncedLyrics);
        Assert.Null(result?.PlainLyrics);
        Assert.Equal("Plain words", await File.ReadAllTextAsync(Path.ChangeExtension(plain, ".lrc")));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("\uFEFF[00:01.234]Timed words"),
            await File.ReadAllBytesAsync(Path.ChangeExtension(timed, ".lrc")));
    }

    [Fact]
    public async Task PlainContentExcludesLrcMetadataAndPreservesActualBracketedWords()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = Guid.CreateVersion7();
        await fixture.AddUserAsync(user);
        var path = await fixture.WriteAudioAsync("kept/plain.mp3", "[ar:Artist]\n[ti:Song]\n[Chorus]\nActual words");
        await fixture.AddMappingAsync("fixture", "plain", path);
        var result = await fixture.Reader.ReadAsync(fixture.Context(user), new Song { Title = "Fixture", Artist = "Artist" },
            [new("fixture", "plain")]);
        Assert.Equal("[Chorus]\nActual words", result?.PlainLyrics);
        Assert.Null(result?.SyncedLyrics);
    }

    [Fact]
    public async Task ReadsExactMappingAndPreservesRawTimedLyricsAndSongMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var audioPath = await fixture.WriteAudioAsync(
            "kept/Artist/track.mp3",
            "[offset:-1250]\n[00:01.234]Kept words");
        await fixture.AddMappingAsync("fixture", "track", audioPath);

        var song = new Song
        {
            Id = "client-item",
            Title = "Song title",
            Artist = "Song artist",
            Artists = ["Song artist", "Featured artist"],
            Album = "Song album",
            Duration = 321,
            PreciseDuration = TimeSpan.FromMilliseconds(321_500),
            LocalPath = Path.Combine(fixture.Root, "untrusted.mp3")
        };

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            song,
            [new KeptLyricsIdentity("fixture", "track")]);

        Assert.NotNull(result);
        Assert.Equal("kept", result.Source);
        Assert.Equal(song.Title, result.TrackName);
        Assert.Equal(song.Artist, result.ArtistName);
        Assert.Equal(song.Album, result.AlbumName);
        Assert.Equal(321, result.Duration);
        Assert.Equal("[offset:-1250]\n[00:01.234]Kept words", result.SyncedLyrics);
        Assert.Null(result.PlainLyrics);
    }

    [Fact]
    public async Task ReadsPlainLyricsWhenTheSidecarHasRealText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var audioPath = await fixture.WriteAudioAsync(
            "kept/Artist/plain.mp3",
            "Plain first line\nPlain second line");
        await fixture.AddMappingAsync("fixture", "plain", audioPath);

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Plain", Artist = "Artist", Album = "Album" },
            [new KeptLyricsIdentity("fixture", "plain")]);

        Assert.NotNull(result);
        Assert.Equal("Plain first line\nPlain second line", result.PlainLyrics);
        Assert.Null(result.SyncedLyrics);
        Assert.Equal("kept", result.Source);
    }

    [Fact]
    public async Task UsesOnlyExactSourceIdentitiesAndContinuesToTheNextCandidate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var wrongPath = await fixture.WriteAudioAsync("kept/Artist/wrong.mp3", "wrong");
        var rightPath = await fixture.WriteAudioAsync("kept/Artist/right.mp3", "[00:00.000]right");
        await fixture.AddMappingAsync("other", "track", wrongPath);
        await fixture.AddMappingAsync("fixture", "track", rightPath);

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Track", Artist = "Artist" },
            [
                new KeptLyricsIdentity("fixture", "missing"),
                new KeptLyricsIdentity("fixture", "track")
            ]);

        Assert.NotNull(result);
        Assert.Equal("[00:00.000]right", result.SyncedLyrics);

        var exactMismatch = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Track", Artist = "Artist" },
            [new KeptLyricsIdentity("other", "missing")]);
        Assert.Null(exactMismatch);
    }

    [Fact]
    public async Task RequiresLinkedEnabledUserWithMatchingBackendIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        var disabledId = Guid.CreateVersion7();
        var mismatchedId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        await fixture.AddUserAsync(disabledId, enabled: false, backendPrincipal: "disabled");
        await fixture.AddUserAsync(mismatchedId, backendType: "subsonic");
        var audioPath = await fixture.WriteAudioAsync("kept/Artist/linked.mp3", "[00:00.000]linked");
        await fixture.AddMappingAsync("fixture", "linked", audioPath);
        var song = new Song { Title = "Linked", Artist = "Artist" };

        Assert.Null(await fixture.Reader.ReadAsync(
            fixture.Context(null), song, [new KeptLyricsIdentity("fixture", "linked")]));
        Assert.Null(await fixture.Reader.ReadAsync(
            fixture.Context(disabledId, backendPrincipal: "disabled"), song, [new KeptLyricsIdentity("fixture", "linked")]));
        Assert.Null(await fixture.Reader.ReadAsync(
            fixture.Context(mismatchedId), song, [new KeptLyricsIdentity("fixture", "linked")]));
        Assert.NotNull(await fixture.Reader.ReadAsync(
            fixture.Context(userId), song, [new KeptLyricsIdentity("fixture", "linked")]));
    }

    [Fact]
    public async Task PersonalAccountRequiresItsOwnerWhileSharedAccountIsAvailableToTheListener()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adminId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        await fixture.AddUserAsync(adminId);
        await fixture.AddUserAsync(ownerId, backendPrincipal: "owner");

        var foreignAccount = Guid.CreateVersion7();
        var sharedAccount = Guid.CreateVersion7();
        var ownAccount = Guid.CreateVersion7();
        await fixture.AddAccountAsync(foreignAccount, "fixture", ownerId);
        await fixture.AddAccountAsync(sharedAccount, "fixture", null);
        await fixture.AddAccountAsync(ownAccount, "fixture", adminId);

        var foreignPath = await fixture.WriteAudioAsync("kept/Artist/foreign.mp3", "[00:00.000]foreign");
        var sharedPath = await fixture.WriteAudioAsync("kept/Artist/shared.mp3", "[00:00.000]shared");
        var ownPath = await fixture.WriteAudioAsync("kept/Artist/own.mp3", "[00:00.000]own");
        await fixture.AddMappingAsync("fixture", "foreign", foreignPath, foreignAccount);
        await fixture.AddMappingAsync("fixture", "shared", sharedPath, sharedAccount);
        await fixture.AddMappingAsync("fixture", "own", ownPath, ownAccount);

        var admin = fixture.Context(adminId, admin: true);
        var foreign = await fixture.Reader.ReadAsync(
            admin,
            new Song { Title = "Foreign", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "foreign")]);
        Assert.Null(foreign);

        var shared = await fixture.Reader.ReadAsync(
            admin,
            new Song { Title = "Shared", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "shared")]);
        Assert.NotNull(shared);
        Assert.Equal("[00:00.000]shared", shared.SyncedLyrics);

        var own = await fixture.Reader.ReadAsync(
            admin,
            new Song { Title = "Own", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "own")]);
        Assert.NotNull(own);
        Assert.Equal("[00:00.000]own", own.SyncedLyrics);
    }

    [Fact]
    public async Task RejectsDisabledMismatchedAndMissingProviderAccounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var disabled = Guid.CreateVersion7();
        var mismatched = Guid.CreateVersion7();
        var missing = Guid.CreateVersion7();
        await fixture.AddAccountAsync(disabled, "fixture", userId, enabled: false);
        await fixture.AddAccountAsync(mismatched, "other-provider", userId);

        foreach (var (externalId, accountId) in new[]
        {
            ("disabled", disabled),
            ("mismatched", mismatched),
            ("missing", missing)
        })
        {
            var path = await fixture.WriteAudioAsync($"kept/Artist/{externalId}.mp3", "[00:00.000]unavailable");
            await fixture.AddMappingAsync("fixture", externalId, path, accountId);
        }

        var identities = new[]
        {
            new KeptLyricsIdentity("fixture", "disabled"),
            new KeptLyricsIdentity("fixture", "mismatched"),
            new KeptLyricsIdentity("fixture", "missing")
        };
        Assert.Null(await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Unavailable", Artist = "Artist" },
            identities));
    }

    [Fact]
    public async Task RejectsMissingAudioAndMissingSidecar()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var missingAudio = Path.Combine(fixture.KeptRoot, "Artist", "missing.mp3");
        var noSidecar = await fixture.WriteAudioAsync("kept/Artist/no-sidecar.mp3", null);
        await fixture.AddMappingAsync("fixture", "missing-audio", missingAudio);
        await fixture.AddMappingAsync("fixture", "no-sidecar", noSidecar);

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Missing", Artist = "Artist" },
            [
                new KeptLyricsIdentity("fixture", "missing-audio"),
                new KeptLyricsIdentity("fixture", "no-sidecar")
            ]);

        Assert.Null(result);
    }

    [Fact]
    public async Task RejectsCacheOutsideSiblingAndTraversalPaths()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var outside = Path.Combine(fixture.Root, "outside", "outside.mp3");
        var cache = Path.Combine(fixture.DownloadRoot, "cache", "cache.mp3");
        var sibling = Path.Combine(fixture.DownloadRoot, "kept-malicious", "sibling.mp3");
        var traversal = Path.Combine(fixture.DownloadRoot, "kept", "..", "kept-malicious", "traversal.mp3");
        foreach (var path in new[] { outside, cache, sibling, traversal })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "audio");
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "[00:00.000]unsafe");
        }

        var paths = new[] { outside, cache, sibling, traversal };
        var ids = new List<KeptLyricsIdentity>();
        for (var index = 0; index < paths.Length; index++)
        {
            var externalId = $"unsafe-{index}";
            await fixture.AddMappingAsync("fixture", externalId, paths[index]);
            ids.Add(new KeptLyricsIdentity("fixture", externalId));
        }

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Unsafe", Artist = "Artist" },
            ids);
        Assert.Null(result);
    }

    [Fact]
    public async Task RejectsWrongOwnerAndRemovedManagedFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        var otherId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        await fixture.AddUserAsync(otherId, backendPrincipal: "other");
        var wrongOwnerPath = await fixture.WriteAudioAsync("kept/Artist/wrong-owner.mp3", "[00:00.000]wrong");
        var removedPath = await fixture.WriteAudioAsync("kept/Artist/removed.mp3", "[00:00.000]removed");
        await fixture.AddMappingAsync("fixture", "wrong-owner", wrongOwnerPath);
        await fixture.AddMappingAsync("fixture", "removed", removedPath);
        await fixture.AddManagedFileAsync(wrongOwnerPath, otherId);
        await fixture.AddManagedFileAsync(removedPath, null, removedAt: DateTimeOffset.UtcNow);

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Managed", Artist = "Artist" },
            [
                new KeptLyricsIdentity("fixture", "wrong-owner"),
                new KeptLyricsIdentity("fixture", "removed")
            ]);

        Assert.Null(result);
    }

    [Fact]
    public async Task RejectsFileAndDirectorySymlinks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var externalRoot = Path.Combine(fixture.Root, "external");
        var externalAudio = Path.Combine(externalRoot, "audio.mp3");
        var externalDirectory = Path.Combine(externalRoot, "album");
        var externalDirectoryAudio = Path.Combine(externalDirectory, "track.mp3");
        Directory.CreateDirectory(externalDirectory);
        await File.WriteAllTextAsync(externalAudio, "audio");
        await File.WriteAllTextAsync(Path.ChangeExtension(externalAudio, ".lrc"), "[00:00.000]file");
        await File.WriteAllTextAsync(externalDirectoryAudio, "audio");
        await File.WriteAllTextAsync(Path.ChangeExtension(externalDirectoryAudio, ".lrc"), "[00:00.000]directory");

        var fileLink = Path.Combine(fixture.KeptRoot, "file-link.mp3");
        var directoryLink = Path.Combine(fixture.KeptRoot, "directory-link");
        File.CreateSymbolicLink(fileLink, externalAudio);
        await File.WriteAllTextAsync(Path.ChangeExtension(fileLink, ".lrc"), "[00:00.000]linked audio");
        Directory.CreateSymbolicLink(directoryLink, externalDirectory);

        await fixture.AddMappingAsync("fixture", "file-link", fileLink);
        await fixture.AddMappingAsync("fixture", "directory-link", Path.Combine(directoryLink, "track.mp3"));
        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Links", Artist = "Artist" },
            [
                new KeptLyricsIdentity("fixture", "file-link"),
                new KeptLyricsIdentity("fixture", "directory-link")
            ]);
        Assert.Null(result);
    }

    [Fact]
    public async Task RejectsSidecarAndConfiguredRootSymlinks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);

        var externalSidecar = Path.Combine(fixture.Root, "external", "lyrics.lrc");
        Directory.CreateDirectory(Path.GetDirectoryName(externalSidecar)!);
        await File.WriteAllTextAsync(externalSidecar, "[00:00.000]sidecar link");
        var audioPath = await fixture.WriteAudioAsync("kept/Artist/sidecar-link.mp3", null);
        var sidecarLink = Path.ChangeExtension(audioPath, ".lrc");
        File.CreateSymbolicLink(sidecarLink, externalSidecar);
        await fixture.AddMappingAsync("fixture", "sidecar-link", audioPath);

        var aliasedRoot = Path.Combine(fixture.Root, "downloads-alias");
        Directory.CreateSymbolicLink(aliasedRoot, fixture.DownloadRoot);
        var aliasedAudio = Path.Combine(aliasedRoot, "kept", "aliased.mp3");
        await File.WriteAllTextAsync(aliasedAudio, "audio");
        await File.WriteAllTextAsync(Path.ChangeExtension(aliasedAudio, ".lrc"), "[00:00.000]root link");
        await fixture.AddMappingAsync("fixture", "aliased", aliasedAudio);
        var aliasedReader = fixture.ReaderFor(aliasedRoot);

        Assert.Null(await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Sidecar", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "sidecar-link")]));
        Assert.Null(await aliasedReader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Aliased", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "aliased")]));
    }

    [Fact]
    public async Task MetadataOnlySidecarIsUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var path = await fixture.WriteAudioAsync(
            "kept/Artist/metadata.mp3",
            "[ar:Artist]\n[al:Album]\n[ti:Title]\n[by:Writer]");
        await fixture.AddMappingAsync("fixture", "metadata", path);

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Metadata", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "metadata")]);
        Assert.Null(result);
    }

    [Fact]
    public async Task RejectsOversizedAsciiAndUnicodeSidecars()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var asciiPath = await fixture.WriteAudioAsync("kept/Artist/ascii.mp3", null);
        var unicodePath = await fixture.WriteAudioAsync("kept/Artist/unicode.mp3", null);
        await File.WriteAllTextAsync(Path.ChangeExtension(asciiPath, ".lrc"), new string('x', 2 * 1024 * 1024 + 1));
        await File.WriteAllTextAsync(Path.ChangeExtension(unicodePath, ".lrc"), new string('é', 1_100_000));
        await fixture.AddMappingAsync("fixture", "ascii", asciiPath);
        await fixture.AddMappingAsync("fixture", "unicode", unicodePath);

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Large", Artist = "Artist" },
            [
                new KeptLyricsIdentity("fixture", "ascii"),
                new KeptLyricsIdentity("fixture", "unicode")
            ]);
        Assert.Null(result);
    }

    [Fact]
    public async Task RejectsInvalidUtf8Sidecar()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var path = await fixture.WriteAudioAsync("kept/Artist/invalid.mp3", null);
        await File.WriteAllBytesAsync(Path.ChangeExtension(path, ".lrc"), [0xC3, 0x28]);
        await fixture.AddMappingAsync("fixture", "invalid", path);

        var result = await fixture.Reader.ReadAsync(
            fixture.Context(userId),
            new Song { Title = "Invalid", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "invalid")]);
        Assert.Null(result);
    }

    [Fact]
    public async Task CancellationUsesTheProtocolToken()
    {
        await using var fixture = await Fixture.CreateAsync();
        var userId = Guid.CreateVersion7();
        await fixture.AddUserAsync(userId);
        var path = await fixture.WriteAudioAsync("kept/Artist/canceled.mp3", "[00:00.000]canceled");
        await fixture.AddMappingAsync("fixture", "canceled", path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Reader.ReadAsync(
            fixture.Context(userId, cancellationToken: cancellation.Token),
            new Song { Title = "Canceled", Artist = "Artist" },
            [new KeptLyricsIdentity("fixture", "canceled")]));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteTestDatabase database,
            string root,
            string downloadRoot,
            string keptRoot,
            TestDbContextFactory factory,
            KeptLyricsReader reader)
        {
            Database = database;
            Root = root;
            DownloadRoot = downloadRoot;
            KeptRoot = keptRoot;
            Factory = factory;
            Reader = reader;
        }

        public SqliteTestDatabase Database { get; }
        public string Root { get; }
        public string DownloadRoot { get; }
        public string KeptRoot { get; }
        public TestDbContextFactory Factory { get; }
        public KeptLyricsReader Reader { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var database = await SqliteTestDatabase.CreateAsync();
            var baseRoot = Path.Combine(
                ResolveTempRoot(),
                "allstarr-kept-lyrics",
                Guid.CreateVersion7().ToString("N"));
            var downloadRoot = Path.Combine(baseRoot, "downloads");
            var keptRoot = Path.Combine(downloadRoot, "kept");
            Directory.CreateDirectory(keptRoot);
            Directory.CreateDirectory(Path.Combine(downloadRoot, "permanent"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Library:DownloadPath"] = downloadRoot
                })
                .Build();
            var factory = new TestDbContextFactory(database.Options);
            return new Fixture(database, baseRoot, downloadRoot, keptRoot, factory,
                new KeptLyricsReader(factory, configuration));
        }

        public KeptLyricsReader ReaderFor(string downloadRoot)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Library:DownloadPath"] = downloadRoot
                })
                .Build();
            return new KeptLyricsReader(Factory, configuration);
        }

        public ProtocolExecutionContext Context(
            Guid? userId,
            string backendType = "jellyfin",
            bool admin = false,
            CancellationToken cancellationToken = default,
            string backendPrincipal = "listener")
        {
            var principalId = userId.HasValue ? backendPrincipal : "unlinked";
            return new ProtocolExecutionContext(
                ProtocolKind.Jellyfin,
                "primary",
                principalId,
                userId.HasValue
                    ? new AllstarrPrincipal(userId.Value, backendType, "primary", principalId, "Fixture", admin)
                    : null,
                "kept-lyrics",
                DateTimeOffset.UtcNow.AddMinutes(1),
                cancellationToken);
        }

        public async Task AddUserAsync(
            Guid id,
            bool enabled = true,
            string backendType = "jellyfin",
            string backendPrincipal = "listener")
        {
            await using var db = await Factory.CreateDbContextAsync();
            db.Users.Add(new UserRecord
            {
                Id = id,
                BackendType = backendType,
                BackendInstanceId = "primary",
                BackendPrincipalId = backendPrincipal,
                DisplayName = "Fixture",
                Enabled = enabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        public async Task AddAccountAsync(Guid id, string providerId, Guid? owner, bool enabled = true)
        {
            await using var db = await Factory.CreateDbContextAsync();
            db.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = id,
                ProviderId = providerId,
                OwnerUserId = owner,
                DisplayName = providerId,
                Enabled = enabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }

        public async Task AddMappingAsync(
            string providerId,
            string externalId,
            string localPath,
            Guid? accountId = null)
        {
            await using var db = await Factory.CreateDbContextAsync();
            db.DownloadedSongMappings.Add(new DownloadedSongMappingEntity
            {
                Id = Guid.CreateVersion7(),
                ScopeKey = accountId.HasValue
                    ? new DownloadedSongMappingScope(accountId, ProviderAudioQuality.Any).Key
                    : DownloadedSongMappingScope.LegacyKey,
                ProviderAccountId = accountId,
                AudioQuality = ProviderAudioQuality.Any,
                ProviderId = providerId,
                ExternalId = externalId,
                LocalPath = localPath,
                Title = "Mapped title",
                Artist = "Mapped artist",
                Album = "Mapped album",
                DownloadedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }

        public async Task AddManagedFileAsync(
            string path,
            Guid? owner,
            DateTimeOffset? removedAt = null)
        {
            await using var db = await Factory.CreateDbContextAsync();
            db.ManagedFiles.Add(new allstarr.Core.ManagedFiles.ManagedFileOwnershipEntity
            {
                Id = Guid.CreateVersion7(),
                RootId = Guid.CreateVersion7(),
                TargetRootPath = DownloadRoot,
                CanonicalPath = Path.GetFullPath(path),
                ContentSha256 = new string('0', 64),
                Length = 5,
                PlacementMethod = allstarr.Core.ManagedFiles.ManagedFilePlacementMethod.Copy,
                OwnerUserId = owner,
                ScopeKey = owner.HasValue
                    ? $"managed:{owner.Value.ToString("N")}:fixture"
                    : "managed:shared:fixture",
                ReferenceCount = 0,
                IsManaged = true,
                CreatedAt = DateTimeOffset.UtcNow,
                RemovedAt = removedAt,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }

        public async Task<string> WriteAudioAsync(string relativePath, string? lyrics)
        {
            var path = Path.Combine(DownloadRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "audio");
            if (lyrics != null)
                await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), lyrics);
            return path;
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static string ResolveTempRoot()
        {
            return Resolve(new DirectoryInfo(Path.GetTempPath()));
            static string Resolve(DirectoryInfo directory) =>
                directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
                (directory.Parent == null ? directory.FullName : Path.Combine(Resolve(directory.Parent), directory.Name));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
