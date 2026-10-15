# Storage operations

The runtime stores durable state in SQLite at `{Storage:DataDirectory}/{Storage:DatabaseFileName}` (defaults: `/app/data/allstarr.db`). Run one Allstarr process per database and mount the data directory on local disk. Network filesystems are rejected. PostgreSQL connection settings are no longer used by the runtime.

## One local data folder

Compose mounts `ALLSTARR_DATA_PATH` at `/app/data`; `.env.example` sets `./data`. With no override, Compose uses the `allstarr-data` named volume. Direct runs can set `Storage__DataDirectory` to another local folder. First startup creates the database and key ring without a setup script or database service.

| Path within the data folder | Contents |
| --- | --- |
| `allstarr.db`, `allstarr.db-wal`, `allstarr.db-shm` | Database and live SQLite sidecars |
| `keyring.json` | Keys for encrypted saved credentials |
| `data-protection/` | Browser session protection keys |
| `downloads/`, `kept/` | Managed and retained music |
| `extensions/` | Installed extension packages |
| `cache/` | Media cache and temporary download/import workspaces |
| `backups/` | Verified database-and-key-ring archives |
| `restore-pending/`, `pre-restore-*/` | Staged and previous restore data |

Explicit library, extension, cache, workspace, and key-ring path overrides remain supported. Include those external paths separately when moving an installation. Optional Apple gateway and wrapper sessions remain in their own profile volumes.

To move to another host, stop Allstarr, copy the entire data folder including any sidecars, and preserve its private permissions. Also copy deployment configuration, optional provider volumes, and external path overrides. Mount the copy into a new container using the same image version. Keep the original container stopped so only one process uses each database, then verify accounts, playlists, settings, and playback. Never copy the active database piecemeal.

## Database readiness and recovery

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

## Event log and job retention

A background cleanup keeps the event log and finished playback jobs from growing the database with listening time. Each playback progress report is its own durable job; finished (succeeded or cancelled) playback jobs and their attempt events are removed once they are older than the event-log retention period. Events that another durable record references, such as a legacy import, are kept. Set these under `Operations:EventLog` (for example `Operations__EventLog__RetentionDays` in the container environment):

| Setting | Default | Purpose |
| --- | --- | --- |
| `Operations__EventLog__RetentionDays` | `30` | Age after which unreferenced events and finished playback jobs are removed (1–3650) |
| `Operations__EventLog__MaximumRows` | `250000` | Oldest unreferenced events beyond this count are removed, job attempt history first (1000–5000000) |
| `Operations__EventLog__JobHistoryRetentionDays` | `7` | Age after which job attempt history is removed; never longer than `RetentionDays` (1–3650) |
| `Operations__EventLog__CleanupIntervalMinutes` | `360` | Time between cleanup passes; one also runs at startup (5–10080) |
| `Operations__EventLog__CleanupBatchSize` | `1000` | Rows deleted per batch (100–10000) |

## Background job recovery and concurrency

Run one Allstarr process per database. After storage becomes ready, the worker immediately requeues jobs interrupted by the previous process. A cancellation saved before shutdown becomes terminal on recovery. Restart recovery preserves the job identity, account binding and retry budget; no lease expiration or heartbeat is required. A job interrupted by `Jobs__MaxConsecutiveInterruptions` restarts in a row (default `5`, range `1`–`100`) without finishing an attempt is marked failed with `worker_interrupted_repeatedly`, so work that takes the process down cannot loop forever; any finished attempt resets the count. A job's detail includes its latest attempt and retained completion history from the event ledger.

`Jobs__MaximumConcurrentJobs` defaults to `4` and accepts values from `1` to `32`. Each registered job type runs one execution at a time by default. Set `Jobs__TypeConcurrencyLimits__<job.type>` to an integer between one and the global maximum to allow more executions of that type. Keys use the normalized lower-case job type. The global limit still applies across types. Shutdown cancels and awaits every active handler; a temporary database failure retries persistence of a finished handler's result without invoking that handler again.

## Kept publication ownership

Managed kept files and their recording associations are stored separately from personal provider download artifacts. Publication retains the source user, job, track and account revision without changing the source artifact's owner. An existing personal download from before track binding was introduced stays personal; it is never guessed into a household kept route.

Playlist retention uses the same household recording association and managed references as favorites. A published snapshot or KeepAll change saves its reference updates, obsolete-work cancellation and new retention jobs in one SQLite transaction. Workers verify the current published entry and effective external route again after preparing audio. Retention workers read the requested source entry and current route inputs directly. An old snapshot or a disabled retention option cannot publish a lasting copy. Source updates and manual rematch publication release obsolete file associations even when a provider track keeps the same reference key.

Placement uses the configured kept root and the existing journal, checksum and filesystem identity checks. A provisional reference protects interrupted placement until the current job can adopt it. Rejected publication releases its own provisional reference and removes only an unpublished, unreferenced owned file. Cleanup refuses caller-owned database transactions, changed paths and any file with a kept route or active reference. Original backend files and provider source bytes remain untouched. Database backups do not include these audio files; preserve their configured roots separately.

Explicit Keep requests use the same verified household recording placement and durable reference owner as hearts and playlist retention. The cache index identifies the requested track. The worker downloads and verifies audio before publication rather than treating legacy cached bytes as verified provenance. Release Keep never deletes a household file directly and affects only the requesting administrator’s explicit reference. The final reference release queues the standard twenty-four-hour cleanup.

Kept playback checks the recorded root, path, length and filesystem identity on the opened read handle, rejecting symbolic links and replaced files without hashing the entire track on each stream, seek or HEAD request. Publication, ordinary ownership lookup and removal retain full content verification. Playback refuses kept files without a recorded filesystem identity and can use an eligible provider instead.

If a kept file is missing, damaged or replaced, a new heart, Keep or retained-playlist download can publish a verified replacement at a fresh path. Existing interests move with the recording routes in the same transaction. The old ownership record is retired and recorded in history. Allstarr leaves changed files and symbolic links at the old path untouched; inspect those paths before removing them manually. An available verified copy of the same recording can be reused instead of another placement.
