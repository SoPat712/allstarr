using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class AdminOidcLinkRecord
{
    public const string SecretPurpose = "admin-oidc-backend";
    public required string Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SecretReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed partial class AllstarrDbContext
{
    public DbSet<AdminOidcLinkRecord> AdminOidcLinks => Set<AdminOidcLinkRecord>();

    private static void ConfigureAdminOidcLinks(ModelBuilder builder)
    {
        builder.Entity<AdminOidcLinkRecord>(entity =>
        {
            entity.ToTable("admin_oidc_links");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(64);
            entity.HasIndex(item => item.UserId).IsUnique();
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SecretReferenceRecord>().WithMany().HasForeignKey(item => item.SecretReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
