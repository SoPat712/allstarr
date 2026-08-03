using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260802223000_EnrichListeningOccurrenceMetadata")]
public sealed class EnrichListeningOccurrenceMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AlbumArtist", "listening_events", "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<bool>("ChosenByUser", "listening_events", "boolean", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>("RecordingMusicBrainzId", "listening_events", "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<int>("TrackNumber", "listening_events", "integer", nullable: true);
        migrationBuilder.AddCheckConstraint("CK_listening_event_track_number", "listening_events", "\"TrackNumber\" IS NULL OR \"TrackNumber\" > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_listening_event_track_number", "listening_events");
        migrationBuilder.DropColumn("AlbumArtist", "listening_events");
        migrationBuilder.DropColumn("ChosenByUser", "listening_events");
        migrationBuilder.DropColumn("RecordingMusicBrainzId", "listening_events");
        migrationBuilder.DropColumn("TrackNumber", "listening_events");
    }
}
