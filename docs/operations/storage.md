# Storage operations

The runtime stores durable state in SQLite at `{Storage:DataDirectory}/{Storage:DatabaseFileName}` (defaults: `/app/data/allstarr.db`). Run one Allstarr process per database and mount the data directory on local disk. Network filesystems are rejected. PostgreSQL connection settings are no longer used by the runtime.

Startup enables WAL, applies the checked-in baseline, and runs an integrity check before storage becomes ready. Each connection enforces foreign keys and uses normal synchronization with a five-second busy timeout. Unknown schema versions and damaged database files leave durable operations unavailable while the native proxy remains available. Startup does not replace damaged data or fall back to another database.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Storage__DataDirectory` | `/app/data` | Owned local database and backup directory |
| `Storage__DatabaseFileName` | `allstarr.db` | File name within the data directory |
| `Storage__AutoMigrate` | `true` | Apply the baseline before readiness |
| `Storage__CommandTimeoutSeconds` | `30` | Command execution timeout |
| `Storage__BackupRetentionCount` | `10` | Number of recent backup archives to retain (1–1000) |

Development installations use a fresh SQLite baseline; PostgreSQL data is not imported. Preserve existing PostgreSQL data separately before replacing an installation. Do not remove media roots or the encryption key ring.

Administrators can create and download verified backups from Settings → Maintenance. Each private ZIP in `{Storage:DataDirectory}/backups` contains a consistent SQLite snapshot, the configured encryption key ring, and a manifest with file checksums and schema information. Creation verifies database integrity and key availability; download verifies the archive checksums again. Keep downloaded archives private: together, the database and key ring can unlock saved credentials. Retention removes only recognized backup archives, leaving unrelated or damaged files untouched. Backups exclude media, caches, and extension packages.

To restore, choose a backup ZIP in Settings → Maintenance and confirm **Restore on restart**. Uploads are limited to 1 GiB, with a maximum expanded database size of 4 GiB. Allstarr checks the archive, schema history, database integrity, and required encryption keys, then stages the files in `{Storage:DataDirectory}/restore-pending`. The running database stays unchanged until you restart Allstarr. A pending restore prevents another backup or restore from starting.

Before opening storage after restart, Allstarr preserves the current database, SQLite sidecars, and key ring in a private `pre-restore-*` directory. It then installs the staged database and key ring. Keep these previous files until you have verified your accounts, playlists, and settings; they are not removed by backup retention. Caches, media, and installed extension packages are outside the restore.

If application is interrupted, the staged files remain available for retry on the next restart. A failed application leaves durable storage unavailable with `restore_application_failed`; fix disk or permission problems and restart again. Keep the configured database and key-ring paths unchanged during recovery. An external key-ring override must be writable for restore; a read-only secret mount cannot be replaced by Allstarr. Restore paths must not use symbolic links. Do not manually remove pending files or replace individual live files after a failed application: the database and key ring must remain a matching pair.

The former command-line storage tools and portable state transfer are unavailable.

The encryption key ring defaults to `{Storage:DataDirectory}/keyring.json`; `Secrets__KeyRingPath` overrides that location for existing secret mounts. On first startup, an empty database gets a randomly generated key ring with private file permissions. Normal startup never overwrites an existing key ring. If encrypted secrets exist but the key ring is missing, restore the original key ring: Allstarr leaves saved credentials unavailable and continues native proxy access. It does not generate a replacement key that would make those credentials unreadable.

Audio, artwork, extension packages, and the encryption key ring remain files. Original backend music is read-only. Keep the configured encryption key ring together with any offline copy of the database: losing it makes the corresponding encrypted provider credentials unreadable. Stop Allstarr before making an offline copy of its data directory, and include any WAL sidecar files. Never copy or replace the active database piecemeal.
