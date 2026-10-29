# Storage operations

The runtime stores durable state in SQLite at `{Storage:DataDirectory}/{Storage:DatabaseFileName}` (defaults: `/app/data/allstarr.db`). Run one Allstarr process per database and mount the data directory on local disk. Network filesystems are rejected. PostgreSQL connection settings are no longer used by the runtime.

## One local data folder

Compose mounts `./data` at `/app/data` by default, with or without `.env`. Set `ALLSTARR_DATA_PATH` to use another local folder. An unchanged v2 Compose layout uses its existing persistent cache mount at `/app/cache/allstarr-v3` when no data mount exists. Direct runs can set `Storage__DataDirectory` to another local folder. First startup creates the database and key ring without a setup script or database service.

| Path within the data folder | Contents |
| --- | --- |
| `allstarr.db`, `allstarr.db-wal`, `allstarr.db-shm` | Database and live SQLite sidecars |
| `keyring.json` | Keys for encrypted saved credentials |
| `data-protection/` | Browser session protection keys |
| `.allstarr-storage` | Owned marker used to recognize a copied data folder |
| `downloads/`, `kept/` | Managed and retained music |
| `extensions/` | Installed extension packages |
| `apple-upload/` | Optional staged Apple APK/APKM packages |
| `cache/` | Media cache and temporary download/import workspaces |
| `backups/` | Verified database-and-key-ring archives |
| `restore-pending/`, `pre-restore-*/` | Staged and previous restore data |

Explicit library, extension, cache, workspace, and key-ring path overrides remain supported. Include those external paths separately when moving an installation. Optional Apple gateway and wrapper sessions remain in their own profile volumes.

## Playback cache and kept audio

New installations cache provider playback for **72 hours after last use**. **Library → Cached → Track cache behavior** controls playback caching and retention. Saved choices and explicit configuration overrides survive upgrades. The legacy `Permanent` storage value disables ordinary playback caching and sends requested managed downloads to Kept; it does not retain every played song.

Temporary audio is limited to **2 GiB by default**. Capacity pressure can evict the least recently used eligible files before their age limit. Active readers and background fills are protected. Cached lists complete published files and shows age expiry only when a tracked last-use time is available; the hourly cleanup may remove them after that time.

Hearts, explicit Keep and retained playlists use separate managed references. Cache eviction does not release these references or delete kept copies. Releasing the last reference starts a twenty-four-hour grace period, independent of the playback cache window. Back up kept audio separately from database-and-key-ring archives.

## Offline folder backup and restore

Stop Allstarr before copying its complete data folder. With the default layout:

```bash
docker compose stop allstarr
cp -a ./data ../allstarr-data-backup
docker compose up -d --no-deps allstarr
```

Use a new backup destination each time. The copy must run with permission to read every private file; use `sudo cp -a` for root-owned Docker data. Verify that the copy includes the database, key ring, private files, media and any SQLite sidecars. Preserve private permissions. Copy deployment configuration, external media roots and optional Apple gateway and wrapper volumes separately. Stop each optional service before copying its state; stop Valkey as well when preserving its volume for a v2 rollback. In a v2 layout, preserve the whole cache, download and kept mounts, including the new `cache/allstarr-v3` directory. A database-and-key-ring archive alone does not include those media files.

To restore a complete folder copy, stop Allstarr, preserve the current data folder separately, and put the verified copy back at the configured host path. Use the same image version and container mount paths. Start only one installation and check readiness, saved accounts, playlists, settings and playback. Do not merge selected database files into a live folder. Never copy or replace the active database piecemeal.

A copied folder is recognized at startup and owned audio is verified before filesystem identities are updated. Native proxying and health remain available. Check **Settings → Storage → Saved-file verification** and **Activity → Everyone → Queue**; changed, missing or linked files need repair. Keep the original stopped installation until verification and playback pass. Never edit ownership records to bypass their checks. See [copied data folder verification](#verifying-a-copied-folder).

For rollback to an earlier image, restore the matching pre-upgrade folder backup before starting that image. Keep the upgraded folder separately. An older image must not open a newer migrated database. For a v2 rollback, retain its untouched Compose file, `.env` and original media roots. See [deployment and rollback](deployment-profiles.md) for image selection without replacing the original Compose file.

## Database readiness and recovery

Startup enables WAL, applies the checked-in baseline, and runs an integrity check before storage becomes ready. Each connection enforces foreign keys and uses normal synchronization with a five-second busy timeout. Unknown schema versions and damaged database files leave durable operations unavailable while the native proxy remains available. Startup does not replace damaged data or fall back to another database.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Storage__DataDirectory` | `/app/data` | Owned local database and backup directory |
| `Storage__DatabaseFileName` | `allstarr.db` | File name within the data directory |
| `Storage__AutoMigrate` | `true` | Apply the baseline before readiness |
| `Storage__CommandTimeoutSeconds` | `30` | Command execution timeout |
| `Storage:BackupEnabled` | `true` | Daily database-and-key-ring backup |
| `Storage:BackupTimeUtc` | `03:00` | Daily time in UTC, in `HH:mm` format |
| `Storage:BackupDirectory` | Empty | Existing absolute container directory; empty uses the data volume's `backups/` folder |
| `Storage:BackupDailyRetentionCount` | `14` | Daily scheduled copies to keep (1–90) |
| `Storage:BackupWeeklyRetentionCount` | `8` | Weekly scheduled copies to keep (0–52) |
| `Storage:BackupRetentionCount` | `10` | Recent manual copies to keep (1–1000) |

Development installations use a fresh SQLite baseline; PostgreSQL data is not imported. Preserve existing PostgreSQL data separately before replacing an installation. Do not remove media roots or the encryption key ring.

Administrators configure daily backups and download archives in **Settings → Backups**. The status card shows the last verified time and size, the next scheduled run, current work and safe failure details. **Back up now** queues durable background work; acceptance does not mean the archive is complete. Work survives restart, and overlapping manual or scheduled requests do not start a second snapshot. A missed schedule runs once after recovery rather than creating a backlog of historical snapshots. Schedule changes apply to future work without a restart.

Each private ZIP contains a consistent SQLite snapshot, the configured encryption key ring, and a manifest with file checksums and schema information. Creation verifies database integrity and key availability; download verifies archive checksums again. Keep downloaded archives private: together, the database and key ring can unlock saved credentials. Backups exclude media, caches, deployment configuration, extension packages and optional provider-session volumes.

Scheduled retention keeps the newest verified archive from each of the most recent 14 UTC dates and eight ISO weeks by default. One archive can satisfy both daily and weekly retention. Manual archives have a separate count, defaulting to ten. Cleanup requires saved ownership and unchanged file identity and content; it preserves active downloads, unrelated files, damaged or changed archives, and archives created before ownership tracking was introduced. Existing archives remain in their folders when the destination changes. Downloading an older archive checks its contents without adopting it for automatic deletion.

The default destination is `{Storage:DataDirectory}/backups`. To use storage synchronized elsewhere, first mount its local folder into the container, then enter that existing absolute container path in **Backup folder**. Configure the synchronization outside Allstarr and protect the remote copy as carefully as the key ring. A missing, replaced or unavailable mount reports a backup failure; it does not redirect backups to another folder. Keep the live SQLite database on local disk even when archives are synchronized elsewhere.

For example, after creating the host folder with suitable private permissions, add a bind mount to your Compose override:

```yaml
services:
  allstarr:
    volumes:
      - type: bind
        source: /absolute/host/backup-sync
        target: /backup-export
        bind:
          create_host_path: false
```

Replace the source path, recreate the container with the override, then save `/backup-export` as **Backup folder**. The long bind syntax prevents Compose from silently creating a missing host folder; see [Docker's bind-mount options](https://docs.docker.com/reference/compose-file/services/#long-syntax-5). It does not configure or verify the synchronization service.

To restore, choose a backup ZIP in Settings → Backups and confirm **Restore on restart**. Uploads are limited to 1 GiB, with a maximum expanded database size of 4 GiB. Allstarr checks the archive, schema history, database integrity, and required encryption keys, then stages the files in `{Storage:DataDirectory}/restore-pending`. The running database stays unchanged until you restart Allstarr. A pending restore prevents another backup or restore from starting.

Before opening storage after restart, Allstarr preserves the current database, SQLite sidecars, and key ring in a private `pre-restore-*` directory. It then installs the staged database and key ring. Keep these previous files until you have verified your accounts, playlists, and settings; they are not removed by backup retention. Caches, media, and installed extension packages are outside the restore.

If application is interrupted, the staged files remain available for retry on the next restart. A failed application leaves durable storage unavailable with `restore_application_failed`; fix disk or permission problems and restart again. Keep the configured database and key-ring paths unchanged during recovery. An external key-ring override must be writable for restore; a read-only secret mount cannot be replaced by Allstarr. Restore paths must not use symbolic links. Do not manually remove pending files or replace individual live files after a failed application: the database and key ring must remain a matching pair.

The shell `backup` and `restore` commands are removed. Use the Backups workflow for verified database archives or a stopped-folder copy for a complete offline backup. The shell `init` and `upgrade` commands are also removed; use the dashboard setup guide and normal image update.

The encryption key ring defaults to `{Storage:DataDirectory}/keyring.json`; `Secrets__KeyRingPath` overrides that location for existing secret mounts. On first startup, an empty database gets a randomly generated key ring with private file permissions. Normal startup never overwrites an existing key ring. If encrypted secrets exist but the key ring is missing, restore the original key ring: Allstarr leaves saved credentials unavailable and continues native proxy access. It does not generate a replacement key that would make those credentials unreadable.

Audio, artwork, extension packages, and the encryption key ring remain files. Original backend music is read-only. Keep the configured encryption key ring together with any offline copy of the database: losing it makes the corresponding encrypted provider credentials unreadable. Stop Allstarr before making an offline copy of its data directory, and include any WAL sidecar files. Never copy or replace the active database piecemeal.

## Audio capacity and cleanup

**Settings → Storage → Audio limits** controls provider audio separately from the metadata, artwork and response-file caches. Changes apply to new work without a restart.

| Setting | Default | Range |
| --- | --- | --- |
| Maximum audio cache | 2 GiB | 1–1024 GiB |
| Provider streams per listener | 6 | 1–64 |
| Concurrent media conversions | 2 | 1–16 |

The audio budget includes completed cache files, in-progress fills and temporary original-download read-ahead. Original downloads use a private directory in the operating system's temporary folder, tied to the configured audio root. Leftovers from an interrupted process count toward the budget and remain protected for review. Concurrent requests for the same authorized source item share a fill; personal accounts, account revisions, quality choices and original-download purposes retain their own scopes. A listener's limit covers opening and active provider responses, including downloads, seeks and HLS segments. Native backend streaming keeps its existing proxy path.

When more space is needed, the cache removes its least recently used eligible completed files. Hourly age-based cleanup uses the same ownership and reader checks, measured from the recorded last use. A recorded filesystem identity and content checksum must still match before removal. Active readers pin their bytes through completion, publication and temporary-file retirement. Lowering the ceiling does not interrupt those readers. Kept songs, downloaded copies, original backend files, unknown files, replaced files and symbolic links are never eviction candidates. Older cache files without verified ownership remain readable and protected; their space still counts toward the ceiling. Legacy transcoded files without a current owner also remain untouched. Do not delete unfamiliar files to clear this condition. Increase the ceiling or review storage through its existing management controls.

Allstarr bounds its own audio conversion processes across playback, extension conversions and quality analysis. Saturated stream, conversion or audio-cache capacity returns HTTP **503** and **Retry-After: 5**. This local busy state does not count as a source failure or trigger source cooldown. Retrying after other responses finish normally frees capacity. A persistently full audio cache reports that its ceiling needs review. Optional external services manage their own resources separately.

## Event log and job retention

A background cleanup keeps the event log and finished playback jobs from growing the database with listening time. Each playback progress report is its own durable job; finished (succeeded or cancelled) playback jobs and their attempt events are removed once they are older than the event-log retention period. Events that another durable record references, such as a legacy import, are kept. Set these under the event-log policy in Settings:

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

Storage rows use the retained recording metadata and actual download source before falling back to file tags or filenames. New kept copies retain available track numbers and album artists; a missing number leaves out the numeric filename prefix. Existing copies show their saved title, artist, album and source without renaming or retagging the audio. Artwork uses the downloaded source, with the provider mark as fallback when artwork is unavailable.

Managed kept files and their recording associations are stored separately from personal provider download artifacts. Publication retains the source user, job, track and account revision without changing the source artifact's owner. An existing personal download from before track binding was introduced stays personal; it is never guessed into a household kept route.

Playlist retention uses the same household recording association and managed references as favorites. A published snapshot or KeepAll change saves its reference updates, obsolete-work cancellation and new retention jobs in one SQLite transaction. Workers verify the current published entry and effective external route again after preparing audio. Retention workers read the requested source entry and current route inputs directly. An old snapshot or a disabled retention option cannot publish a lasting copy. Source updates and manual rematch publication release obsolete file associations even when a provider track keeps the same reference key.

Placement uses the configured kept root and the existing journal, checksum and filesystem identity checks. A provisional reference protects interrupted placement until the current job can adopt it. Rejected publication releases its own provisional reference and removes only an unpublished, unreferenced owned file. Cleanup refuses caller-owned database transactions, changed paths and any file with a kept route or active reference. Original backend files remain untouched. Published artifact provenance is retained; the download owner removes superseded workspace bytes only after their published, managed-file and reservation dependencies are gone. Database backups do not include these audio files; preserve their configured roots separately.

Explicit Keep requests use the same verified household recording placement and durable reference owner as hearts and playlist retention. The cache index identifies the requested track. The worker downloads and verifies audio before publication rather than treating legacy cached bytes as verified provenance. Release Keep never deletes a household file directly and affects only the requesting administrator’s explicit reference. The final reference release queues the standard twenty-four-hour cleanup.

**Delete kept copy…** is a separate administrator action in Library → Kept. Its confirmation lists the current favorites, playlist references and Keeps, then queues deletion of the household audio and its unchanged lyrics sidecar. Hearts and playlist entries stay, so the song can stream or download into temporary cache when played online. Existing automatic retention requests cannot immediately recreate the deleted kept copy; a new explicit Keep can save it again. Changed files, references or administrator access invalidate the confirmation. Activity reports the result, including any changed lyrics left for inspection. Ordinary **Release Keep** keeps its twenty-four-hour grace period.

Kept playback checks the recorded root, path, length and filesystem identity on the opened read handle, rejecting symbolic links and replaced files without hashing the entire track on each stream, seek or HEAD request. Publication, ordinary ownership lookup and removal retain full content verification. Playback refuses kept files without a recorded filesystem identity and can use an eligible provider instead.

If a kept file is missing, damaged or replaced, a new heart, Keep or retained-playlist download can publish a verified replacement at a fresh path. Existing interests move with the recording routes in the same transaction. The old ownership record is retired and recorded in history. Allstarr leaves changed files and symbolic links at the old path untouched; inspect those paths before removing them manually. An available verified copy of the same recording can be reused instead of another placement.

A database that has retained superseded download artifacts cannot be migrated back to the earlier single-artifact schema without losing publication or reservation history. That downgrade is rejected before changing the schema. To return to an earlier version, restore a backup made before the upgrade using the normal recovery procedure.

Personal source use modes extend the existing runtime settings allowlist and preserve older settings during upgrade. An in-place downgrade is rejected while personal source modes are saved, so returning to an older version requires the matching pre-upgrade backup and image.

## Library promotion API

Promotion is available through authenticated admin-port APIs; there is no dashboard promotion flow yet. An administrator first configures `Promotion:AllowedBase` and registers existing writable subdirectories with `PUT /api/admin/library-promotions/roots` (`id`, `name`, `path`). Roots must stay inside that base and cannot traverse symbolic links. `GET` on the same route returns root IDs and names.

Use `POST /api/admin/library-promotions/preview` with `recordingId`, `artifactId`, `rootId`, `backendLibraryId`, and `requestKey` to review the destination, size, and steps for a complete verified original. `POST /api/admin/library-promotions` starts the durable copy, metadata/cover application, quality screen, owned placement, and backend refresh request. The original source and existing backend library files remain unchanged. Reusing the same request key with the same request returns the existing run.

Inspect `GET /api/admin/library-promotions/{id}`. A suspicious or inconclusive quality result pauses at **Needs review**; the owner or an administrator can give an explicit `reason` to `POST /{id}/approve`. `POST /{id}/cancel` requests cancellation. Library access and source ownership are checked again during work; an approval does not bypass those checks. Preserve the approved media root alongside database backups.

## Imported original media

A version 2 upgrade can retain its original kept and permanent-download files in place. New managed files use an `Allstarr` subfolder within each existing persistent media mount, so a backend library already watching the kept mount continues to see newly kept songs. The original directories and files are not renamed. Import previews exclude exact files already owned by the new managed-file store rather than excluding a folder by name. SQLite records the exact root, path, filesystem identity, size and checksum, together with the administrator's import receipt. These originals have permanent protected references. Playback, lyrics and downloads read them; cleanup, Remove and Release Keep do not delete or retarget them. Existing lyrics sidecars are preserved, and Allstarr does not create sidecars beside an imported original.

A verified database-and-key-ring backup includes the ownership proof but excludes the audio. Stop Allstarr before copying its data folder and all retained original media roots, then restore the same container paths on the new host. After a stopped copy, Allstarr recognizes the new data folder and queues verification of already owned files. It checks changed filesystem identities against their saved contents before rebinding ownership. Missing mounts, changed bytes and symbolic links stay unavailable for review. Keep the stopped version 2 folders until the upgrade is verified. Going back to version 2 uses its untouched environment and original folders; rolling back a version 3 database uses the matching older backup and image. A populated original-media schema refuses a downgrade that would discard its protections.

## Verifying a copied folder

Keep the container paths unchanged when moving data, especially the original `/app/kept` and `/app/downloads` mounts in an upgraded installation. Copy the complete stopped folder, including `.allstarr-storage`, SQLite sidecars and the key ring. Do not edit the marker or copy individual live database files. The same image recognizes the copy and records **Verify saved files** as durable work in Activity. Health and native proxying remain available; other queued work resumes after verification. Already verified files become readable as the work proceeds. Playback retains its normal opened-file identity checks and does not rehash the entire song on each request.

Verification reads only paths already owned by the database. It never adopts arbitrary neighboring files or changes audio, lyrics, references, account revisions, credential versions or the immutable original import provenance. Hashing runs outside database writers. Each identity update checks the current job, storage generation and exact saved file revision in one transaction. Interrupted work resumes and skips identities already verified. An interrupted legacy import retains files already adopted; unverified originals whose identity changed need a new preview.

If a file needs review, restore its original bytes and path, then choose **Verify saved files** in Settings → Storage. The same action is available through `POST /api/admin/storage/files/verify`. The authenticated status API at `GET /api/admin/storage/files` returns progress and an antiforgery token; verification requires that token and an active administrator. Repeated requests reuse pending work. Missing or unsafe files remain protected and unavailable while other verified files stay usable. Per-file verification details use the shorter job-history retention policy so large moves do not crowd out administrator events.

The populated copy-recovery schema cannot be downgraded in place. Use the matching older image and a backup from before the upgrade. Database-and-key-ring ZIPs still exclude audio; preserve the original media roots or use a complete stopped-folder copy when relocating music as well.
