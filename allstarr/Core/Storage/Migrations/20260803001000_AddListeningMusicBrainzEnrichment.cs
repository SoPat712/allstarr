using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260803001000_AddListeningMusicBrainzEnrichment")]
public sealed class AddListeningMusicBrainzEnrichment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Isrc", "listening_events", "character varying(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<double>("MusicBrainzEnrichmentConfidence", "listening_events", "double precision", nullable: true);
        migrationBuilder.AddColumn<long>("MusicBrainzEnrichedAt", "listening_events", "bigint", nullable: true);
        migrationBuilder.AddColumn<string>("MusicBrainzEnrichmentState", "listening_events", "character varying(32)", maxLength: 32, nullable: false, defaultValue: "NotRequested");
        migrationBuilder.AddColumn<string>("MusicBrainzFactsJson", "listening_events", "text", nullable: true);
        migrationBuilder.AddColumn<string>("MusicBrainzSourceRevision", "listening_events", "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddCheckConstraint("CK_listening_event_musicbrainz_confidence", "listening_events",
            "\"MusicBrainzEnrichmentConfidence\" IS NULL OR (\"MusicBrainzEnrichmentConfidence\" >= 0 AND \"MusicBrainzEnrichmentConfidence\" <= 1)");
        migrationBuilder.AddCheckConstraint("CK_listening_event_musicbrainz_state", "listening_events",
            "\"MusicBrainzEnrichmentState\" IN ('NotRequested', 'Pending', 'Resolved', 'Unresolved', 'Failed')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_listening_event_musicbrainz_confidence", "listening_events");
        migrationBuilder.DropCheckConstraint("CK_listening_event_musicbrainz_state", "listening_events");
        migrationBuilder.DropColumn("Isrc", "listening_events");
        migrationBuilder.DropColumn("MusicBrainzEnrichmentConfidence", "listening_events");
        migrationBuilder.DropColumn("MusicBrainzEnrichedAt", "listening_events");
        migrationBuilder.DropColumn("MusicBrainzEnrichmentState", "listening_events");
        migrationBuilder.DropColumn("MusicBrainzFactsJson", "listening_events");
        migrationBuilder.DropColumn("MusicBrainzSourceRevision", "listening_events");
    }
}
