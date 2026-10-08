# Storage operations

The runtime stores durable state in SQLite at `{Storage:DataDirectory}/{Storage:DatabaseFileName}` (defaults: `/app/data/allstarr.db`). Run one Allstarr process per database and mount the data directory on local disk. Network filesystems are rejected. PostgreSQL connection settings are no longer used by the runtime.

Startup enables WAL, applies the checked-in baseline, and runs an integrity check before storage becomes ready. Each connection enforces foreign keys and uses normal synchronization with a five-second busy timeout. Unknown schema versions and damaged database files leave durable operations unavailable while the native proxy remains available. Startup does not replace damaged data or fall back to another database.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Storage__DataDirectory` | `/app/data` | Owned local database and backup directory |
| `Storage__DatabaseFileName` | `allstarr.db` | File name within the data directory |
| `Storage__AutoMigrate` | `true` | Apply the baseline before readiness |
| `Storage__CommandTimeoutSeconds` | `30` | Command execution timeout |

Development installations use a fresh SQLite baseline; PostgreSQL data is not imported. Preserve existing PostgreSQL data separately before replacing an installation. Do not remove media roots or the encryption key ring.

The Maintenance page reports SQLite readiness. Database backup and restore are temporarily unavailable; the page displays “Backups are being rebuilt,” and backup requests return `503 backups_unavailable`. The former command-line storage tools and portable state transfer are unavailable.

The encryption key ring defaults to `{Storage:DataDirectory}/keyring.json`; `Secrets__KeyRingPath` overrides that location for existing secret mounts. On first startup, an empty database gets a randomly generated key ring with private file permissions. Existing files are never overwritten. If encrypted secrets exist but the key ring is missing, restore the original key ring: Allstarr leaves saved credentials unavailable and continues native proxy access. It does not generate a replacement key that would make those credentials unreadable.

Audio, artwork, extension packages, and the encryption key ring remain files. Original backend music is read-only. Keep the configured encryption key ring together with any offline copy of the database: losing it makes the corresponding encrypted provider credentials unreadable. Stop Allstarr before making an offline copy of its data directory, and include any WAL sidecar files. Never copy or replace the active database piecemeal.
