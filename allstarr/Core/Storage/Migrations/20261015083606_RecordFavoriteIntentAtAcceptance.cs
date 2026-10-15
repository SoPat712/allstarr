using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RecordFavoriteIntentAtAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH latest AS (
                    SELECT *, ROW_NUMBER() OVER (
                        PARTITION BY OwnerUserId, Protocol, BackendInstanceId, ItemId
                        ORDER BY CreatedAt DESC, Id DESC) AS position
                    FROM favorite_events
                )
                UPDATE favorite_states AS state
                SET IsFavorite = (event.Operation = 'Favorite'),
                    LastEventId = event.Id,
                    UpdatedAt = event.CreatedAt,
                    Revision = state.Revision + 1
                FROM latest AS event
                WHERE event.position = 1
                    AND state.OwnerUserId = event.OwnerUserId
                    AND state.Protocol = event.Protocol
                    AND state.BackendInstanceId = event.BackendInstanceId
                    AND state.ItemId = event.ItemId
                    AND (state.LastEventId <> event.Id
                        OR state.IsFavorite <> (event.Operation = 'Favorite'));

                WITH latest AS (
                    SELECT *, ROW_NUMBER() OVER (
                        PARTITION BY OwnerUserId, Protocol, BackendInstanceId, ItemId
                        ORDER BY CreatedAt DESC, Id DESC) AS position
                    FROM favorite_events
                )
                INSERT INTO favorite_states
                    (Id, OwnerUserId, Protocol, BackendInstanceId, ItemId,
                     IsFavorite, LastEventId, UpdatedAt, Revision)
                SELECT event.Id, event.OwnerUserId, event.Protocol, event.BackendInstanceId,
                    event.ItemId, (event.Operation = 'Favorite'), event.Id, event.CreatedAt, 1
                FROM latest AS event
                WHERE event.position = 1 AND NOT EXISTS (
                    SELECT 1 FROM favorite_states AS state
                    WHERE state.OwnerUserId = event.OwnerUserId
                        AND state.Protocol = event.Protocol
                        AND state.BackendInstanceId = event.BackendInstanceId
                        AND state.ItemId = event.ItemId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Accepted user intent must survive rollback of this data correction.
        }
    }
}
