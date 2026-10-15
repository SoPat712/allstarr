using allstarr.Core.Configuration;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class LegacyMediaInventoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-legacy-media", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider services;

    public LegacyMediaInventoryTests()
    {
        Directory.CreateDirectory(root);
        services = new ServiceCollection().AddDbContext<AllstarrDbContext>(options => options.UseSqlite("Data Source=:memory:"))
            .AddManagedFilePlacement().BuildServiceProvider();
    }

    [Fact]
    public void Preview_ReadsOnlyInventoryAndPreservesOriginalFiles()
    {
        var file = Create("kept/Artist/Album/01 - Song [deezer-123].flac");
        var modified = File.GetLastWriteTimeUtc(file);
        var plan = Inventory().Preview(Document("STORAGE_MODE=Permanent"));
        var candidate = Assert.Single(plan.Files);
        Assert.Equal(file, candidate.Path);
        Assert.Equal("deezer", candidate.ProviderId);
        Assert.Equal("123", candidate.ExternalId);
        Assert.Equal("Song", candidate.Metadata.Title);
        Assert.Equal(["Artist"], candidate.Metadata.Artists);
        Assert.True(candidate.Kept);
        Assert.NotEmpty(candidate.FileId);
        Assert.Equal("original fixture bytes", File.ReadAllText(file));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void Preview_NestedKeptWinsAndUnknownAudioIsNotAssignedAnInventedIdentity()
    {
        Create("downloads/kept/Unknown.flac");
        Create("downloads/Artist/Album/Song [qobuz-456].mp3");
        Create("downloads/cache/temporary.flac");
        Create("downloads/Artist/Album/Song.lrc");
        var plan = Inventory().Preview(Document("STORAGE_MODE=Permanent"));
        Assert.Equal(2, plan.Files.Count);
        Assert.Null(plan.Files.Single(item => item.Kept).ProviderId);
        Assert.Equal("qobuz", plan.Files.Single(item => !item.Kept).ProviderId);
    }

    [Fact]
    public void Preview_NeverFollowsLinksOrOutsideRootOverrides()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.flac"), "outside bytes");
        Directory.CreateDirectory(Path.Combine(root, "kept"));
        Directory.CreateSymbolicLink(Path.Combine(root, "kept", "escape"), outside);
        var plan = Inventory().Preview(Document("LIBRARY_KEPT_PATH=" + outside));
        Assert.Empty(plan.Files);
        Assert.Contains(plan.Notices, notice => notice.Contains("outside", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notices, notice => notice.Contains("links", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Adoption_RejectsAReplacementAndNeverCopiesOrChangesOriginalBytes()
    {
        var path = Create("kept/Artist/Album/Song [deezer-123].flac");
        var plan = Inventory().Preview(Document("STORAGE_MODE=Permanent"));
        var snapshot = Assert.Single(plan.Files);
        using var scope = services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        var prepared = await files.PrepareLegacyAdoptionAsync(snapshot, DateTimeOffset.UtcNow, default);
        Assert.Equal(ManagedFilePlacementMethod.Adopted, prepared.PlacementMethod);
        Assert.Equal(path, prepared.CanonicalPath);
        Assert.Equal("original fixture bytes", File.ReadAllText(path));
        File.Move(path, path + ".original");
        File.WriteAllText(path, "original fixture bytes");
        Assert.Throws<IOException>(() => files.ValidateLegacySnapshot(snapshot));
        Assert.Equal("original fixture bytes", File.ReadAllText(path + ".original"));
    }

    private string Create(string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "original fixture bytes");
        return path;
    }
    private LegacyMediaInventory Inventory() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Legacy:AllowedRoots"] = Path.Combine(root, "kept") + "," + Path.Combine(root, "downloads"),
        ["Legacy:KeptPath"] = Path.Combine(root, "kept"),
        ["Legacy:DownloadPath"] = Path.Combine(root, "downloads")
    }).Build(), services.GetRequiredService<IServiceScopeFactory>());
    private static LegacyEnvDocument Document(string text) => LegacyEnvParser.Parse(System.Text.Encoding.UTF8.GetBytes(text));
    public void Dispose() { services.Dispose(); Directory.Delete(root, true); }
}
