# Legacy `.env` import

Version 3 uses an explicit administrator import to carry safe configuration from a 2.x deployment into a fresh SQLite installation. Startup does not apply a legacy file automatically. In an unchanged version 2 container layout, the database and encryption key ring are created under the persistent `/app/cache/allstarr-v3` mount. New kept and permanent-download placement stays in the existing persistent media mounts, under an `Allstarr` subfolder of their configured roots (normally `/app/kept/Allstarr` and `/app/downloads/Allstarr`). This keeps new songs visible to libraries that already mount the kept folder while separating them from imported originals. If only the downloads mount exists, kept placement uses `/app/downloads/kept/Allstarr`. SQLite, the key ring and cache use the cache mount; an absent media mount falls back to the persistent data directory. The old client and administration ports, administration bind/trust environment names, and `/health` remain supported. Native backend requests continue while setup is pending. A container with neither a persistent data mount nor the compatible cache mount refuses to start.

## Supported workflow

1. Back up and stop the version 2 deployment.
2. Create a separate version 3 deployment with an empty local data folder; startup creates its database and key ring.
3. Finish version 3 onboarding and sign in as an administrator.
4. Open the legacy `.env` migration in Settings and upload or paste the old file.
5. Review the preview, conflicts, obsolete keys, deployment-only values, accounts, backend identity, and playlists.
6. Confirm and apply the preview before it expires.
7. Review each connection result. Successful imported accounts are enabled; reconnect or retest failed accounts in Integrations before enabling them.
8. Review imported playlist links and schedules. Existing target IDs and schedules are preserved; source order replaces the old first/last rule. Resolve any missing account or target through the playlist workflow.
9. Run readiness checks and create and download a verified backup in Settings → Maintenance before cutting clients over.

Version 2 and version 3 must never write the same cache, download, kept, or managed-library root at the same time.

## Ownership rules

The parser classifies recognized input rather than copying arbitrary keys.

| Class | Result |
| --- | --- |
| Deployment bootstrap | Report for manual review; never change the running image, backend, listener, database, key ring, CORS, or mount policy. |
| Durable non-secret setting | Validate and add only when the target setting is absent. |
| Shared provider credential | Store encrypted after confirmation; test the connection before enabling. Failed or unchecked connections stay disabled with recovery instructions. |
| User account | Attach only to the signed-in administrator; enable only after a successful connection check. |
| Backend identity | Create only for the signed-in administrator when the legacy Jellyfin principal and backend instance are explicit. |
| Spotify playlist | Restore a durable link and schedule for the verified administrator and existing target. Use source order and preserve manual target entries. Missing prerequisites remain review items. |
| Extension registry | Import reviewed HTTPS addresses disabled, without fetching or installing packages. Enable a registry explicitly in Integrations. |
| Obsolete or unknown | Report and leave behind. |

The exact recognized aliases and dispositions are owned by `allstarr/Core/Configuration/LegacyEnvParser.cs`. Validation and durable application are owned by `LegacyEnvMigrationService.cs`. Tests in `allstarr.Tests/Configuration/LegacyEnvParserTests.cs` and `allstarr.Tests/Configuration/LegacyEnvMigrationServiceTests.cs` are the executable contract.

Legacy Redis/Valkey, SQLite, mapping-file, cache-file, AIO, and Compose-overlay values are recognized only so the preview can explain that they are obsolete. Their data is not imported as runtime state.

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
- It does not grant every user access to an imported shared source.
- It does not preserve a rollback database inside the new runtime. Keep the stopped version 2 deployment separately until verification is complete.

SquidWTF is retired. No replacement source or account is selected automatically; choose a source in Integrations. The old Apple AIO URL is left behind and links to optional Apple setup. Retired Spotify cache settings, obsolete concurrency flags, and Valkey configuration are reported rather than copied into active settings. Credential verification runs before opening the import writer transaction, and the administrator and current account/settings state are rechecked before persistence.

## Original media

The preview inventories audio under the approved legacy mounts, including nested kept folders. It reports identified files, files that need identification, missing mounts and skipped links or temporary cache folders. A preview changes neither the database nor the originals. One preview accepts at most 10,000 audio files; select smaller mounted subfolders if the inventory exceeds that limit.

Apply saves the reviewed inventory with the import receipt and queues **Import legacy files** in Activity. The worker verifies each original file and records ownership in place. It does not copy, move, retag or delete audio, create missing lyrics sidecars, or import pending deletions. Interrupted work resumes and skips files already adopted. A changed file is left untouched and reported for review; create another preview after checking the mounts. Repeating an unchanged preview does not duplicate files, accounts, schedules or settings.

Known provider IDs in version 2 filenames retain their external IDs and can serve household playback without the former provider account. Unidentified audio remains protected in the file inventory without an invented song association. Kept lists imported originals with a read-only status and download action. Existing sibling lyrics remain readable. Original ownership and its permanent reference cannot be released through removal or cleanup actions.

Database backups preserve the import receipts and ownership proof, while original audio stays in its existing mounts. Include those folders in an offline copy and keep their container paths stable. See [storage operations](storage.md) before moving or rolling back an upgraded installation.
