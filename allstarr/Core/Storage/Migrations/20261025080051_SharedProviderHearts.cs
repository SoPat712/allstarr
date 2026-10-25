using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SharedProviderHearts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("/* favorite-link-rebuild */ SELECT 1;");
            migrationBuilder.AddColumn<bool>(
                name: "IsRealProviderIntent",
                table: "favorite_events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "SharedLifecycleVersion",
                table: "favorite_events",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SharedLinkId",
                table: "favorite_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provider_favorite_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PairKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConfirmedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfirmationVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UnionJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UnionAttempt = table.Column<long>(type: "INTEGER", nullable: false),
                    UnionAdministratorFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    CursorAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CursorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProcessedRows = table.Column<long>(type: "INTEGER", nullable: false),
                    SeededItems = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_favorite_links", x => x.Id);
                    table.CheckConstraint("CK_favorite_link_progress", "length(\"PairKey\")=64 AND length(\"UnionAdministratorFingerprint\")=64 AND \"ConfirmationVersion\"=1 AND \"UnionAttempt\">0 AND \"Phase\" BETWEEN 0 AND 2 AND \"ProcessedRows\">=0 AND \"SeededItems\">=0 AND \"Revision\">0 AND ((\"CursorAt\" IS NULL)=(\"CursorId\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_provider_favorite_links_durable_jobs_UnionJobId",
                        column: x => x.UnionJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_favorite_links_users_ConfirmedByUserId",
                        column: x => x.ConfirmedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "linked_provider_favorite_states",
                columns: table => new
                {
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "BINARY"),
                    ResourceKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, collation: "BINARY"),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, collation: "BINARY"),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LifecycleVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastIntentEventId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_linked_provider_favorite_states", x => new { x.LinkId, x.ProviderId, x.ResourceKind, x.ExternalId });
                    table.CheckConstraint("CK_linked_provider_favorite_state", "length(\"ProviderId\") BETWEEN 1 AND 100 AND \"ResourceKind\" IN ('track','album','artist','playlist') AND length(\"ExternalId\") BETWEEN 1 AND 500 AND \"LifecycleVersion\">=0 AND \"Revision\">0");
                    table.ForeignKey(
                        name: "FK_linked_provider_favorite_states_favorite_events_LastIntentEventId",
                        column: x => x.LastIntentEventId,
                        principalTable: "favorite_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_linked_provider_favorite_states_provider_favorite_links_LinkId",
                        column: x => x.LinkId,
                        principalTable: "provider_favorite_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_favorite_link_members",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BackendPrincipalId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    IdentityFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConfirmedDisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UpperAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpperId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_favorite_link_members", x => x.UserId);
                    table.UniqueConstraint("AK_provider_favorite_link_members_UserId_LinkId", x => new { x.UserId, x.LinkId });
                    table.CheckConstraint("CK_favorite_link_member", "\"Protocol\" IN ('jellyfin','subsonic') AND length(\"BackendInstanceId\") BETWEEN 1 AND 200 AND length(\"BackendPrincipalId\") BETWEEN 1 AND 300 AND length(\"IdentityFingerprint\")=64 AND length(\"ConfirmedDisplayName\") BETWEEN 1 AND 200 AND ((\"UpperAt\" IS NULL)=(\"UpperId\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_provider_favorite_link_members_provider_favorite_links_LinkId",
                        column: x => x.LinkId,
                        principalTable: "provider_favorite_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_favorite_link_members_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_favorite_link_requests",
                columns: table => new
                {
                    AdministratorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Created = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_favorite_link_requests", x => new { x.AdministratorUserId, x.RequestId });
                    table.CheckConstraint("CK_favorite_link_request", "\"Operation\" IN ('create','retry') AND length(\"RequestHash\")=64");
                    table.ForeignKey(
                        name: "FK_provider_favorite_link_requests_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_favorite_link_requests_provider_favorite_links_LinkId",
                        column: x => x.LinkId,
                        principalTable: "provider_favorite_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_favorite_link_requests_users_AdministratorUserId",
                        column: x => x.AdministratorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_event_link_baseline",
                table: "favorite_events",
                columns: new[] { "OwnerUserId", "Protocol", "BackendInstanceId", "BackendPrincipalId", "SharedLinkId", "IsRealProviderIntent", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_events_OwnerUserId_SharedLinkId",
                table: "favorite_events",
                columns: new[] { "OwnerUserId", "SharedLinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_event_link_item",
                table: "favorite_events",
                columns: new[] { "OwnerUserId", "Protocol", "BackendInstanceId", "BackendPrincipalId", "ItemId", "CreatedAt", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_favorite_event_shared_scope",
                table: "favorite_events",
                sql: "((\"SharedLinkId\" IS NULL AND \"SharedLifecycleVersion\" IS NULL) OR (\"SharedLinkId\" IS NOT NULL AND \"SharedLifecycleVersion\" IS NOT NULL AND \"SharedLifecycleVersion\">=0 AND \"IsRealProviderIntent\"=1))");

            migrationBuilder.CreateIndex(
                name: "IX_linked_provider_favorite_states_LastIntentEventId",
                table: "linked_provider_favorite_states",
                column: "LastIntentEventId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_favorite_link_member_role",
                table: "provider_favorite_link_members",
                columns: new[] { "LinkId", "Protocol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_favorite_link_requests_JobId",
                table: "provider_favorite_link_requests",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_favorite_link_requests_LinkId",
                table: "provider_favorite_link_requests",
                column: "LinkId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_favorite_link_created",
                table: "provider_favorite_links",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_favorite_link_pair",
                table: "provider_favorite_links",
                column: "PairKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_favorite_links_ConfirmedByUserId",
                table: "provider_favorite_links",
                column: "ConfirmedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_favorite_links_UnionJobId",
                table: "provider_favorite_links",
                column: "UnionJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_favorite_events_provider_favorite_link_members_OwnerUserId_SharedLinkId",
                table: "favorite_events",
                columns: new[] { "OwnerUserId", "SharedLinkId" },
                principalTable: "provider_favorite_link_members",
                principalColumns: new[] { "UserId", "LinkId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                /* favorite-link-legacy-intents */
                UPDATE favorite_events AS e SET "IsRealProviderIntent"=1
                WHERE allstarr_valid_favorite_resource(e."ItemId") AND EXISTS (
                  SELECT 1 FROM favorite_states AS s
                  JOIN users AS u ON u."Id"=s."OwnerUserId"
                  JOIN durable_jobs AS j ON j."Id"=e."JobId" AND j."OwnerUserId"=e."OwnerUserId" AND j."Type"='favorite.process'
                  WHERE s."LastEventId"=e."Id" AND s."OwnerUserId"=e."OwnerUserId"
                    AND s."Protocol"=e."Protocol" AND s."BackendInstanceId"=e."BackendInstanceId" AND s."ItemId"=e."ItemId"
                    AND u."BackendType"=e."Protocol" AND u."BackendInstanceId"=e."BackendInstanceId"
                    AND u."BackendPrincipalId"=e."BackendPrincipalId" AND u."IsSynthetic"=0
                    AND ((s."IsFavorite"=1 AND e."Operation"='Favorite') OR (s."IsFavorite"=0 AND e."Operation"='Unfavorite')));
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_favorite_event_scope_immutable BEFORE UPDATE OF "IsRealProviderIntent","SharedLinkId","SharedLifecycleVersion" ON favorite_events
                WHEN NEW."IsRealProviderIntent" IS NOT OLD."IsRealProviderIntent" OR NEW."SharedLinkId" IS NOT OLD."SharedLinkId" OR NEW."SharedLifecycleVersion" IS NOT OLD."SharedLifecycleVersion"
                BEGIN SELECT RAISE(ABORT,'CK_favorite_event_scope_immutable'); END;
                CREATE TRIGGER TR_provider_favorite_link_member_immutable BEFORE UPDATE ON provider_favorite_link_members
                BEGIN SELECT RAISE(ABORT,'CK_provider_favorite_link_member_immutable'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                /* favorite-link-rebuild */
                CREATE TEMP TABLE favorite_link_downgrade_guard (Allowed INTEGER NOT NULL CONSTRAINT CK_provider_favorite_links_require_restore CHECK(Allowed=1));
                INSERT INTO favorite_link_downgrade_guard SELECT NOT (
                  EXISTS(SELECT 1 FROM provider_favorite_links) OR EXISTS(SELECT 1 FROM provider_favorite_link_members) OR
                  EXISTS(SELECT 1 FROM linked_provider_favorite_states) OR EXISTS(SELECT 1 FROM provider_favorite_link_requests) OR
                  EXISTS(SELECT 1 FROM favorite_events WHERE "SharedLinkId" IS NOT NULL));
                DROP TABLE favorite_link_downgrade_guard;
                DROP TRIGGER TR_favorite_event_scope_immutable;
                DROP TRIGGER TR_provider_favorite_link_member_immutable;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_favorite_events_provider_favorite_link_members_OwnerUserId_SharedLinkId",
                table: "favorite_events");

            migrationBuilder.DropTable(
                name: "linked_provider_favorite_states");

            migrationBuilder.DropTable(
                name: "provider_favorite_link_members");

            migrationBuilder.DropTable(
                name: "provider_favorite_link_requests");

            migrationBuilder.DropTable(
                name: "provider_favorite_links");

            migrationBuilder.DropIndex(
                name: "IX_favorite_event_link_baseline",
                table: "favorite_events");

            migrationBuilder.DropIndex(
                name: "IX_favorite_events_OwnerUserId_SharedLinkId",
                table: "favorite_events");

            migrationBuilder.DropIndex(
                name: "IX_favorite_event_link_item",
                table: "favorite_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_favorite_event_shared_scope",
                table: "favorite_events");

            migrationBuilder.DropColumn(
                name: "IsRealProviderIntent",
                table: "favorite_events");

            migrationBuilder.DropColumn(
                name: "SharedLifecycleVersion",
                table: "favorite_events");

            migrationBuilder.DropColumn(
                name: "SharedLinkId",
                table: "favorite_events");
        }
    }
}
