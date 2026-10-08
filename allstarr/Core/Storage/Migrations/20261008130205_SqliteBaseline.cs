using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SqliteBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_auth_sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_auth_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "application_cache_entries",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_cache_entries", x => x.Key);
                    table.CheckConstraint("CK_application_cache_payload_bytes", "\"PayloadBytes\" >= 0 AND \"PayloadBytes\" <= 1048576");
                });

            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ArtifactPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ApplicationVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RestoreStatus = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RestoreVerifiedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "downloaded_song_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AudioQuality = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Album = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DownloadedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_downloaded_song_mappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "extension_registries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RegistryUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extension_registries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "manual_lyrics_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdentityHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Album = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    LyricsId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_lyrics_mappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "extension_packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RegistryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PreviousPackageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExtensionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SdkVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PackagePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StagedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ActivatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DisabledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extension_packages", x => x.Id);
                    table.CheckConstraint("CK_extension_packages_content_hash", "length(\"ContentSha256\") = 64");
                    table.CheckConstraint("CK_extension_packages_sha256", "length(\"Sha256\") = 64");
                    table.ForeignKey(
                        name: "FK_extension_packages_extension_packages_PreviousPackageId",
                        column: x => x.PreviousPackageId,
                        principalTable: "extension_packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_extension_packages_extension_registries_RegistryId",
                        column: x => x.RegistryId,
                        principalTable: "extension_registries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "canonical_artists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SortName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Disambiguation = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MusicBrainzArtistId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_artists", x => x.Id);
                    table.UniqueConstraint("AK_canonical_artists_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_canonical_artists_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "canonical_catalog_aliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CanonicalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ExternalIdHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_catalog_aliases", x => x.Id);
                    table.CheckConstraint("CK_canonical_catalog_alias_hash", "length(\"ExternalIdHash\") = 64");
                    table.ForeignKey(
                        name: "FK_canonical_catalog_aliases_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "canonical_release_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PrimaryType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SecondaryTypesJson = table.Column<string>(type: "jsonb", nullable: false),
                    FirstReleaseDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    MusicBrainzReleaseGroupId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_release_groups", x => x.Id);
                    table.UniqueConstraint("AK_canonical_release_groups_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_canonical_release_groups_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_facts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CanonicalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FieldName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ValueJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceRevision = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PayloadSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RefreshAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    SupersededAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_facts", x => x.Id);
                    table.CheckConstraint("CK_catalog_fact_confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
                    table.CheckConstraint("CK_catalog_fact_payload_hash", "length(\"PayloadSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_catalog_facts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AvailableAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeliveredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FailedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outbox_messages_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.UniqueConstraint("AK_users_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_users_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "extension_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionPackageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EventCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extension_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_extension_logs_extension_packages_ExtensionPackageId",
                        column: x => x.ExtensionPackageId,
                        principalTable: "extension_packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "canonical_release_group_artists",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalReleaseGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CanonicalArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreditName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    JoinPhrase = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_release_group_artists", x => new { x.TenantId, x.CanonicalReleaseGroupId, x.Position });
                    table.CheckConstraint("CK_canonical_release_group_artist_position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_canonical_release_group_artists_canonical_artists_TenantId_CanonicalArtistId",
                        columns: x => new { x.TenantId, x.CanonicalArtistId },
                        principalTable: "canonical_artists",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_release_group_artists_canonical_release_groups_TenantId_CanonicalReleaseGroupId",
                        columns: x => new { x.TenantId, x.CanonicalReleaseGroupId },
                        principalTable: "canonical_release_groups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "canonical_releases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalReleaseGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Disambiguation = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    ReleaseDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Barcode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MusicBrainzReleaseId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_releases", x => x.Id);
                    table.UniqueConstraint("AK_canonical_releases_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_canonical_releases_canonical_release_groups_TenantId_CanonicalReleaseGroupId",
                        columns: x => new { x.TenantId, x.CanonicalReleaseGroupId },
                        principalTable: "canonical_release_groups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_canonical_releases_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_events_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_audit_events_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backend_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackendType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backend_identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backend_identities_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backend_identities_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "canonical_recordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Disambiguation = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    IsExplicit = table.Column<bool>(type: "INTEGER", nullable: true),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    Isrc = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    MusicBrainzRecordingId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_recordings", x => x.Id);
                    table.UniqueConstraint("AK_canonical_recordings_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_canonical_recordings_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_recordings_users_TenantId_CreatedByUserId",
                        columns: x => new { x.TenantId, x.CreatedByUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "extension_permission_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionPackageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PermissionValue = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extension_permission_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_extension_permission_review_package",
                        column: x => x.ExtensionPackageId,
                        principalTable: "extension_packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_extension_permission_reviews_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "favorite_action_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AddToVirtualLiked = table.Column<bool>(type: "INTEGER", nullable: true),
                    MatchLocalLibrary = table.Column<bool>(type: "INTEGER", nullable: true),
                    AutoDownload = table.Column<bool>(type: "INTEGER", nullable: true),
                    EnrichMetadata = table.Column<bool>(type: "INTEGER", nullable: true),
                    PlaceManagedFile = table.Column<bool>(type: "INTEGER", nullable: true),
                    RefreshBackendLibrary = table.Column<bool>(type: "INTEGER", nullable: true),
                    TargetCredentialReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorite_action_policies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_favorite_action_policies_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_favorite_action_policies_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_favorite_action_policies_users_TenantId_UpdatedByUserId",
                        columns: x => new { x.TenantId, x.UpdatedByUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intelligence_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowedSignalTypesJson = table.Column<string>(type: "TEXT", nullable: false),
                    EnabledProvidersJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_intelligence_policies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_intelligence_policies_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_intelligence_policies_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    JobType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OverlapPolicy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MisfirePolicy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RetryPolicyJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadTemplateJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    NextRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_schedules", x => x.Id);
                    table.UniqueConstraint("AK_job_schedules_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_JobSchedule_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "listening_history_imports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    DisplayFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviewJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewRevision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApplyGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    NextSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ImportedRows = table.Column<long>(type: "INTEGER", nullable: false),
                    DuplicateRows = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedRows = table.Column<long>(type: "INTEGER", nullable: false),
                    UnresolvedRows = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_history_imports", x => x.Id);
                    table.CheckConstraint("CK_listening_history_import_counts", "\"NextSequence\" >= 0 AND \"ImportedRows\" >= 0 AND \"DuplicateRows\" >= 0 AND \"ResolvedRows\" >= 0 AND \"UnresolvedRows\" >= 0");
                    table.CheckConstraint("CK_listening_history_import_size", "\"SizeBytes\" > 0");
                    table.CheckConstraint("CK_listening_history_import_state", "\"State\" IN ('Previewed', 'Pending', 'Running', 'Completed', 'Cancelled', 'Failed', 'Expired')");
                    table.ForeignKey(
                        name: "FK_listening_history_imports_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_history_imports_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "listening_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    WindowStart = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_listening_profiles_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_profiles_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "onboarding_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CompletedStepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CompletionSource = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReopenedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_onboarding_states_users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "playback_delivery_checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurrenceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SignalKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SafeMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RetryAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    RequiresReauthentication = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playback_delivery_checkpoints", x => x.Id);
                    table.CheckConstraint("CK_playback_delivery_checkpoint_state", "\"State\" IN ('Delivered', 'Ignored', 'Retrying', 'PermanentFailure')");
                    table.ForeignKey(
                        name: "FK_playback_delivery_checkpoints_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_runtime_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ValueType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_runtime_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_runtime_settings_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tenant_runtime_settings_users_TenantId_UpdatedByUserId",
                        columns: x => new { x.TenantId, x.UpdatedByUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "legacy_env_imports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AuditEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProvenanceJson = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_env_imports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_legacy_env_imports_audit_events_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "audit_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_legacy_env_imports_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_legacy_env_imports_users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "secret_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BackendIdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ActiveVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_references", x => x.Id);
                    table.ForeignKey(
                        name: "FK_secret_references_backend_identities_BackendIdentityId",
                        column: x => x.BackendIdentityId,
                        principalTable: "backend_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_secret_references_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "canonical_recording_artists",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CanonicalArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreditName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    JoinPhrase = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_recording_artists", x => new { x.TenantId, x.CanonicalRecordingId, x.Position });
                    table.CheckConstraint("CK_canonical_recording_artist_position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_canonical_recording_artists_canonical_artists_TenantId_CanonicalArtistId",
                        columns: x => new { x.TenantId, x.CanonicalArtistId },
                        principalTable: "canonical_artists",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_recording_artists_canonical_recordings_TenantId_CanonicalRecordingId",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "canonical_release_tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalReleaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediumPosition = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackPosition = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    MusicBrainzTrackId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_release_tracks", x => x.Id);
                    table.UniqueConstraint("AK_canonical_release_tracks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_canonical_release_track_position", "\"MediumPosition\" > 0 AND \"TrackPosition\" > 0");
                    table.ForeignKey(
                        name: "FK_canonical_release_tracks_canonical_recordings_TenantId_CanonicalRecordingId",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_release_tracks_canonical_releases_TenantId_CanonicalReleaseId",
                        columns: x => new { x.TenantId, x.CanonicalReleaseId },
                        principalTable: "canonical_releases",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_canonical_release_tracks_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "library_tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackendIdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BackendItemId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Album = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AlbumArtist = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationProvenance = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DurationRetrievedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Isrc = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    MusicBrainzRecordingId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MusicBrainzReleaseId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MusicBrainzArtistId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProviderIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CoverArtReference = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AcceptedDecisionVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    IndexedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_tracks", x => x.Id);
                    table.UniqueConstraint("AK_library_tracks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_library_tracks_decision_version", "\"AcceptedDecisionVersion\" IS NULL OR \"AcceptedDecisionVersion\" > 0");
                    table.CheckConstraint("CK_library_tracks_duration", "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" > 0");
                    table.CheckConstraint("CK_library_tracks_stable_artwork", "\"CoverArtReference\" IS NULL OR \"CoverArtReference\" NOT LIKE '%://%'");
                    table.ForeignKey(
                        name: "FK_LibraryTrack_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_track_canonical_recording",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_tracks_backend_identities_BackendIdentityId",
                        column: x => x.BackendIdentityId,
                        principalTable: "backend_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "admin_oidc_links",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BackendIdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecretReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_oidc_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_oidc_links_backend_identities_BackendIdentityId",
                        column: x => x.BackendIdentityId,
                        principalTable: "backend_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_admin_oidc_links_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "listening_intake_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SecretReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelayExternally = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_intake_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_listening_intake_token_secret",
                        column: x => x.SecretReferenceId,
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_intake_tokens_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_intake_tokens_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    SecretReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_accounts", x => x.Id);
                    table.UniqueConstraint("AK_provider_accounts_Id_ProviderId", x => new { x.Id, x.ProviderId });
                    table.CheckConstraint("CK_provider_accounts_scope_shape", "(\"Scope\" = 'Global' AND \"TenantId\" IS NULL AND \"OwnerUserId\" IS NULL AND \"LibraryScopeId\" IS NULL) OR (\"Scope\" = 'User' AND \"TenantId\" IS NOT NULL AND \"OwnerUserId\" IS NOT NULL AND \"LibraryScopeId\" IS NULL) OR (\"Scope\" = 'Library' AND \"TenantId\" IS NOT NULL AND \"OwnerUserId\" IS NULL AND \"LibraryScopeId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_provider_account_creator",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_provider_account_tenant_owner",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_accounts_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_accounts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "secret_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecretReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RetiredAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_secret_versions_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "durable_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ProviderCapability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PolicySnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DeferralCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxDeferrals = table.Column<int>(type: "INTEGER", nullable: false),
                    AvailableAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CancellationRequestedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_durable_job_tenant_owner",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_durable_jobs_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_durable_jobs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SourceProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourcePlaylistId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SourcePlaylistIdHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetProtocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetBackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetPlaylistId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProjectionMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Resolved"),
                    MaterializationMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ImportMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Linked"),
                    TrackRetention = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "OnDemand"),
                    MirrorStaleEntries = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreserveManualEntries = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncName = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncDescription = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncArtwork = table.Column<bool>(type: "INTEGER", nullable: false),
                    RuleVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PolicyVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_links", x => x.Id);
                    table.UniqueConstraint("AK_playlist_links_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_links_import_mode", "\"ImportMode\" IN ('Linked', 'OneTime')");
                    table.CheckConstraint("CK_playlist_links_one_time_schedule", "\"ImportMode\" <> 'OneTime' OR \"ScheduleId\" IS NULL");
                    table.CheckConstraint("CK_playlist_links_source_hash", "length(\"SourcePlaylistIdHash\") = 64");
                    table.CheckConstraint("CK_playlist_links_track_retention", "\"TrackRetention\" IN ('OnDemand', 'KeepAll')");
                    table.ForeignKey(
                        name: "FK_PlaylistLink_JobSchedule",
                        columns: x => new { x.TenantId, x.ScheduleId },
                        principalTable: "job_schedules",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistLink_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_link_provider_account",
                        columns: x => new { x.ProviderAccountId, x.SourceProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_circuits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RetryAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_circuits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_circuits_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_health_rollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WindowStart = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessRate = table.Column<double>(type: "REAL", nullable: false),
                    P50LatencyMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    P95LatencyMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    LastState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastFailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_health_rollups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_health_rollups_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_health_samples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LatencyMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_health_samples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_health_samples_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_track_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ResourceKind = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CatalogNamespace = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ExternalIdHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Verification = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    VerificationMethod = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DecisionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_track_identities", x => x.Id);
                    table.CheckConstraint("CK_provider_track_identities_decision_version", "\"DecisionVersion\" > 0");
                    table.CheckConstraint("CK_provider_track_identities_external_hash", "length(\"ExternalIdHash\") = 64");
                    table.CheckConstraint("CK_provider_track_identities_scope_shape", "(\"Scope\" = 'Catalog' AND \"ProviderAccountId\" IS NULL) OR (\"Scope\" = 'Account' AND \"ProviderAccountId\" IS NOT NULL)");
                    table.CheckConstraint("CK_provider_track_identities_track_only", "\"ResourceKind\" = 'Track'");
                    table.CheckConstraint("CK_provider_track_identities_verification", "\"Verification\" IN ('Verified', 'Pinned')");
                    table.ForeignKey(
                        name: "FK_provider_track_identities_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_track_identity_canonical_recording",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_track_identity_provider_account",
                        columns: x => new { x.ProviderAccountId, x.ProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "favorite_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BackendPrincipalId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceRevision = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PolicySnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorite_events", x => x.Id);
                    table.UniqueConstraint("AK_favorite_events_Id_TenantId_OwnerUserId", x => new { x.Id, x.TenantId, x.OwnerUserId });
                    table.ForeignKey(
                        name: "FK_favorite_events_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_favorite_events_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_favorite_events_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_attempts_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "listening_signals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SignalType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TrackKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    TrackReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SignalKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_signals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_listening_signal_job",
                        column: x => x.SourceJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_signals_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_signals_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RootId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetRootPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CanonicalPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    FileSystemDeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FileSystemFileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FileSystemLinkCount = table.Column<uint>(type: "INTEGER", nullable: true),
                    PlacementMethod = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    SourceJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ReferenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsManaged = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_files", x => x.Id);
                    table.UniqueConstraint("AK_managed_files_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_managed_files_owned", "\"IsManaged\" = TRUE");
                    table.CheckConstraint("CK_managed_files_references", "\"ReferenceCount\" >= 0");
                    table.CheckConstraint("CK_managed_files_sha256", "length(\"ContentSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_managed_file_tenant_job",
                        column: x => x.SourceJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_file_tenant_user",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_files_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_download_workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    DurableJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_download_workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_download_workspace_account",
                        columns: x => new { x.ProviderAccountId, x.ProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_download_workspace_job",
                        column: x => x.DurableJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_download_workspace_user",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_download_workspaces_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_route_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DurableJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RouteKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    SelectedProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SelectedProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CandidateDecisionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_route_decisions", x => x.Id);
                    table.UniqueConstraint("AK_provider_route_decisions_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_provider_route_decision_account",
                        columns: x => new { x.SelectedProviderAccountId, x.SelectedProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_decision_actor",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_decision_job",
                        column: x => x.DurableJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_decisions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PolicySnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    SeedTrackKeysJson = table.Column<string>(type: "TEXT", nullable: false),
                    Limit = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScheduledFor = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_runs", x => x.Id);
                    table.UniqueConstraint("AK_recommendation_runs_Id_TenantId_OwnerUserId", x => new { x.Id, x.TenantId, x.OwnerUserId });
                    table.CheckConstraint("CK_recommendation_run_schedule_pair", "(\"ScheduleId\" IS NULL AND \"ScheduledFor\" IS NULL) OR (\"ScheduleId\" IS NOT NULL AND \"ScheduledFor\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_recommendation_runs_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_runs_job_schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "job_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_runs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_runs_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_source_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SnapshotVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderRevision = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ArtworkReferenceKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PayloadSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RetrievedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_source_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_playlist_source_snapshots_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_snapshots_payload_hash", "length(\"PayloadSha256\") = 64");
                    table.CheckConstraint("CK_playlist_snapshots_stable_artwork", "\"ArtworkReferenceKey\" IS NULL OR \"ArtworkReferenceKey\" NOT LIKE '%://%'");
                    table.CheckConstraint("CK_playlist_snapshots_version", "\"SnapshotVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_PlaylistSourceSnapshot_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSourceSnapshot_PlaylistLink",
                        columns: x => new { x.TenantId, x.PlaylistLinkId },
                        principalTable: "playlist_links",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_snapshot_provider_account",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_source_snapshots_durable_jobs_SourceJobId",
                        column: x => x.SourceJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_metadata_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderTrackIdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BackendPrincipalId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ResourceKind = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ExternalIdHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SnapshotVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderRevision = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RetrievedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_metadata_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_external_metadata_snapshots_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_external_snapshots_external_hash", "length(\"ExternalIdHash\") = 64");
                    table.CheckConstraint("CK_external_snapshots_payload_hash", "length(\"PayloadSha256\") = 64");
                    table.CheckConstraint("CK_external_snapshots_version", "\"SnapshotVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_ExternalMetadataSnapshot_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_metadata_snapshots_durable_jobs_SourceJobId",
                        column: x => x.SourceJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_snapshot_provider_account",
                        columns: x => new { x.ProviderAccountId, x.ProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_snapshot_provider_identity",
                        column: x => x.ProviderTrackIdentityId,
                        principalTable: "provider_track_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "listening_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    OccurrenceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ListenedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PositionTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ClientClass = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DeviceClass = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ImportProvenance = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TrackReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Album = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AlbumArtist = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RecordingMusicBrainzId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Isrc = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    MusicBrainzEnrichmentState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MusicBrainzEnrichmentConfidence = table.Column<double>(type: "REAL", nullable: true),
                    MusicBrainzSourceRevision = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MusicBrainzFactsJson = table.Column<string>(type: "TEXT", nullable: true),
                    MusicBrainzEnrichedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TrackNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ChosenByUser = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderTrackIdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderTrackReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_events", x => x.Id);
                    table.CheckConstraint("CK_listening_event_duration", "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" > 0");
                    table.CheckConstraint("CK_listening_event_musicbrainz_confidence", "\"MusicBrainzEnrichmentConfidence\" IS NULL OR (\"MusicBrainzEnrichmentConfidence\" >= 0 AND \"MusicBrainzEnrichmentConfidence\" <= 1)");
                    table.CheckConstraint("CK_listening_event_musicbrainz_state", "\"MusicBrainzEnrichmentState\" IN ('NotRequested', 'Pending', 'Resolved', 'Unresolved', 'Failed')");
                    table.CheckConstraint("CK_listening_event_position", "\"PositionTicks\" IS NULL OR \"PositionTicks\" >= 0");
                    table.CheckConstraint("CK_listening_event_track_number", "\"TrackNumber\" IS NULL OR \"TrackNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_listening_event_canonical_recording",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_event_library_track",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_event_provider_account",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_event_provider_identity",
                        column: x => x.ProviderTrackIdentityId,
                        principalTable: "provider_track_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_events_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_events_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "favorite_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Reversible = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorite_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_favorite_action_event",
                        columns: x => new { x.EventId, x.TenantId, x.OwnerUserId },
                        principalTable: "favorite_events",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "favorite_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorite_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_favorite_state_event",
                        columns: x => new { x.LastEventId, x.TenantId, x.OwnerUserId },
                        principalTable: "favorite_events",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_favorite_states_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_file_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManagedFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ReferenceKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReleasedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_file_references", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_file_reference_tenant_file",
                        columns: x => new { x.TenantId, x.ManagedFileId },
                        principalTable: "managed_files",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_file_reference_tenant_user",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "metadata_enrichment_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineageJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManagedArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PlanVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRevisionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DecisionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PathValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_enrichment_plans", x => x.Id);
                    table.UniqueConstraint("AK_enrichment_plan_scope", x => new { x.Id, x.TenantId, x.OwnerUserId, x.ManagedArtifactId, x.LineageJobId });
                    table.CheckConstraint("CK_enrichment_plans_fingerprint", "length(\"Fingerprint\") = 64");
                    table.ForeignKey(
                        name: "FK_metadata_enrichment_plans_durable_jobs_LineageJobId",
                        column: x => x.LineageJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_metadata_enrichment_plans_managed_files_ManagedArtifactId",
                        column: x => x.ManagedArtifactId,
                        principalTable: "managed_files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_metadata_enrichment_plans_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_metadata_enrichment_plans_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_download_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    DurableJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderArtifactId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Container = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Codec = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: true),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: true),
                    BitDepth = table.Column<int>(type: "INTEGER", nullable: true),
                    Channels = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ManagedFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PlacedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_download_artifacts", x => x.Id);
                    table.CheckConstraint("CK_download_artifact_length", "\"Length\" > 0");
                    table.CheckConstraint("CK_download_artifact_sha", "length(\"ContentSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_provider_download_artifacts_managed_files_ManagedFileId",
                        column: x => x.ManagedFileId,
                        principalTable: "managed_files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_download_artifacts_provider_download_workspaces_WorkspaceRecordId",
                        column: x => x.WorkspaceRecordId,
                        principalTable: "provider_download_workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_route_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteDecisionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutcomeKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NextProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_route_outcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_route_outcome_account",
                        columns: x => new { x.ProviderAccountId, x.ProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_outcome_decision",
                        columns: x => new { x.RouteDecisionId, x.TenantId },
                        principalTable: "provider_route_decisions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_provider_route_outcomes_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generated_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MaterializationState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendPlaylistId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TargetRevision = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MaterializedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_sets", x => x.Id);
                    table.UniqueConstraint("AK_generated_sets_Id_TenantId_OwnerUserId", x => new { x.Id, x.TenantId, x.OwnerUserId });
                    table.ForeignKey(
                        name: "FK_generated_sets_job_schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "job_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generated_sets_recommendation_runs_RunId_TenantId_OwnerUserId",
                        columns: x => new { x.RunId, x.TenantId, x.OwnerUserId },
                        principalTable: "recommendation_runs",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_generated_sets_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generated_sets_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SignalsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IdentityJson = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceRevision = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ExclusionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_candidates", x => x.Id);
                    table.UniqueConstraint("AK_recommendation_candidates_Id_TenantId_OwnerUserId", x => new { x.Id, x.TenantId, x.OwnerUserId });
                    table.ForeignKey(
                        name: "FK_recommendation_candidates_canonical_recordings_TenantId_CanonicalRecordingId",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_candidates_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_candidates_recommendation_runs_RunId_TenantId_OwnerUserId",
                        columns: x => new { x.RunId, x.TenantId, x.OwnerUserId },
                        principalTable: "recommendation_runs",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "playlist_sync_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistSourceSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    RuleVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MaterializationMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetRevisionBefore = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TargetRevisionAfter = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConflictCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PlannedTargetTrackCount = table.Column<int>(type: "INTEGER", nullable: true),
                    PlannedTargetDurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    VerifiedTargetTrackCount = table.Column<int>(type: "INTEGER", nullable: true),
                    VerifiedTargetDurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    VerificationCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_sync_runs", x => x.Id);
                    table.UniqueConstraint("AK_playlist_sync_runs_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_sync_generation", "\"Generation\" > 0");
                    table.CheckConstraint("CK_playlist_sync_verification_counts", "(\"PlannedTargetTrackCount\" IS NULL OR \"PlannedTargetTrackCount\" >= 0) AND (\"VerifiedTargetTrackCount\" IS NULL OR \"VerifiedTargetTrackCount\" >= 0)");
                    table.CheckConstraint("CK_playlist_sync_verification_durations", "(\"PlannedTargetDurationMilliseconds\" IS NULL OR \"PlannedTargetDurationMilliseconds\" >= 0) AND (\"VerifiedTargetDurationMilliseconds\" IS NULL OR \"VerifiedTargetDurationMilliseconds\" >= 0)");
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_JobSchedule",
                        columns: x => new { x.TenantId, x.ScheduleId },
                        principalTable: "job_schedules",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_PlaylistLink",
                        columns: x => new { x.TenantId, x.PlaylistLinkId },
                        principalTable: "playlist_links",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_PlaylistSourceSnapshot",
                        columns: x => new { x.TenantId, x.PlaylistSourceSnapshotId },
                        principalTable: "playlist_source_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_sync_runs_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "manual_track_overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    DecisionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    MatcherVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_track_overrides", x => x.Id);
                    table.CheckConstraint("CK_manual_overrides_shape", "(\"Decision\" = 'Pin' AND \"LibraryTrackId\" IS NOT NULL) OR \"Decision\" = 'Reject'");
                    table.CheckConstraint("CK_manual_overrides_version", "\"DecisionVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_ManualTrackOverride_ExternalMetadataSnapshot",
                        columns: x => new { x.TenantId, x.ExternalSnapshotId },
                        principalTable: "external_metadata_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualTrackOverride_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualTrackOverride_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "track_matches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Threshold = table.Column<double>(type: "REAL", nullable: false),
                    DecisionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSnapshotVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    LibraryIndexRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    MatcherVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PolicyVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CandidateResultsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_track_matches", x => x.Id);
                    table.UniqueConstraint("AK_track_matches_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_track_matches_confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1 AND \"Threshold\" >= 0 AND \"Threshold\" <= 1");
                    table.CheckConstraint("CK_track_matches_selected_shape", "(\"State\" IN ('Accepted', 'Suggested') AND (\"LibraryTrackId\" IS NOT NULL OR \"CanonicalRecordingId\" IS NOT NULL)) OR (\"State\" = 'Pinned' AND \"LibraryTrackId\" IS NOT NULL) OR (\"State\" IN ('Unresolved', 'Rejected', 'Ambiguous') AND \"LibraryTrackId\" IS NULL)");
                    table.CheckConstraint("CK_track_matches_version", "\"DecisionVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_TrackMatch_Canonicaling",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackMatch_ExternalMetadataSnapshot",
                        columns: x => new { x.TenantId, x.ExternalSnapshotId },
                        principalTable: "external_metadata_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackMatch_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackMatch_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "metadata_enrichment_applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManagedArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineageJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtifactContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SafeErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_enrichment_applications", x => x.Id);
                    table.CheckConstraint("CK_enrichment_applications_sha256", "length(\"ArtifactContentSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_enrichment_application_plan",
                        columns: x => new { x.PlanId, x.TenantId, x.OwnerUserId, x.ManagedArtifactId, x.LineageJobId },
                        principalTable: "metadata_enrichment_plans",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId", "ManagedArtifactId", "LineageJobId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_metadata_enrichment_applications_durable_jobs_LineageJobId",
                        column: x => x.LineageJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_metadata_enrichment_applications_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generated_set_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GeneratedSetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ExplanationJson = table.Column<string>(type: "TEXT", nullable: false),
                    IdentityJson = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_set_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generated_set_entries_generated_sets_GeneratedSetId_TenantId_OwnerUserId",
                        columns: x => new { x.GeneratedSetId, x.TenantId, x.OwnerUserId },
                        principalTable: "generated_sets",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    TrackKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_feedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recommendation_feedback_recommendation_candidates_CandidateId_TenantId_OwnerUserId",
                        columns: x => new { x.CandidateId, x.TenantId, x.OwnerUserId },
                        principalTable: "recommendation_candidates",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recommendation_feedback_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_feedback_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_target_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryTrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBySyncRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetEntryId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LastKnownPosition = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_target_memberships", x => x.Id);
                    table.CheckConstraint("CK_playlist_membership_position", "\"LastKnownPosition\" >= 0");
                    table.ForeignKey(
                        name: "FK_PlaylistTargetMembership_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistTargetMembership_PlaylistLink",
                        columns: x => new { x.TenantId, x.PlaylistLinkId },
                        principalTable: "playlist_links",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistTargetMembership_PlaylistSyncRun",
                        columns: x => new { x.TenantId, x.CreatedBySyncRunId },
                        principalTable: "playlist_sync_runs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_source_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistSourceSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalMetadataSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublishedTrackMatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourcePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceEntryIdHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_source_entries", x => x.Id);
                    table.UniqueConstraint("AK_playlist_source_entries_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_source_entry_hash", "length(\"SourceEntryIdHash\") = 64");
                    table.CheckConstraint("CK_playlist_source_entry_position", "\"SourcePosition\" >= 0");
                    table.ForeignKey(
                        name: "FK_PlaylistSourceEntry_ExternalMetadataSnapshot",
                        columns: x => new { x.TenantId, x.ExternalMetadataSnapshotId },
                        principalTable: "external_metadata_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSourceEntry_PlaylistSourceSnapshot",
                        columns: x => new { x.TenantId, x.PlaylistSourceSnapshotId },
                        principalTable: "playlist_source_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSourceEntry_TrackMatch",
                        columns: x => new { x.TenantId, x.PublishedTrackMatchId },
                        principalTable: "track_matches",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_sync_entry_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistSyncRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistSourceEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackMatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LibraryTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourcePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetPosition = table.Column<int>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OutcomeCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_sync_entry_results", x => x.Id);
                    table.CheckConstraint("CK_playlist_result_positions", "\"SourcePosition\" >= 0 AND (\"TargetPosition\" IS NULL OR \"TargetPosition\" >= 0)");
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_PlaylistSourceEntry",
                        columns: x => new { x.TenantId, x.PlaylistSourceEntryId },
                        principalTable: "playlist_source_entries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_PlaylistSyncRun",
                        columns: x => new { x.TenantId, x.PlaylistSyncRunId },
                        principalTable: "playlist_sync_runs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_TrackMatch",
                        columns: x => new { x.TenantId, x.TrackMatchId },
                        principalTable: "track_matches",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_auth_sessions_ExpiresAt",
                table: "admin_auth_sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_admin_oidc_links_BackendIdentityId",
                table: "admin_oidc_links",
                column: "BackendIdentityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_oidc_links_SecretReferenceId",
                table: "admin_oidc_links",
                column: "SecretReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_application_cache_category_updated",
                table: "application_cache_entries",
                columns: new[] { "Category", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_application_cache_expires_at",
                table: "application_cache_entries",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_updates",
                table: "audit_events",
                columns: new[] { "TenantId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorUserId",
                table: "audit_events",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CorrelationId",
                table: "audit_events",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_backend_identities_BackendType_BackendInstanceId_PrincipalId",
                table: "backend_identities",
                columns: new[] { "BackendType", "BackendInstanceId", "PrincipalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backend_identities_TenantId_UserId",
                table: "backend_identities",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_backend_identities_UserId",
                table: "backend_identities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_backups_CreatedAt",
                table: "backups",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_canonical_artists_TenantId_MusicBrainzArtistId",
                table: "canonical_artists",
                columns: new[] { "TenantId", "MusicBrainzArtistId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_artists_TenantId_SortName_Id",
                table: "canonical_artists",
                columns: new[] { "TenantId", "SortName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_catalog_aliases_TenantId_EntityKind_CanonicalEntityId",
                table: "canonical_catalog_aliases",
                columns: new[] { "TenantId", "EntityKind", "CanonicalEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_catalog_aliases_TenantId_Namespace_EntityKind_ExternalIdHash",
                table: "canonical_catalog_aliases",
                columns: new[] { "TenantId", "Namespace", "EntityKind", "ExternalIdHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recording_artists_TenantId_CanonicalArtistId_CanonicalRecordingId",
                table: "canonical_recording_artists",
                columns: new[] { "TenantId", "CanonicalArtistId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_TenantId_CreatedByUserId",
                table: "canonical_recordings",
                columns: new[] { "TenantId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_TenantId_Isrc",
                table: "canonical_recordings",
                columns: new[] { "TenantId", "Isrc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_TenantId_MusicBrainzRecordingId",
                table: "canonical_recordings",
                columns: new[] { "TenantId", "MusicBrainzRecordingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_TenantId_Title_Id",
                table: "canonical_recordings",
                columns: new[] { "TenantId", "Title", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_group_artists_TenantId_CanonicalArtistId_CanonicalReleaseGroupId",
                table: "canonical_release_group_artists",
                columns: new[] { "TenantId", "CanonicalArtistId", "CanonicalReleaseGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_groups_TenantId_MusicBrainzReleaseGroupId",
                table: "canonical_release_groups",
                columns: new[] { "TenantId", "MusicBrainzReleaseGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_groups_TenantId_Title_Id",
                table: "canonical_release_groups",
                columns: new[] { "TenantId", "Title", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_tracks_TenantId_CanonicalRecordingId",
                table: "canonical_release_tracks",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_tracks_TenantId_CanonicalReleaseId_MediumPosition_TrackPosition",
                table: "canonical_release_tracks",
                columns: new[] { "TenantId", "CanonicalReleaseId", "MediumPosition", "TrackPosition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_tracks_TenantId_MusicBrainzTrackId",
                table: "canonical_release_tracks",
                columns: new[] { "TenantId", "MusicBrainzTrackId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_releases_TenantId_CanonicalReleaseGroupId_ReleaseDate",
                table: "canonical_releases",
                columns: new[] { "TenantId", "CanonicalReleaseGroupId", "ReleaseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_releases_TenantId_MusicBrainzReleaseId",
                table: "canonical_releases",
                columns: new[] { "TenantId", "MusicBrainzReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_facts_TenantId_EntityKind_CanonicalEntityId_FieldName",
                table: "catalog_facts",
                columns: new[] { "TenantId", "EntityKind", "CanonicalEntityId", "FieldName" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_facts_TenantId_SourceId_RefreshAfter",
                table: "catalog_facts",
                columns: new[] { "TenantId", "SourceId", "RefreshAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_downloaded_song_mapping_identity",
                table: "downloaded_song_mappings",
                columns: new[] { "ScopeKey", "ProviderId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_job_updates",
                table: "durable_jobs",
                columns: new[] { "TenantId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_ProviderAccountId",
                table: "durable_jobs",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_ScopeKey_Type_IdempotencyKey",
                table: "durable_jobs",
                columns: new[] { "ScopeKey", "Type", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_State_AvailableAt_Priority",
                table: "durable_jobs",
                columns: new[] { "State", "AvailableAt", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_TenantId_OwnerUserId",
                table: "durable_jobs",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "UX_durable_job_owner_lineage",
                table: "durable_jobs",
                columns: new[] { "Id", "TenantId", "OwnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_durable_job_tenant_lineage",
                table: "durable_jobs",
                columns: new[] { "Id", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_extension_logs_ExtensionId_CreatedAt",
                table: "extension_logs",
                columns: new[] { "ExtensionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_extension_logs_ExtensionPackageId",
                table: "extension_logs",
                column: "ExtensionPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_extension_packages_ExtensionId_State",
                table: "extension_packages",
                columns: new[] { "ExtensionId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_extension_packages_ExtensionId_Version_Sha256",
                table: "extension_packages",
                columns: new[] { "ExtensionId", "Version", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_extension_packages_PreviousPackageId",
                table: "extension_packages",
                column: "PreviousPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_extension_packages_RegistryId",
                table: "extension_packages",
                column: "RegistryId");

            migrationBuilder.CreateIndex(
                name: "IX_extension_permission_review_key",
                table: "extension_permission_reviews",
                columns: new[] { "ExtensionPackageId", "PermissionKind", "PermissionValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_extension_permission_reviews_ReviewedByUserId",
                table: "extension_permission_reviews",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_extension_registries_RegistryUrl",
                table: "extension_registries",
                column: "RegistryUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_ProviderAccountId_ProviderId",
                table: "external_metadata_snapshots",
                columns: new[] { "ProviderAccountId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_ProviderTrackIdentityId",
                table: "external_metadata_snapshots",
                column: "ProviderTrackIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_SourceJobId",
                table: "external_metadata_snapshots",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_TenantId_OwnerUserId",
                table: "external_metadata_snapshots",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_external_snapshot_version",
                table: "external_metadata_snapshots",
                columns: new[] { "TenantId", "ProviderAccountId", "ResourceKind", "ExternalIdHash", "SnapshotVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_favorite_action_policies_TenantId_UpdatedByUserId",
                table: "favorite_action_policies",
                columns: new[] { "TenantId", "UpdatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_policy_credential_reference",
                table: "favorite_action_policies",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_favorite_policy_scope",
                table: "favorite_action_policies",
                columns: new[] { "TenantId", "OwnerUserId", "Scope", "Protocol", "BackendInstanceId", "LibraryScopeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_favorite_action_event",
                table: "favorite_actions",
                columns: new[] { "EventId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_action_owner_state",
                table: "favorite_actions",
                columns: new[] { "TenantId", "OwnerUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_action_type",
                table: "favorite_actions",
                columns: new[] { "EventId", "ActionType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_favorite_event_credential_reference",
                table: "favorite_events",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_favorite_event_job",
                table: "favorite_events",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_favorite_event_key",
                table: "favorite_events",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_favorite_event_owner_created",
                table: "favorite_events",
                columns: new[] { "TenantId", "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_state_event",
                table: "favorite_states",
                columns: new[] { "LastEventId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_state_owner_target",
                table: "favorite_states",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_entries_GeneratedSetId_Position",
                table: "generated_set_entries",
                columns: new[] { "GeneratedSetId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_entries_GeneratedSetId_TenantId_OwnerUserId",
                table: "generated_set_entries",
                columns: new[] { "GeneratedSetId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_credential_reference",
                table: "generated_sets",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_schedule",
                table: "generated_sets",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_generated_sets_RunId",
                table: "generated_sets",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generated_sets_RunId_TenantId_OwnerUserId",
                table: "generated_sets",
                columns: new[] { "RunId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_generated_sets_TenantId_OwnerUserId",
                table: "generated_sets",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_intelligence_policy_credential_reference",
                table: "intelligence_policies",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_intelligence_policy_scope",
                table: "intelligence_policies",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_attempts_JobId_AttemptNumber",
                table: "job_attempts",
                columns: new[] { "JobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_schedules_Enabled_NextRunAt",
                table: "job_schedules",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_job_schedules_TenantId_OwnerUserId",
                table: "job_schedules",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_env_imports_AuditEventId",
                table: "legacy_env_imports",
                column: "AuditEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_env_imports_TenantId_ActorUserId",
                table: "legacy_env_imports",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_env_imports_TenantId_SourceSha256_SchemaVersion",
                table: "legacy_env_imports",
                columns: new[] { "TenantId", "SourceSha256", "SchemaVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_track_backend_item",
                table: "library_tracks",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "BackendInstanceId", "BackendItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_track_scoped_isrc",
                table: "library_tracks",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "Isrc" });

            migrationBuilder.CreateIndex(
                name: "IX_library_track_scoped_musicbrainz",
                table: "library_tracks",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "MusicBrainzRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_library_tracks_BackendIdentityId",
                table: "library_tracks",
                column: "BackendIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_library_tracks_TenantId_CanonicalRecordingId",
                table: "library_tracks",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_event_occurrence",
                table: "listening_events",
                columns: new[] { "TenantId", "OwnerUserId", "OccurrenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_listening_event_scope_history",
                table: "listening_events",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "State", "ListenedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_events_ProviderAccountId",
                table: "listening_events",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_listening_events_ProviderTrackIdentityId",
                table: "listening_events",
                column: "ProviderTrackIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_listening_events_TenantId_CanonicalRecordingId",
                table: "listening_events",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_events_TenantId_LibraryTrackId",
                table: "listening_events",
                columns: new[] { "TenantId", "LibraryTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_history_import_content",
                table: "listening_history_imports",
                columns: new[] { "TenantId", "OwnerUserId", "ContentSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_history_import_job",
                table: "listening_history_imports",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_listening_history_import_scope",
                table: "listening_history_imports",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_intake_token_scope",
                table: "listening_intake_tokens",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_intake_token_secret",
                table: "listening_intake_tokens",
                column: "SecretReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_listening_profiles_TenantId_OwnerUserId_CreatedAt",
                table: "listening_profiles",
                columns: new[] { "TenantId", "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_signal_idempotency",
                table: "listening_signals",
                columns: new[] { "TenantId", "OwnerUserId", "SignalKey" },
                unique: true,
                filter: "\"SignalKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listening_signals_SourceJobId",
                table: "listening_signals",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_listening_signals_TenantId_OwnerUserId_ExpiresAt",
                table: "listening_signals",
                columns: new[] { "TenantId", "OwnerUserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_reference_key",
                table: "managed_file_references",
                columns: new[] { "ManagedFileId", "ReferenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_reference_owner",
                table: "managed_file_references",
                columns: new[] { "TenantId", "OwnerUserId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_references_TenantId_ManagedFileId",
                table: "managed_file_references",
                columns: new[] { "TenantId", "ManagedFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_fingerprint",
                table: "managed_files",
                columns: new[] { "RootId", "ContentSha256", "ScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_job",
                table: "managed_files",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_path",
                table: "managed_files",
                column: "CanonicalPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_user",
                table: "managed_files",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "UX_managed_file_owner_lineage",
                table: "managed_files",
                columns: new[] { "Id", "TenantId", "OwnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_lyrics_mappings_IdentityHash",
                table: "manual_lyrics_mappings",
                column: "IdentityHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_lyrics_mappings_UpdatedAt",
                table: "manual_lyrics_mappings",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_manual_track_override_active",
                table: "manual_track_overrides",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "ExternalSnapshotId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_manual_track_overrides_TenantId_ExternalSnapshotId",
                table: "manual_track_overrides",
                columns: new[] { "TenantId", "ExternalSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_track_overrides_TenantId_LibraryTrackId",
                table: "manual_track_overrides",
                columns: new[] { "TenantId", "LibraryTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_application_hash",
                table: "metadata_enrichment_applications",
                columns: new[] { "TenantId", "OwnerUserId", "PlanId", "ManagedArtifactId", "ArtifactContentSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_application_job",
                table: "metadata_enrichment_applications",
                column: "LineageJobId");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_application_plan",
                table: "metadata_enrichment_applications",
                columns: new[] { "PlanId", "TenantId", "OwnerUserId", "ManagedArtifactId", "LineageJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_plan_file",
                table: "metadata_enrichment_plans",
                column: "ManagedArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_plan_fingerprint",
                table: "metadata_enrichment_plans",
                columns: new[] { "TenantId", "OwnerUserId", "ManagedArtifactId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_plan_job",
                table: "metadata_enrichment_plans",
                column: "LineageJobId");

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_states_TenantId_UserId",
                table: "onboarding_states",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_State_AvailableAt",
                table: "outbox_messages",
                columns: new[] { "State", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_updates",
                table: "outbox_messages",
                columns: new[] { "TenantId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_playback_delivery_idempotency",
                table: "playback_delivery_checkpoints",
                columns: new[] { "TenantId", "OwnerUserId", "SignalKey", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playback_delivery_occurrence_status",
                table: "playback_delivery_checkpoints",
                columns: new[] { "TenantId", "OwnerUserId", "OccurrenceKey", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_link_source_target",
                table: "playlist_links",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "SourceProviderId", "ProviderAccountId", "SourcePlaylistIdHash", "TargetProtocol", "TargetBackendInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_links_ProviderAccountId_SourceProviderId",
                table: "playlist_links",
                columns: new[] { "ProviderAccountId", "SourceProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_links_TenantId_ScheduleId",
                table: "playlist_links",
                columns: new[] { "TenantId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_entries_TenantId_ExternalMetadataSnapshotId",
                table: "playlist_source_entries",
                columns: new[] { "TenantId", "ExternalMetadataSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_entries_TenantId_PublishedTrackMatchId",
                table: "playlist_source_entries",
                columns: new[] { "TenantId", "PublishedTrackMatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_entry_position",
                table: "playlist_source_entries",
                columns: new[] { "TenantId", "PlaylistSourceSnapshotId", "SourcePosition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_snapshot_published",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "PlaylistLinkId", "PublishedAt", "SnapshotVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_snapshot_updates",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "RetrievedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_snapshot_version",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "PlaylistLinkId", "SnapshotVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_snapshots_ProviderAccountId",
                table: "playlist_source_snapshots",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_snapshots_SourceJobId",
                table: "playlist_source_snapshots",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_snapshots_TenantId_OwnerUserId",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_result_run_position",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "PlaylistSyncRunId", "SourcePosition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_entry_results_TenantId_LibraryTrackId",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "LibraryTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_entry_results_TenantId_PlaylistSourceEntryId",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "PlaylistSourceEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_entry_results_TenantId_TrackMatchId",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "TrackMatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_JobId",
                table: "playlist_sync_runs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_OwnerUserId",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_PlaylistLinkId_IdempotencyKey",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "PlaylistLinkId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_PlaylistSourceSnapshotId",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "PlaylistSourceSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_ScheduleId",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_membership_target_entry",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "PlaylistLinkId", "TargetEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_membership_track_active",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "PlaylistLinkId", "LibraryTrackId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_target_memberships_TenantId_CreatedBySyncRunId",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "CreatedBySyncRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_target_memberships_TenantId_LibraryTrackId",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "LibraryTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_CreatedByUserId",
                table: "provider_accounts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_ProviderId_TenantId_OwnerUserId",
                table: "provider_accounts",
                columns: new[] { "ProviderId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_SecretReferenceId",
                table: "provider_accounts",
                column: "SecretReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_TenantId_OwnerUserId",
                table: "provider_accounts",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_circuits_ProviderAccountId_Capability",
                table: "provider_circuits",
                columns: new[] { "ProviderAccountId", "Capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_download_artifact_identity",
                table: "provider_download_artifacts",
                columns: new[] { "WorkspaceRecordId", "ProviderArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_download_artifact_job_provider",
                table: "provider_download_artifacts",
                columns: new[] { "TenantId", "DurableJobId", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_download_artifacts_ManagedFileId",
                table: "provider_download_artifacts",
                column: "ManagedFileId");

            migrationBuilder.CreateIndex(
                name: "IX_download_workspace_idempotency",
                table: "provider_download_workspaces",
                columns: new[] { "TenantId", "DurableJobId", "ProviderId", "ProviderAccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_download_workspaces_DurableJobId",
                table: "provider_download_workspaces",
                column: "DurableJobId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_download_workspaces_ProviderAccountId_ProviderId",
                table: "provider_download_workspaces",
                columns: new[] { "ProviderAccountId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_download_workspaces_TenantId_OwnerUserId",
                table: "provider_download_workspaces",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_download_workspaces_WorkspaceId",
                table: "provider_download_workspaces",
                column: "WorkspaceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_rollup_account_capability_window",
                table: "provider_health_rollups",
                columns: new[] { "ProviderAccountId", "Capability", "WindowStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_rollup_window_end",
                table: "provider_health_rollups",
                column: "WindowEnd");

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_account_capability_observed",
                table: "provider_health_samples",
                columns: new[] { "ProviderAccountId", "Capability", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_updates",
                table: "provider_health_samples",
                columns: new[] { "TenantId", "ObservedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decision_correlation",
                table: "provider_route_decisions",
                columns: new[] { "TenantId", "CorrelationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decision_key",
                table: "provider_route_decisions",
                columns: new[] { "TenantId", "RouteKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decisions_DurableJobId",
                table: "provider_route_decisions",
                column: "DurableJobId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decisions_SelectedProviderAccountId_SelectedProviderId",
                table: "provider_route_decisions",
                columns: new[] { "SelectedProviderAccountId", "SelectedProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decisions_TenantId_ActorUserId",
                table: "provider_route_decisions",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcome_key",
                table: "provider_route_outcomes",
                columns: new[] { "RouteDecisionId", "OutcomeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcome_tenant_created",
                table: "provider_route_outcomes",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcomes_ProviderAccountId_ProviderId",
                table: "provider_route_outcomes",
                columns: new[] { "ProviderAccountId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcomes_RouteDecisionId_TenantId",
                table: "provider_route_outcomes",
                columns: new[] { "RouteDecisionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identities_ProviderAccountId_ProviderId",
                table: "provider_track_identities",
                columns: new[] { "ProviderAccountId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identities_TenantId_CanonicalRecordingId",
                table: "provider_track_identities",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identity_account_exact",
                table: "provider_track_identities",
                columns: new[] { "TenantId", "ProviderId", "ResourceKind", "CatalogNamespace", "ProviderAccountId", "ExternalIdHash" },
                unique: true,
                filter: "\"Scope\" = 'Account'");

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identity_catalog_exact",
                table: "provider_track_identities",
                columns: new[] { "TenantId", "ProviderId", "ResourceKind", "CatalogNamespace", "ExternalIdHash" },
                unique: true,
                filter: "\"Scope\" = 'Catalog'");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_ProviderAccountId",
                table: "recommendation_candidates",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_RunId_Position",
                table: "recommendation_candidates",
                columns: new[] { "RunId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_RunId_TenantId_OwnerUserId",
                table: "recommendation_candidates",
                columns: new[] { "RunId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_TenantId_CanonicalRecordingId",
                table: "recommendation_candidates",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_feedback_CandidateId_TenantId_OwnerUserId",
                table: "recommendation_feedback",
                columns: new[] { "CandidateId", "TenantId", "OwnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_feedback_TenantId_OwnerUserId_Protocol_BackendInstanceId_LibraryScopeId_TrackKey",
                table: "recommendation_feedback",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "TrackKey" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_credential_reference",
                table: "recommendation_runs",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_idempotency",
                table: "recommendation_runs",
                columns: new[] { "TenantId", "OwnerUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_schedule_history",
                table: "recommendation_runs",
                columns: new[] { "TenantId", "OwnerUserId", "ScheduleId", "ScheduledFor" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_schedule_occurrence",
                table: "recommendation_runs",
                columns: new[] { "ScheduleId", "ScheduledFor" },
                unique: true,
                filter: "\"ScheduleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_runs_JobId",
                table: "recommendation_runs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_secret_references_BackendIdentityId",
                table: "secret_references",
                column: "BackendIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_secret_references_TenantId_Purpose",
                table: "secret_references",
                columns: new[] { "TenantId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_versions_SecretReferenceId_Version",
                table: "secret_versions",
                columns: new[] { "SecretReferenceId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_runtime_settings_TenantId_Key",
                table: "tenant_runtime_settings",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_runtime_settings_TenantId_UpdatedByUserId",
                table: "tenant_runtime_settings",
                columns: new[] { "TenantId", "UpdatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Slug",
                table: "tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_track_match_scoped_decision",
                table: "track_matches",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "ExternalSnapshotId", "DecisionVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_track_match_updates",
                table: "track_matches",
                columns: new[] { "TenantId", "DecidedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_track_matches_TenantId_CanonicalRecordingId",
                table: "track_matches",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_track_matches_TenantId_ExternalSnapshotId",
                table: "track_matches",
                columns: new[] { "TenantId", "ExternalSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_track_matches_TenantId_LibraryTrackId",
                table: "track_matches",
                columns: new[] { "TenantId", "LibraryTrackId" });
            allstarr.Core.Storage.Sql.SqliteIntegrity.ConfigureBaseline(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_auth_sessions");

            migrationBuilder.DropTable(
                name: "admin_oidc_links");

            migrationBuilder.DropTable(
                name: "application_cache_entries");

            migrationBuilder.DropTable(
                name: "backups");

            migrationBuilder.DropTable(
                name: "canonical_catalog_aliases");

            migrationBuilder.DropTable(
                name: "canonical_recording_artists");

            migrationBuilder.DropTable(
                name: "canonical_release_group_artists");

            migrationBuilder.DropTable(
                name: "canonical_release_tracks");

            migrationBuilder.DropTable(
                name: "catalog_facts");

            migrationBuilder.DropTable(
                name: "downloaded_song_mappings");

            migrationBuilder.DropTable(
                name: "extension_logs");

            migrationBuilder.DropTable(
                name: "extension_permission_reviews");

            migrationBuilder.DropTable(
                name: "favorite_action_policies");

            migrationBuilder.DropTable(
                name: "favorite_actions");

            migrationBuilder.DropTable(
                name: "favorite_states");

            migrationBuilder.DropTable(
                name: "generated_set_entries");

            migrationBuilder.DropTable(
                name: "intelligence_policies");

            migrationBuilder.DropTable(
                name: "job_attempts");

            migrationBuilder.DropTable(
                name: "legacy_env_imports");

            migrationBuilder.DropTable(
                name: "listening_events");

            migrationBuilder.DropTable(
                name: "listening_history_imports");

            migrationBuilder.DropTable(
                name: "listening_intake_tokens");

            migrationBuilder.DropTable(
                name: "listening_profiles");

            migrationBuilder.DropTable(
                name: "listening_signals");

            migrationBuilder.DropTable(
                name: "managed_file_references");

            migrationBuilder.DropTable(
                name: "manual_lyrics_mappings");

            migrationBuilder.DropTable(
                name: "manual_track_overrides");

            migrationBuilder.DropTable(
                name: "metadata_enrichment_applications");

            migrationBuilder.DropTable(
                name: "onboarding_states");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "playback_delivery_checkpoints");

            migrationBuilder.DropTable(
                name: "playlist_sync_entry_results");

            migrationBuilder.DropTable(
                name: "playlist_target_memberships");

            migrationBuilder.DropTable(
                name: "provider_circuits");

            migrationBuilder.DropTable(
                name: "provider_download_artifacts");

            migrationBuilder.DropTable(
                name: "provider_health_rollups");

            migrationBuilder.DropTable(
                name: "provider_health_samples");

            migrationBuilder.DropTable(
                name: "provider_route_outcomes");

            migrationBuilder.DropTable(
                name: "recommendation_feedback");

            migrationBuilder.DropTable(
                name: "secret_versions");

            migrationBuilder.DropTable(
                name: "tenant_runtime_settings");

            migrationBuilder.DropTable(
                name: "canonical_artists");

            migrationBuilder.DropTable(
                name: "canonical_releases");

            migrationBuilder.DropTable(
                name: "extension_packages");

            migrationBuilder.DropTable(
                name: "favorite_events");

            migrationBuilder.DropTable(
                name: "generated_sets");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "metadata_enrichment_plans");

            migrationBuilder.DropTable(
                name: "playlist_source_entries");

            migrationBuilder.DropTable(
                name: "playlist_sync_runs");

            migrationBuilder.DropTable(
                name: "provider_download_workspaces");

            migrationBuilder.DropTable(
                name: "provider_route_decisions");

            migrationBuilder.DropTable(
                name: "recommendation_candidates");

            migrationBuilder.DropTable(
                name: "canonical_release_groups");

            migrationBuilder.DropTable(
                name: "extension_registries");

            migrationBuilder.DropTable(
                name: "managed_files");

            migrationBuilder.DropTable(
                name: "track_matches");

            migrationBuilder.DropTable(
                name: "playlist_source_snapshots");

            migrationBuilder.DropTable(
                name: "recommendation_runs");

            migrationBuilder.DropTable(
                name: "external_metadata_snapshots");

            migrationBuilder.DropTable(
                name: "library_tracks");

            migrationBuilder.DropTable(
                name: "playlist_links");

            migrationBuilder.DropTable(
                name: "durable_jobs");

            migrationBuilder.DropTable(
                name: "provider_track_identities");

            migrationBuilder.DropTable(
                name: "job_schedules");

            migrationBuilder.DropTable(
                name: "canonical_recordings");

            migrationBuilder.DropTable(
                name: "provider_accounts");

            migrationBuilder.DropTable(
                name: "secret_references");

            migrationBuilder.DropTable(
                name: "backend_identities");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
