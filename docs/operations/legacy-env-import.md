# Legacy `.env` import

Version 3 uses an explicit administrator import to carry safe configuration from a 2.x deployment into a fresh SQLite installation. Startup does not apply a legacy file automatically. In an unchanged version 2 container layout, the database and encryption key ring are created under the persistent `/app/cache/allstarr-v3` mount. New kept and permanent-download placement stays in the existing persistent media mounts, under an `Allstarr` subfolder of their configured roots (normally `/app/kept/Allstarr` and `/app/downloads/Allstarr`). This keeps new songs visible to libraries that already mount the kept folder while separating them from imported originals. If only the downloads mount exists, kept placement uses `/app/downloads/kept/Allstarr`. SQLite, the key ring and cache use the cache mount; an absent media mount falls back to the persistent data directory. The old client and administration ports, administration bind/trust environment names, and `/health` remain supported. Native backend requests continue while setup is pending. A container with neither a persistent data mount nor the compatible cache mount refuses to start.

## Supported workflow

1. Stop version 2 and back up its configuration and persistent folders. Follow [the upgrade procedure](deployment-profiles.md#upgrade-from-version-2) with the existing Compose file and mounts.
2. Start version 3. It creates SQLite and the key ring in the persistent data or compatible cache mount.
3. Sign in as a media-server administrator.
4. Open **Settings → Setup → Review previous settings** and preview the mounted `.env`, or upload or paste a copy.
5. Review the preview, conflicts, obsolete keys, deployment-only values, accounts, backend identity, and playlists.
6. Confirm and apply the preview before it expires.
7. Review each connection result. Successful imported accounts are enabled; reconnect or retest failed accounts in Integrations before enabling them.
8. Review imported playlist links and schedules. Existing target IDs and schedules are preserved; source order replaces the old first/last rule. Resolve any missing account or target through the playlist workflow.
9. Check readiness and playback, then create and download a verified backup in **Settings → Backups**.

Version 2 and version 3 must never write the same cache, download, kept, or managed-library root at the same time.

## Ownership rules

The parser classifies recognized input rather than copying arbitrary keys.

| Class | Result |
| --- | --- |
| Deployment bootstrap | Report listeners, image, storage, key ring and mount policy for review. Save the already verified active backend connection once; never switch servers from imported input. |
| Durable non-secret setting | Validate and add only when the target setting is absent. |
| Shared provider credential | Store encrypted after confirmation; test the connection before enabling. Failed or unchecked connections stay disabled with recovery instructions. |
| User account | Attach only to the signed-in administrator; enable only after a successful connection check. |
| Backend identity | Use the verified signed-in administrator and its existing backend instance. Imported user IDs never grant authority. |
| Spotify playlist | Restore a durable link and schedule for the verified administrator and existing target. Use source order and preserve manual target entries. Missing prerequisites remain review items. |
| Extension registry | Import reviewed HTTPS addresses disabled, without fetching or installing packages. Enable a registry explicitly in Integrations. |
| Obsolete or unknown | Report and leave behind. |

The exact recognized aliases and dispositions are owned by `allstarr/Core/Configuration/LegacyEnvParser.cs`. Validation and durable application are owned by `LegacyEnvMigrationService.cs`. Tests in `allstarr.Tests/Configuration/LegacyEnvParserTests.cs` and `allstarr.Tests/Configuration/LegacyEnvMigrationServiceTests.cs` are the executable contract.

Legacy storage-engine selectors, Redis/Valkey, mapping-file, cache-file, AIO and Compose-overlay settings are reported rather than imported. SQLite is the current durable store; the import does not convert an older database or cache into authoritative state.

## Safety

- Empty input means absent, not clear an existing target.
- Existing durable settings and accounts win over imported values.
- A backend type or address mismatch blocks apply; the import never switches the active connection.
- Empty duplicate assignments do not erase earlier non-empty values; the preview identifies duplicate lines.
- A preview is short-lived and bound to the administrator session that created it.
- Secret values never appear in the preview, logs, jobs, audit metadata, or command output.
- Import is idempotent and records lineage; replay must not duplicate accounts, identities, links, schedules, or settings.
- Spotify personal access requires a current account connection rather than treating old shared credentials as a signed-in user.

## What import does not do

- It does not switch Jellyfin and Subsonic backends.
- It does not copy media or provider-session volumes.
- It does not convert old mapping JSON or cache keys into authoritative matches.
- It does not retain the old `local tracks last` behavior. The preview explains that source order now applies.
- Imported Shared accounts follow the normal household capability policy. They do not grant backend library access or access to private playlists.
- It does not preserve a rollback database inside the new runtime. Keep the stopped version 2 deployment separately until verification is complete.

SquidWTF is retired. No replacement source or account is selected automatically; choose a source in Integrations. The old Apple AIO URL is left behind and links to optional Apple setup. Retired Spotify cache settings, obsolete concurrency flags, and Valkey configuration are reported rather than copied into active settings. Credential verification runs before opening the import writer transaction, and the administrator and current account/settings state are rechecked before persistence.

## Original media

The preview inventories audio under the approved legacy mounts, including nested kept folders. It reports identified files, files that need identification, missing mounts and skipped links or temporary cache folders. A preview changes neither the database nor the originals. One preview accepts at most 10,000 audio files; select smaller mounted subfolders if the inventory exceeds that limit.

Apply saves the reviewed inventory with the import receipt and queues **Import legacy files** in Activity. The worker verifies each original file and records ownership in place. It does not copy, move, retag or delete audio, create missing lyrics sidecars, or import pending deletions. Interrupted work resumes and skips files already adopted. A changed file is left untouched and reported for review; create another preview after checking the mounts. Repeating an unchanged preview does not duplicate files, accounts, schedules or settings.

Known provider IDs in version 2 filenames retain their external IDs and can serve household playback without the former provider account. Unidentified audio remains protected in the file inventory without an invented song association. Kept lists imported originals with a read-only status and download action. Existing sibling lyrics remain readable. Original ownership and its permanent reference cannot be released through removal or cleanup actions.

Database backups preserve the import receipts and ownership proof, while original audio stays in its existing mounts. Include those folders in an offline copy and keep their container paths stable. See [storage operations](storage.md) before moving or rolling back an upgraded installation.

## Setup progress and mounted previews

Importing settings records the import step; it does not finish the entire setup guide. The shared setup engine keeps reviewed and skipped steps separately for each administrator. Finishing explicitly records remaining optional steps as skipped. Reopening preserves that progress. Updates compare the saved revision, so a stale browser tab must reload before changing another step. Disabled or demoted administrators cannot save or reopen setup.

The administration API exposes the shared checklist at `GET /api/admin/onboarding/status` and accepts `stepId`, `action` (`review`, `skip`, or `reset`) and `expectedRevision` at `POST /api/admin/onboarding/step`. The verified media-server identity is required and cannot be skipped.

When the old environment is still mounted, migration status reports its availability without returning its contents. `POST /api/admin/config/migration/preview-mounted` reads that file with the same 1 MiB limit and creates the same session-bound preview as an upload. Symbolic links, missing files and oversized inputs are rejected. Reading or previewing never edits the file or applies its values. Confirmation through the existing apply endpoint is still required.

## Saving the active backend

The connection preview identifies the active server and whether it is already saved. Apply stores its type, address and verified instance identity only when absent. A legacy Jellyfin service key is checked against the importing administrator outside the database writer, then encrypted for that exact backend address and credential purpose. Failed or unavailable checks leave service access inactive for review; native proxying can still use the saved connection. Existing encrypted access and its versions are preserved.

New connection or service-access values take effect after Allstarr restarts once the successful response has completed. A replay returns the existing receipt without checking credentials again or requesting another restart. Afterward, changing or removing old environment values does not replace the saved connection or imported household settings.

The current import receipt format adds this connection snapshot. An older receipt remains intact; explicitly previewing the same source again can add missing connection settings under the new format while preserving settings and accounts already imported. No original input, receipt, audio, account revision or encrypted version is rewritten.

Browser access policy, library selection, request logging, metadata-service settings and other supported operator policy are durable settings too. Preview uses the same validators as the settings editor, including URL restrictions and the explicit-origin requirement for credentialed browser access. New startup policy values request a restart after the apply response. Neither preview nor apply writes the old environment file.

Retired MusicBrainz login fields and the old Jellyfin client username are left behind. The unsafe request-redaction switch is never applied: sensitive values are always redacted. Legacy SSO values require explicit review and configuration through the encrypted administrator-access owner; they do not enable a foreign identity service during import.

A stopped data-folder copy can resume already adopted media through the saved ownership proof after its contents are verified. If a legacy import was interrupted before some files were adopted, changed filesystem identities for those unverified originals require a fresh preview. Existing adopted references and all original bytes remain protected.
