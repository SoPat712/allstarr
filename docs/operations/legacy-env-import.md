# Legacy `.env` import

Version 3 brings safe settings from a 2.x `.env` over through an import that an administrator previews and confirms. Startup never applies an old file by itself. For the upgrade steps, mounts and folder layout, see [upgrade from version 2](deployment-profiles.md#upgrade-from-version-2).

## Supported workflow

1. Stop version 2 and back up its configuration and persistent folders. Follow [the upgrade procedure](deployment-profiles.md#upgrade-from-version-2) with the existing Compose file and mounts.
2. Start version 3. It creates the database and key ring in the persistent data or cache mount.
3. Sign in as a media-server administrator.
4. Open the setup guide's **Review previous settings** step. You can reopen the guide from **Settings → Backups and health → Setup guide**. The **Import from Allstarr v2** card previews the mounted `.env`, or a copy you upload or paste (up to 1 MiB).
5. Review the preview: conflicts, obsolete keys, deployment-only values, accounts, media-server identity and playlists.
6. Confirm the import before the preview expires.
7. Review each connection result. Imported accounts that pass their connection check are enabled. Reconnect or retest the others in **Sources → Accounts** before you turn them on.
8. Review imported playlist links and schedules. Existing target playlists and schedules are kept; source order replaces the old first/last rule. Fix any missing account or target from the playlist.
9. Check readiness and playback, then create and download a verified backup in **Settings → Backups and health**.

Version 2 and version 3 must never write the same cache, download, kept or library folder at the same time.

Importing settings marks that one setup step as reviewed; it doesn't finish the whole guide.

## What is imported

The import recognises known settings; it never copies unknown keys.

| Kind of value | Result |
| --- | --- |
| Deployment values | Ports, image, storage, key ring and mount settings are shown for review. The active media-server connection, already verified, is saved once. Imported values never switch servers. |
| Ordinary settings | Checked and added only when the setting has no saved value yet. |
| Shared service credentials | Stored encrypted after you confirm, then tested. Accounts that fail or can't be checked stay off, with steps to fix them. |
| Personal service credentials | Attached only to the signed-in administrator and turned on only after a successful connection check. |
| Media-server identity | Taken from the verified signed-in administrator. Imported user IDs never grant access. |
| Spotify playlists | Restored as a link and schedule for the signed-in administrator and an existing target playlist, using source order and keeping manual entries. Missing pieces become review items. |
| Extension registries | Imported off, without fetching or installing anything. Turn a registry on in **Sources → Services**. |
| Obsolete or unknown | Reported and left behind. |

The full list of recognised names is in `allstarr/Core/Configuration/LegacyEnvParser.cs`.

Old storage-engine, Redis/Valkey, mapping-file, cache-file, all-in-one and Compose-overlay settings are reported, not imported. The import does not turn an older database or cache into current data.

## Safety

- An empty value means "not set"; it never clears a saved setting.
- Saved settings and accounts win over imported values.
- A media-server type or address that doesn't match blocks the import; it never switches the active connection.
- An empty duplicate line doesn't erase an earlier value. The preview shows duplicate lines.
- A preview is short-lived and tied to the administrator session that made it.
- Secret values never appear in the preview, logs, jobs, audit records or command output.
- Running the same import again changes nothing: no duplicate accounts, identities, links, schedules or settings.
- Personal Spotify access needs a current account connection; old shared credentials are not treated as a signed-in user.
- Old single sign-on values are shown for review only. Set up single sign-on yourself in **Settings → People and sign-in**.
- Sensitive values are always redacted in request logs; the old switch that turned redaction off is never applied.

## What import does not do

- It does not switch between Jellyfin and Subsonic.
- It does not copy media or provider-session volumes.
- It does not turn old mapping JSON or cache keys into matches.
- It does not keep the old "local tracks last" behaviour. The preview explains that source order now applies.
- Imported Shared accounts follow the normal household rules. They don't grant media-server library access or access to private playlists.
- It does not keep a rollback copy of the database. Keep the stopped version 2 deployment until you have checked everything.
- It never edits the old `.env` file.

SquidWTF is retired. No replacement is chosen for you; pick your services in **Sources → Services**. The old Apple all-in-one URL is left behind and points to the optional [Apple setup](apple-download-provider.md). Retired Spotify cache settings, old concurrency flags, Valkey settings, retired MusicBrainz sign-in fields and the old Jellyfin client username are reported, not copied.

## Original media

The preview lists audio under the approved old mounts, including nested kept folders. It reports identified files, files that need identifying, missing mounts, and skipped links or temporary cache folders. A preview changes neither the database nor the files. One preview accepts at most 10,000 audio files; choose smaller mounted subfolders if you have more.

Applying the import queues **Import legacy files** in Activity. The job checks each original file and records it where it is. It does not copy, move, retag or delete audio, create lyrics files, or import pending deletions. An interrupted job resumes and skips files already recorded. A file that changed is left alone and reported; check the mounts and make a new preview.

Song IDs from services in version 2 file names are kept, so those files can play for the household without the old service account. Files that can't be identified stay protected without a guessed song. Library → Kept shows imported originals as read-only, with a download action. Existing lyrics files next to them still work. Removal and cleanup actions never delete or release an imported original.

Database backups keep the record of imported files, but the audio stays in its original mounts. Include those folders in any offline copy and keep their container paths. See [storage operations](storage.md#imported-version-2-media) before you move or roll back an upgraded installation.

## The media-server connection

The preview shows the active media server and whether it is already saved. Importing saves its type, address and verified identity only when none is saved. An old Jellyfin API key is checked against the importing administrator, then stored encrypted for that exact server. If the check fails, media-server access stays off for review; music apps keep working.

New connection or access values take effect when Allstarr restarts after the import. Running the import again returns the earlier result without restarting. After that, changing or removing old environment values does not replace the saved connection or imported settings.

Library selection, request logging, metadata-service and browser-access settings are imported too, checked by the same rules as the settings pages. Credentialed browser access needs explicit origins.
