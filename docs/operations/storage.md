# Storage operations

Allstarr keeps its database, encryption key ring and managed music in one data folder. The database is SQLite at `/app/data/allstarr.db`; no database container is needed. Run one Allstarr process per data folder and keep it on local disk. Allstarr refuses a database folder on a network filesystem such as NFS or SMB.

## The data folder

Compose mounts `./data` at `/app/data`, with or without `.env`. Set `ALLSTARR_DATA_PATH` to use another local folder or a named volume. An unchanged version 2 layout keeps its data elsewhere; see [upgrade from version 2](deployment-profiles.md#upgrade-from-version-2). First startup creates the database and key ring.

| In the data folder | Contents |
| --- | --- |
| `allstarr.db`, `allstarr.db-wal`, `allstarr.db-shm` | Database and its live SQLite files |
| `keyring.json` | Keys for saved passwords, tokens and keys |
| `data-protection/` | Dashboard sign-in protection keys |
| `.allstarr-storage` | Marker used to recognise a copied data folder |
| `downloads/`, `kept/` | Downloaded and kept music |
| `extensions/` | Installed extension packages |
| `apple-upload/` | Optional staged Apple APK/APKM packages |
| `cache/` | Song cache and temporary download and import space |
| `backups/` | Verified database-and-key-ring archives |
| `restore-pending/`, `pre-restore-*/` | A staged restore, and the files it replaced |

The key ring, download and kept folders can live outside the data folder through [container overrides](configuration.md#container-overrides). Include those paths when you back up or move an installation. Optional Apple gateway and wrapper sessions remain in their own profile volumes.

The database also holds problem reports. They are included in backups and never sent to an outside service.

Original media-server music is only read, never changed.

## Song cache and kept songs

**Settings → Storage and speed → Song cache** controls songs played from services:

- **Cache played songs** is on by default. Cached songs start faster next time.
- **Keep cached songs for (hours)** defaults to 72 hours after the last play. The size limit can remove songs sooner.
- **Maximum audio cache (GiB)** defaults to 2 GiB.

When the cache is full, the least recently used songs that nobody is playing are removed first. An hourly cleanup removes songs past their age. **Library → Cached** lists cached songs and links to these settings.

Turning **Cache played songs** off stops caching ordinary playback; requested downloads then go to Kept. It does not keep every played song.

Hearts, **Keep** and retained playlists protect their own copies in Kept, whatever the cache does. **Stop keeping** removes only your own Keep. When the last reason to keep a song goes away, its copy is deleted after 24 hours. An administrator's **Delete kept copy…** in Library → Kept deletes the household copy without that wait. Its confirmation lists what still refers to the song; hearts and playlist entries stay, and the song streams again when played. Back up kept music separately from database archives.

## Audio capacity and cleanup

These limits apply to new work as soon as you save them.

| Setting | Where | Default | Range |
| --- | --- | --- | --- |
| Maximum audio cache (GiB) | Song cache | 2 GiB | 1 to 1024 |
| Provider streams per listener | Speed | 6 | 1 to 64 |
| Concurrent media conversions | Speed | 2 | 1 to 16 |

The audio limit covers finished cache files, songs still filling, and temporary read-ahead for original downloads. It is separate from the metadata, artwork and response caches. Two listeners asking for the same song from the same account and quality share one download.

A listener's stream limit counts opening and active service streams, including downloads, seeks and HLS segments. Media-server streams don't count.

Cleanup never removes songs that are being played, kept songs, downloads, your media server's files, files it doesn't recognise, changed files or symbolic links. Older cache files without a verified owner stay readable and still count towards the limit. Do not delete unfamiliar files to free space; raise the limit or use the cache controls under **Advanced: cache times, file checks and cleanup**.

When stream, conversion or cache capacity is full, Allstarr answers HTTP 503 with `Retry-After: 5`. This is a local busy state: it doesn't count as a service failure and frees up as other responses finish. A cache that stays full is reported as needing a higher limit.

## Backups

Set up backups in **Settings → Backups and health**.

| Setting | Default |
| --- | --- |
| Daily backups | On |
| Backup time (UTC) | 03:00 |
| Daily copies | 14 (1 to 90) |
| Weekly copies | 8 (0 to 52) |
| Manual copies | 10 (1 to 1000) |
| Backup folder | Empty: the data folder's `backups/` |

The status card shows the last verified backup, its size, the next run and any failure. **Back up now** queues the work; it is not done until the status says so. Work survives a restart, and overlapping requests don't start a second backup. A missed schedule runs once after recovery. Schedule changes apply without a restart.

Each backup is a private ZIP with a consistent database snapshot, the key ring, and a manifest with checksums. Creating it checks the database and keys; downloading it checks the checksums again. Keep downloaded archives private: the database and key ring together unlock saved credentials. Backups do not include music, caches, deployment files, extension packages or optional service volumes.

Retention keeps the newest backup from each of the last 14 UTC days and 8 ISO weeks, plus the 10 newest manual backups. One file can count as both daily and weekly. Cleanup deletes only backups Allstarr made and that are unchanged; it keeps damaged or changed files, unrelated files and backups from earlier versions.

### Back up to synced storage

To keep backups on synced storage, mount its local folder into the container, then enter that existing absolute container path as **Backup folder**. Set up the sync outside Allstarr and protect the remote copy as carefully as the key ring. A missing or replaced mount is reported as a backup failure; Allstarr never falls back to another folder. Keep the live database on local disk.

For example, after creating the host folder with private permissions, add a bind mount to a Compose override:

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

Recreate the container, then save `/backup-export` as **Backup folder**. The long syntax stops Compose from silently creating a missing host folder; see [Docker's bind-mount options](https://docs.docker.com/reference/compose-file/services/#long-syntax-5).

## Restore a backup

In **Settings → Backups and health**, choose a backup ZIP and confirm **Restore on restart**. Uploads are limited to 1 GiB, with at most 4 GiB of expanded database. Allstarr checks the archive, its schema history, the database and the keys, then stages the files in `restore-pending/`. Nothing changes until you restart Allstarr. While a restore is pending, no other backup or restore can start.

On restart, Allstarr moves the current database and key ring to a private `pre-restore-*` folder, then installs the staged pair. Keep that folder until you have checked accounts, playlists and settings; backup retention never removes it. Caches, music and extension packages are not part of a restore.

If the restore is interrupted, it is retried at the next restart. If it fails, storage stays unavailable with `restore_application_failed`; fix the disk or permission problem and restart again. Keep the database and key-ring paths unchanged while you recover, and don't remove pending files or swap individual files by hand: the database and key ring must stay a matching pair. A key ring on a read-only mount can't be replaced, and restore paths must not be symbolic links.

## The key ring

The key ring is `keyring.json` in the data folder unless `Secrets__KeyRingPath` points elsewhere. First startup on an empty database creates it with private permissions. Startup never overwrites an existing key ring.

Keep the key ring with every copy of the database. Without it, saved credentials can't be read. If encrypted credentials exist but the key ring is missing, restore the original key ring: Allstarr leaves those credentials unavailable and keeps music apps working, rather than making a new key that would lock them out for good.

## Offline folder backup and restore

A complete copy of the stopped data folder is the full backup: database, key ring, sign-in keys and music. With the default layout:

```bash
docker compose stop allstarr
cp -a ./data ../allstarr-data-backup
docker compose up -d --no-deps allstarr
```

Use a new destination each time. Copy with permission to read every private file (`sudo cp -a` for root-owned Docker data), and keep the permissions. Check that the copy includes the database and its `-wal` and `-shm` files, the key ring and the music. Copy deployment files, outside music folders and optional Apple volumes separately; stop each optional service before copying its state.

To restore, stop Allstarr, set the current folder aside, and put the verified copy back at the configured host path. Use the same image version and container paths. Start only one installation, then check readiness, accounts, playlists, settings and playback. Never copy or replace the live database file by file.

To roll back to an earlier image, restore the folder backup made before the upgrade, then start that image. Never run an older image against a database a newer version has migrated. See [deployment and rollback](deployment-profiles.md#backup-restore-and-rollback).

## Verifying a copied folder

Keep the container paths unchanged when you move data, especially `/app/kept` and `/app/downloads` in an upgraded version 2 installation. Copy the whole stopped folder, including `.allstarr-storage`, the SQLite files and the key ring. Don't edit the marker.

At startup the same image recognises the copy and queues **Verify saved files** in Activity. Health checks and music apps keep working. Each saved file is checked against its recorded contents before Allstarr trusts its new location; other queued work resumes afterwards. Missing, changed or linked files stay unavailable for review while the rest stay usable. Verification never adopts other files and never changes audio or lyrics.

If a file needs review, restore its original bytes and path, then choose **Verify saved files** under **Saved-file verification** in Settings → Storage and speed → **Advanced: cache times, file checks and cleanup**. Follow progress in Activity. Keep the original stopped installation until verification and playback pass.

## Database readiness

At startup Allstarr turns on SQLite write-ahead logging, applies pending migrations and runs an integrity check before storage is ready. An unknown schema version or a damaged database leaves stored features unavailable while music apps keep reaching the media server. Startup never replaces damaged data or switches to another database.

## Event log and job retention

Activity history is trimmed in the background so the database doesn't grow with listening time. Set it in **Settings → History and scrobbling**. Changes restart Allstarr.

| Setting | Default | Range |
| --- | --- | --- |
| Activity retention days | 30 | 1 to 3650 |
| Maximum activity rows | 250000 | 1000 to 5000000 |
| Job history retention days (under Advanced) | 7 | 1 to 3650, never longer than activity retention |
| Activity cleanup interval in minutes (under Advanced) | 360 | 5 to 10080 |
| Activity cleanup batch size (under Advanced) | 1000 | 100 to 10000 |

Each playback progress report is a short job. Finished playback jobs and their history are removed once they pass the activity retention age. Events that another record depends on, such as a legacy import, are kept. A cleanup pass also runs at startup.

After a restart, jobs that were interrupted are queued again with the same retry budget. A job interrupted five times in a row without finishing is marked failed (`worker_interrupted_repeatedly`), so work that crashes Allstarr can't loop forever. Up to four jobs run at once.

## Adding songs to the media server library

Approve a writable Jellyfin music folder in **Settings → Library → Where added songs go**, then use **Add to library** on a song, or **Library → To library**. If Jellyfin and Allstarr see the folder under different paths, map the media-server path to its path inside the Allstarr container. Approval rejects symbolic links and checks write access. `Promotion__AllowedBase` is an optional [container override](configuration.md#container-overrides) that limits where added songs may go; it is not needed.

Library permissions and approval apply to each request and again while the work runs. Quality checks can pause a run for review. Existing media-server files are never changed. Back up approved library folders separately from database archives.

## Imported version 2 media

A version 2 upgrade can keep its original kept and downloaded files in place. New files go into an `Allstarr` subfolder of each existing media mount, so a media server already watching the kept folder sees new songs. Originals are never renamed, moved, retagged or deleted, and Allstarr adds no lyrics files beside them. See [legacy import](legacy-env-import.md#original-media).

Database backups include the record of which files Allstarr owns, but not the audio. When you move such an installation, copy the stopped data folder and every original media folder, and keep the same container paths. Keep the stopped version 2 folders until the upgrade is verified.
