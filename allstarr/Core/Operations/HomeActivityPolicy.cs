namespace allstarr.Core.Operations;

internal static class HomeActivityPolicy
{
    // Partial indexes evaluate these predicates on writes, including bulk updates and older producers.
    public const string Jobs = """
        "State" = 'Failed'
        OR ("State" = 'RetryScheduled' AND NOT (coalesce("LastAttemptOutcome", '') = 'deferred' AND "LastErrorCode" IS NULL
            AND lower("Type") IN ('listening-history.resolve', 'recording.tags.refresh', 'recording.musicbrainz.enrich',
                'recording.odesli.enrich', 'listening.musicbrainz.enrich', 'favorite.display-backfill', 'health.cleanup',
                'recording.kept-cleanup', 'mixes.refresh', 'lyrics.refresh')))
        OR ("State" IN ('Running', 'Succeeded', 'Cancelled') AND lower("Type") IN (
            'library.refresh', 'library.promote', 'health.smoke', 'health.alert', 'storage.backup',
            'favorite.process', 'spotify-likes.import', 'favorite.link-union', 'collection.migrate',
            'playlist.materialize', 'playlist.provider-source-update', 'playlist.retain-track',
            'listening-history.import', 'recording.keep', 'downloads.quality-check',
            'extension.session.verify', 'problem-alert.delivery', 'recommendation.generate',
            'storage.verify-files', 'configuration.legacy-media', 'releases.poll', 'library.index'))
        """;

    private const string AuditLabel = """
        lower(replace(replace(replace(replace(CASE WHEN lower(trim("Category")) = 'background-task'
            THEN coalesce(CASE WHEN json_valid("DetailsJson") THEN json_extract("DetailsJson", '$.name') END, 'Background maintenance')
            ELSE "Action" END, '.', ''), '-', ''), '_', ''), ' ', ''))
        """;

    public const string Audits = """
        "Category" != 'playback-start' AND (
            lower("Outcome") LIKE '%fail%' OR lower("Outcome") LIKE '%error%'
            OR lower("Outcome") LIKE '%reject%' OR lower("Outcome") LIKE '%unhealthy%'
            OR lower("Outcome") LIKE '%warn%' OR lower("Outcome") LIKE '%degrad%'
            OR lower("Outcome") LIKE '%conflict%' OR lower("Outcome") LIKE '%retry%'
            OR lower("Outcome") LIKE '%ambiguous%' OR lower("Outcome") LIKE '%partial%'
            OR lower("Outcome") IN ('unresolved', 'blocked', 'unauthorized')
            OR (lower("Outcome") NOT IN ('pending', 'waiting', 'deferred')
                AND "Category" NOT IN ('job-progress', 'job-attempt')
                AND "Action" NOT IN ('track-match.rematch', 'automatic.review', 'recording.odesli.enrich')
        """ + " AND " + AuditLabel + " NOT GLOB '*backgroundtask*'" +
        " AND " + AuditLabel + " NOT GLOB '*runtimesettingssourceuse*'" +
        " AND " + AuditLabel + " NOT GLOB '*recordlisten*'" +
        " AND " + AuditLabel + " NOT GLOB '*recordalisten*'" +
        " AND " + AuditLabel + " NOT GLOB '*playbacksignal*'" +
        " AND " + AuditLabel + " NOT GLOB '*trackidentity*'" +
        " AND " + AuditLabel + " NOT GLOB '*canonicalrecording*'" +
        " AND " + AuditLabel + " NOT GLOB '*historyresolution*'))";

    public const string JobQuery = "SELECT * FROM durable_jobs WHERE (" + Jobs + ")";
    public const string AuditQuery = "SELECT * FROM audit_events WHERE (" + Audits + ")";
}
