using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

// Synced playlists remember who owns them at the source, so feeds and other people's playlists can stay out of the
// listener's library lists, and each playlist can carry the listener's own choice.
[DbContext(typeof(AllstarrDbContext))]
[Migration("20261109010000_PlaylistKinds")]
public sealed class PlaylistKinds : ListenerActivityIndex
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("SourceOwnerId", "playlist_source_snapshots", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("SourceKind", "playlist_source_snapshots", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<bool>("CountsTowardLibrary", "playlist_links", type: "INTEGER", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("SourceOwnerId", "playlist_source_snapshots");
        migrationBuilder.DropColumn("SourceKind", "playlist_source_snapshots");
        migrationBuilder.DropColumn("CountsTowardLibrary", "playlist_links");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        modelBuilder.Entity("allstarr.Core.Storage.PlaylistLinkRecord", b => b.Property<bool?>("CountsTowardLibrary").HasColumnType("INTEGER"));
        modelBuilder.Entity("allstarr.Core.Storage.PlaylistSourceSnapshotRecord", b =>
        {
            b.Property<string>("SourceKind").HasColumnType("TEXT");
            b.Property<string>("SourceOwnerId").HasColumnType("TEXT");
        });
    }
}
