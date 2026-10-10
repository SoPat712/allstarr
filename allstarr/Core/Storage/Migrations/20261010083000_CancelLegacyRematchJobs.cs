using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CancelLegacyRematchJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE durable_jobs
                SET State = 'Cancelled',
                    CompletedAt = UpdatedAt,
                    CancellationRequestedAt = UpdatedAt,
                    LastErrorCode = 'rematch-unified',
                    LastErrorMessage = 'Playlist and bulk rematch now use one scoped rematch job.',
                    Revision = Revision + 1
                WHERE Type IN ('playlist.rematch', 'track-match.rematch-all')
                  AND State IN ('Pending', 'Running', 'RetryScheduled');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
