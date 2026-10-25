namespace allstarr.Core.Storage;

public sealed record BackupPolicy(bool Enabled, string TimeUtc, string Directory, int DailyRetentionCount,
    int WeeklyRetentionCount, int ManualRetentionCount, string Revision)
{
    public string TimeZone => "UTC";
}
public sealed record BackupConfiguration(bool Enabled, string TimeUtc, string Directory, int DailyRetentionCount,
    int WeeklyRetentionCount, int ManualRetentionCount, string ExpectedRevision);
public sealed record BackupFailure(string Code, string Message, DateTimeOffset At);
public sealed record BackupStatus(string State, string DirectoryState, Guid? ActiveJobId,
    DateTimeOffset? LastSuccessAt, DateTimeOffset? NextRunAt, long? LastBytes, BackupFailure? LastFailure);
public sealed record BackupConfigurationResult(BackupPolicy BackupPolicy, BackupStatus BackupStatus);
public sealed record BackupQueued(Guid JobId, bool Created, string Message = "Backup queued.");
public sealed record BackupJobPayload(Guid ArtifactId);
internal sealed record BackupRootProof(string Directory, string StorageBinding, string DeviceId, string FileId);
internal sealed record BackupScheduleTemplate(int Version, string ConfigurationRevision, BackupRootProof? Root = null);
public sealed class BackupConfigurationConflictException() : InvalidOperationException("Backup settings changed. Refresh and try again.");
public sealed class BackupRunningException(Guid jobId) : InvalidOperationException("A backup is already queued or running. Refresh to view its progress.")
{ public Guid JobId => jobId; }
internal sealed class BackupDirectoryException(bool changed = false) : IOException("The backup directory is unavailable or has changed.")
{ public bool Changed => changed; }
internal sealed class BackupUnclaimedException() : IOException("An unclaimed archive was preserved. Create a new backup or inspect the backup folder.");
