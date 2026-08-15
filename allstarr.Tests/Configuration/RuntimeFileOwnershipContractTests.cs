using System.Text.RegularExpressions;

namespace allstarr.Tests;

public sealed class RuntimeFileOwnershipContractTests
{
    private static readonly string[] WriterMarkers =
    [
        "File.WriteAll", "IOFile.WriteAll", "File.AppendAll", "IOFile.AppendAll",
        "File.Create(", "IOFile.Create(", "File.Copy(", "IOFile.Copy(",
        "File.Move(", "IOFile.Move(", "ExtractToFile(", "FileMode.Create",
        "FileMode.Append", "new StreamWriter("
    ];

    private static readonly IReadOnlyDictionary<string, string> AllowedWriterOwners =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Controllers/AppleMusicController.cs"] = "managed audio",
            ["Controllers/DownloadsController.cs"] = "managed audio and lyrics sidecars",
            ["Core/Downloads/ProviderDownloadArtifactResolver.cs"] = "managed audio",
            ["Core/Enrichment/TagLibManagedMetadataWriter.cs"] = "managed audio and artwork",
            ["Core/Extensions/ExtensionSdkV1.cs"] = "extension packages",
            ["Core/Extensions/ExtensionSignedSessionClient.cs"] = "protected extension session",
            ["Core/Intelligence/ListeningHistoryImportPersistence.cs"] = "bounded listening-history upload",
            ["Core/ManagedFiles/FilePlacementService.cs"] = "managed audio and artwork",
            ["Core/ManagedFiles/PhysicalManagedFileOperations.cs"] = "managed audio and artwork",
            ["Core/Operations/PlatformReadinessService.cs"] = "temporary readiness probe",
            ["Core/Storage/DurableBackupService.cs"] = "backup archive",
            ["Core/Storage/DurableStateTransferService.cs"] = "transfer archive",
            ["Core/Storage/SelectiveStateTransferService.cs"] = "transfer archive",
            ["Services/AppleMusic/AppleMusicDownloadService.cs"] = "managed audio",
            ["Services/Common/BaseDownloadService.cs"] = "managed audio and artwork",
            ["Services/Common/ExtensionManager.cs"] = "extension packages",
            ["Services/Common/FileMediaApplicationCache.cs"] = "bounded media cache",
            ["Services/Common/ManagedTrackCacheService.cs"] = "managed audio cache",
            ["Services/Deezer/DeezerDownloadService.cs"] = "managed audio",
            ["Services/Lyrics/KeptLyricsSidecarService.cs"] = "lyrics sidecar",
            ["Services/Qobuz/QobuzDownloadService.cs"] = "managed audio",
            ["Services/Subsonic/PlaylistSyncService.cs"] = "M3U target artifact"
        };

    [Fact]
    public void RuntimeFileWriters_AreLimitedToExplicitArtifactOwners()
    {
        var productionRoot = Path.Combine(FindRepositoryRoot(), "allstarr");
        var writers = Directory.GetFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return WriterMarkers.Any(source.Contains);
            })
            .Select(path => Path.GetRelativePath(productionRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(writers.Except(AllowedWriterOwners.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void RuntimeJsonWriters_AreOnlyCachePackageOrTransferArtifacts()
    {
        var productionRoot = Path.Combine(FindRepositoryRoot(), "allstarr");
        var jsonWriters = Directory.GetFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return Regex.IsMatch(
                    source,
                    @"(?:JsonSerializer\.Serialize(?:Async)?\b[\s\S]{0,600}(?:File\.WriteAll|File\.Move|FileMode\.Create)|(?:File\.WriteAll|FileMode\.Create)[\s\S]{0,600}JsonSerializer\.Serialize(?:Async)?\b)");
            })
            .Select(path => Path.GetRelativePath(productionRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        var allowed = new[]
        {
            "Core/Extensions/ExtensionSdkV1.cs",
            "Core/Extensions/ExtensionSignedSessionClient.cs",
            "Core/Enrichment/TagLibManagedMetadataWriter.cs",
            "Core/ManagedFiles/FilePlacementService.cs",
            "Core/Storage/DurableBackupService.cs",
            "Core/Storage/DurableStateTransferService.cs",
            "Core/Storage/SelectiveStateTransferService.cs",
            "Services/Common/ExtensionManager.cs",
            "Services/Common/FileMediaApplicationCache.cs"
        };

        Assert.Empty(jsonWriters.Except(allowed, StringComparer.Ordinal));
    }

    [Fact]
    public void RemovedLegacyMatcher_CannotBeRegistered()
    {
        var productionRoot = Path.Combine(FindRepositoryRoot(), "allstarr");
        var source = string.Join('\n', Directory.GetFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(File.ReadAllText));

        foreach (var removed in new[]
                 {
                     "SpotifyPlaylistMatchingAdapter", "PlaylistMatchingCoordinator",
                     "LegacyPlaylistMatchRecovery", "IPlaylistMatchingCoordinator"
                 })
            Assert.DoesNotContain(removed, source, StringComparison.Ordinal);
    }

    [Fact]
    public void StateOwnershipMatrix_ExplicitlyAllowsNonBusinessArtifacts()
    {
        var architecture = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "docs", "architecture", "overview.md"));
        foreach (var allowed in new[]
                 {
                     "Managed audio and artwork", "target playlist files",
                     "kept lyrics sidecars", "installed extension package payloads",
                     "encryption key ring", "verified backup artifacts",
                     "bounded temporary transfer archives",
                     "Rebuildable media cache with bounded size/TTL",
                     "atomic staging files", "password-file location"
                 })
            Assert.Contains(allowed, architecture, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate allstarr.sln");
    }
}
