using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class KeptLyricsReaderTests
{
    [Theory]
    [InlineData("shared", true)]
    [InlineData("personal", true)]
    [InlineData("foreign", false)]
    [InlineData("changed-revision", false)]
    [InlineData("missing-proof", false)]
    public async Task RetainedOriginal_LyricsRequireTheExactAuthorizedRepresentationAndSourceRevision(string accountScope, bool expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var listener = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        await fixture.AddUserAsync(listener);
        await fixture.AddUserAsync(foreign, backendPrincipal: "foreign");
        await fixture.AddAccountAsync(account, "fixture", accountScope switch
        {
            "personal" => listener,
            "foreign" => foreign,
            _ => null
        });
        var path = await fixture.WriteAudioAsync("kept/original.mp3", "[00:01.000]Original words");
        await fixture.AddMappingAsync("fixture", "original", path, account);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var mapping = await db.DownloadedSongMappings.SingleAsync();
            mapping.ScopeKey = new DownloadedSongMappingScope(account, ProviderAudioQuality.Any, DownloadedSongRepresentation.Original, 1).Key;
            mapping.CacheIdentityJson = accountScope == "missing-proof" ? null : JsonSerializer.Serialize(new
            {
                Device = "fixture-device",
                File = "fixture-file",
                Length = new FileInfo(path).Length,
                Sha256 = new string('a', 64),
                LastUsed = DateTimeOffset.UtcNow,
                Representation = "Original",
                SourceAccountRevision = 1
            });
            if (accountScope == "changed-revision") (await db.ProviderAccounts.SingleAsync()).Revision = 2;
            await db.SaveChangesAsync();
        }
        var result = await fixture.Reader.ReadAsync(fixture.Context(listener),
            new Song { Title = "Original", Artist = "Artist" }, [new("fixture", "original")]);
        Assert.Equal(expected, result != null);
        if (expected) Assert.Equal("[00:01.000]Original words", result!.SyncedLyrics);
        Assert.Equal("[00:01.000]Original words", await File.ReadAllTextAsync(Path.ChangeExtension(path, ".lrc")));
    }
}
