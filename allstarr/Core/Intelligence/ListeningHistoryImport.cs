namespace allstarr.Core.Intelligence;

public enum ListeningHistoryImportClassification
{
    Completed,
    Partial,
    Skipped
}

public sealed record ListeningHistoryImportRow(
    long Sequence,
    string SourceUserKey,
    string SourceItemKey,
    DateTimeOffset StartedAt,
    DateTimeOffset ListenedAt,
    long MillisecondsPlayed,
    string Title,
    string Artist,
    string? Album,
    string? ProviderTrackReference,
    string? Client,
    string? ReasonStart,
    string? ReasonEnd,
    bool Offline,
    DateTimeOffset? OfflineAt,
    bool PrivateSession,
    ListeningHistoryImportClassification Classification,
    string ReasonCode);

public sealed record ListeningHistoryImportScan(
    string Format,
    long Rows,
    long MusicRows,
    long Completed,
    long Partial,
    long Skipped,
    long Episodes,
    long NonTrack,
    long Malformed,
    long Duplicate,
    long RowsWithoutProviderIdentity,
    int SourceUserCount,
    int EstimatedMusicBrainzLookups,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest,
    IReadOnlyDictionary<string, long> ReasonCounts);

public sealed record ListeningHistoryImportScanContext(
    DateTimeOffset Now,
    int MaximumRows = 1_000_000);

public interface IListeningHistoryImporter
{
    string Format { get; }

    Task<ListeningHistoryImportScan?> ScanAsync(
        Stream source,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow = null,
        CancellationToken cancellationToken = default);
}

public sealed class ListeningHistoryImporterRegistry(IEnumerable<IListeningHistoryImporter> importers)
{
    private readonly IListeningHistoryImporter[] _importers = importers.ToArray();

    public async Task<ListeningHistoryImportScan> ScanAsync(
        Func<Stream> openSource,
        ListeningHistoryImportScanContext context,
        Func<ListeningHistoryImportRow, CancellationToken, ValueTask>? onRow = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var importer in _importers)
        {
            await using var source = openSource();
            if (!source.CanRead) throw new ArgumentException("The history import source must be readable.", nameof(openSource));
            var scan = await importer.ScanAsync(source, context, onRow, cancellationToken);
            if (scan != null) return scan;
        }
        throw new ListeningHistoryImportException(
            "history_import_format_unsupported",
            "The file does not match a supported listening-history format.");
    }
}

public sealed class ListeningHistoryImportException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class ListeningHistoryImportRegistration
{
    public static IServiceCollection AddListeningHistoryImport(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ListeningHistoryImportOptions.SectionName)
                          .Get<ListeningHistoryImportOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IListeningHistoryImporter, SpotifyListeningHistoryImporter>();
        services.AddSingleton<ListeningHistoryImporterRegistry>();
        services.AddSingleton<ListeningHistoryImportArtifactStore>();
        services.AddSingleton<ListeningHistoryImportService>();
        return services;
    }
}
