using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public enum ListeningHistoryImportState
{
    Previewed,
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed,
    Expired
}

public sealed class ListeningHistoryImportRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Protocol { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public string LibraryScopeId { get; set; } = "";
    public string DisplayFileName { get; set; } = "";
    public string Format { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string PreviewJson { get; set; } = "{}";
    public string PreviewRevision { get; set; } = "";
    public ListeningHistoryImportState State { get; set; }
    public Guid? JobId { get; set; }
    public int ApplyGeneration { get; set; }
    public long NextSequence { get; set; }
    public long ImportedRows { get; set; }
    public long DuplicateRows { get; set; }
    public long ResolvedRows { get; set; }
    public long UnresolvedRows { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; }

    public void ExpireWithoutArtifact()
    {
        if (State is not (ListeningHistoryImportState.Previewed or ListeningHistoryImportState.Pending or ListeningHistoryImportState.Running)) return;
        State = ListeningHistoryImportState.Expired;
        JobId = null;
        Revision++;
    }
}

public sealed record ListeningHistoryImportPreview(
    string Format,
    long FileRows,
    long MusicRows,
    long Completed,
    long Partial,
    long Skipped,
    long Episodes,
    long NonTrack,
    long Malformed,
    long DuplicateInFile,
    long DuplicateExisting,
    long NewRows,
    long ResolvedNewRows,
    long UnresolvedNewRows,
    long RowsWithoutProviderIdentity,
    int SourceUserCount,
    int EstimatedMusicBrainzLookups,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest,
    IReadOnlyDictionary<string, long> ReasonCounts);

public sealed record ListeningHistoryImportPreviewResult(
    Guid ImportId,
    string Revision,
    string DisplayFileName,
    long SizeBytes,
    DateTimeOffset ExpiresAt,
    ListeningHistoryImportState State,
    Guid? JobId,
    long ImportedRows,
    long DuplicateRows,
    long ResolvedRows,
    long UnresolvedRows,
    ListeningHistoryImportPreview Preview);

public sealed class ListeningHistoryImportOptions
{
    public const string SectionName = "Intelligence:HistoryImport";
    public string RootPath { get; set; } = "/app/cache/listening-history-imports";
    public long MaximumUploadBytes { get; set; } = 64L * 1024 * 1024;
    public int MaximumRows { get; set; } = 1_000_000;
    public int PreviewLifetimeHours { get; set; } = 24;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || MaximumUploadBytes is < 1 or > 1024L * 1024 * 1024 ||
            MaximumRows is < 1 or > 10_000_000 || PreviewLifetimeHours is < 1 or > 168)
            throw new InvalidOperationException("Listening-history import limits are invalid.");
    }
}

public sealed record ListeningHistoryImportArtifact(string ContentSha256, long SizeBytes);

public sealed class ListeningHistoryImportArtifactStore(ListeningHistoryImportOptions options)
{
    public async Task<ListeningHistoryImportArtifact> StageAsync(
        Guid importId,
        Stream source,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (importId == Guid.Empty || expectedBytes is < 1 || expectedBytes > options.MaximumUploadBytes || !source.CanRead)
            throw new ArgumentException("The listening-history upload is invalid.");
        var root = Root();
        var destination = PathFor(root, importId);
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long written = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                {
                    if (written > options.MaximumUploadBytes - read)
                        throw new InvalidDataException("The listening-history upload exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    written += read;
                }
                await output.FlushAsync(cancellationToken);
            }
            if (written != expectedBytes)
                throw new InvalidDataException("The listening-history upload length changed during transfer.");
            File.Move(partial, destination, overwrite: false);
            return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), written);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public Stream OpenRead(Guid importId)
    {
        var path = PathFor(Root(), importId);
        if (!File.Exists(path)) throw new FileNotFoundException("The listening-history upload is unavailable.");
        RejectSymlink(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task VerifyAsync(
        Guid importId,
        string expectedSha256,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        await using var source = OpenRead(importId);
        if (source.Length != expectedBytes)
            throw new InvalidDataException("The listening-history upload length no longer matches its preview.");
        var actual = await SHA256.HashDataAsync(source, cancellationToken);
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException exception) { throw new InvalidDataException("The stored upload checksum is invalid.", exception); }
        if (expected.Length != SHA256.HashSizeInBytes || !CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("The listening-history upload changed after preview.");
    }

    public void Delete(Guid importId)
    {
        var path = PathFor(Root(), importId);
        if (!File.Exists(path)) return;
        RejectSymlink(path);
        File.Delete(path);
    }

    private string Root()
    {
        options.Validate();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        Directory.CreateDirectory(root);
        RejectSymlink(root);
        return root;
    }

    private static string PathFor(string root, Guid importId)
    {
        var path = Path.GetFullPath(Path.Combine(root, $"{importId:N}.json"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The listening-history upload escapes its workspace.");
        return path;
    }

    private static void RejectSymlink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Listening-history upload paths may not be symbolic links.");
    }
}

public sealed class ListeningHistoryImportService(
    IDbContextFactory<AllstarrDbContext> factory,
    ListeningHistoryImporterRegistry importers,
    ListeningHistoryImportArtifactStore artifacts,
    ListeningHistoryImportOptions options,
    IPlatformClock clock)
{
    public async Task<ListeningHistoryImportPreviewResult> PreviewAsync(
        IntelligenceScope scope,
        string displayFileName,
        Stream content,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        options.Validate();
        displayFileName = Path.GetFileName(displayFileName).Trim();
        if (displayFileName.Length is < 1 or > 255 || displayFileName.Any(char.IsControl))
            throw new ListeningHistoryImportException("history_import_filename_invalid", "The selected filename is invalid.");
        var importId = Guid.CreateVersion7();
        try
        {
            var artifact = await artifacts.StageAsync(importId, content, sizeBytes, cancellationToken);
            var accumulator = new PreviewAccumulator(factory, scope);
            var scan = await importers.ScanAsync(
                () => artifacts.OpenRead(importId),
                new(clock.UtcNow, options.MaximumRows),
                accumulator.AddAsync,
                cancellationToken);
            await accumulator.FlushAsync(cancellationToken);
            var preview = new ListeningHistoryImportPreview(
                scan.Format,
                scan.Rows,
                scan.MusicRows,
                scan.Completed,
                scan.Partial,
                scan.Skipped,
                scan.Episodes,
                scan.NonTrack,
                scan.Malformed,
                scan.Duplicate,
                accumulator.DuplicateRows,
                accumulator.NewRows,
                accumulator.ResolvedRows,
                accumulator.UnresolvedRows,
                scan.RowsWithoutProviderIdentity,
                scan.SourceUserCount,
                Math.Min(scan.EstimatedMusicBrainzLookups, (int)Math.Min(int.MaxValue, accumulator.UnresolvedRows)),
                scan.Earliest,
                scan.Latest,
                scan.ReasonCounts);
            var previewJson = JsonSerializer.Serialize(preview);
            var revision = Revision(scope, artifact.ContentSha256, previewJson);
            var now = clock.UtcNow;
            var expiresAt = now.AddHours(options.PreviewLifetimeHours);
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            db.ListeningHistoryImports.Add(new()
            {
                Id = importId,
                TenantId = scope.TenantId,
                OwnerUserId = scope.OwnerUserId,
                Protocol = scope.Protocol,
                BackendInstanceId = scope.BackendInstanceId,
                LibraryScopeId = scope.LibraryScopeId,
                DisplayFileName = displayFileName,
                Format = scan.Format,
                ContentSha256 = artifact.ContentSha256,
                SizeBytes = artifact.SizeBytes,
                PreviewJson = previewJson,
                PreviewRevision = revision,
                State = ListeningHistoryImportState.Previewed,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = expiresAt,
                Revision = 1
            });
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = scope.TenantId,
                ActorUserId = scope.OwnerUserId,
                Category = "listening-history-import",
                Action = "previewed",
                Outcome = "success",
                CorrelationId = importId.ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    format = scan.Format,
                    sizeBytes = artifact.SizeBytes,
                    fileRows = scan.Rows,
                    preview.NewRows,
                    duplicateRows = scan.Duplicate + accumulator.DuplicateRows,
                    preview.ResolvedNewRows,
                    preview.UnresolvedNewRows
                }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
            return new(importId, revision, displayFileName, artifact.SizeBytes, expiresAt,
                ListeningHistoryImportState.Previewed, null, 0, 0, 0, 0, preview);
        }
        catch
        {
            artifacts.Delete(importId);
            throw;
        }
    }

    public async Task<ListeningHistoryImportPreviewResult?> GetAsync(
        IntelligenceScope scope,
        Guid importId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.ListeningHistoryImports.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == importId && item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            item.LibraryScopeId == scope.LibraryScopeId, cancellationToken);
        if (record == null) return null;
        var preview = JsonSerializer.Deserialize<ListeningHistoryImportPreview>(record.PreviewJson)
                      ?? throw new InvalidDataException("The saved listening-history preview is invalid.");
        return new(record.Id, record.PreviewRevision, record.DisplayFileName, record.SizeBytes, record.ExpiresAt,
            record.State, record.JobId, record.ImportedRows, record.DuplicateRows, record.ResolvedRows,
            record.UnresolvedRows, preview);
    }

    internal static string OccurrenceKey(IntelligenceScope scope, ListeningHistoryImportRow row) =>
        Hash($"{scope.TenantId:N}\u001f{scope.OwnerUserId:N}\u001f{scope.Protocol}\u001f{scope.BackendInstanceId}\u001f{scope.LibraryScopeId}\u001fimport\u001fspotify\u001f{row.SourceUserKey}\u001f{row.ListenedAt.ToUnixTimeMilliseconds()}\u001f{row.SourceItemKey}");

    private static string Revision(IntelligenceScope scope, string contentSha256, string previewJson) =>
        Hash($"{scope.TenantId:N}\u001f{scope.OwnerUserId:N}\u001f{scope.Protocol}\u001f{scope.BackendInstanceId}\u001f{scope.LibraryScopeId}\u001f{contentSha256}\u001f{previewJson}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class PreviewAccumulator(
        IDbContextFactory<AllstarrDbContext> factory,
        IntelligenceScope scope)
    {
        private readonly List<(string OccurrenceKey, string? ExternalIdHash)> _rows = new(500);
        public long DuplicateRows { get; private set; }
        public long NewRows { get; private set; }
        public long ResolvedRows { get; private set; }
        public long UnresolvedRows { get; private set; }

        public async ValueTask AddAsync(ListeningHistoryImportRow row, CancellationToken cancellationToken)
        {
            var externalId = row.ProviderTrackReference?["spotify:track:".Length..];
            _rows.Add((OccurrenceKey(scope, row), externalId == null ? null : Hash(externalId)));
            if (_rows.Count == 500) await FlushAsync(cancellationToken);
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_rows.Count == 0) return;
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var occurrenceKeys = _rows.Select(item => item.OccurrenceKey).ToArray();
            var existing = await db.ListeningEvents.AsNoTracking().Where(item =>
                    item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                    occurrenceKeys.Contains(item.OccurrenceKey))
                .Select(item => item.OccurrenceKey).ToHashSetAsync(cancellationToken);
            var newRows = _rows.Where(item => !existing.Contains(item.OccurrenceKey)).ToArray();
            var identityHashes = newRows.Select(item => item.ExternalIdHash).OfType<string>().Distinct().ToArray();
            var resolved = identityHashes.Length == 0
                ? []
                : await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                        item.TenantId == scope.TenantId && item.ProviderId == "spotify" &&
                        item.ResourceKind == ProviderResourceKind.Track && identityHashes.Contains(item.ExternalIdHash))
                    .Select(item => item.ExternalIdHash).ToHashSetAsync(cancellationToken);
            DuplicateRows += existing.Count;
            NewRows += newRows.Length;
            ResolvedRows += newRows.LongCount(item => item.ExternalIdHash != null && resolved.Contains(item.ExternalIdHash));
            UnresolvedRows += newRows.LongCount(item => item.ExternalIdHash == null || !resolved.Contains(item.ExternalIdHash));
            _rows.Clear();
        }
    }
}
