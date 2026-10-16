namespace allstarr.Core.ManagedFiles;

public sealed class ManagedStorageIdentityRecord
{
    public int Id { get; set; } = 1;
    public string MarkerDeviceId { get; set; } = "";
    public string MarkerFileId { get; set; } = "";
    public Guid Generation { get; set; }
    public Guid? VerificationJobId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
