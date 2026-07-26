# Legacy `.env` import

Version 3 uses an explicit administrator import to carry safe configuration from a 2.x deployment into a fresh PostgreSQL-backed installation. Startup never scans or applies a legacy file automatically.

## Supported workflow

1. Back up and stop the version 2 deployment.
2. Create a separate version 3 deployment, PostgreSQL database, key ring, cache, and writable media roots.
3. Finish version 3 onboarding and sign in as an administrator.
4. Open the legacy `.env` migration in Settings and upload or paste the old file.
5. Review the preview, conflicts, obsolete keys, deployment-only values, accounts, and playlist handoffs.
6. Confirm and apply the preview before it expires.
7. Test imported accounts and enable sharing explicitly where appropriate.
8. Recreate or reconcile playlists through the provider-neutral playlist workflow.
9. Run readiness checks and take a PostgreSQL plus key-ring backup before cutting clients over.

Version 2 and version 3 must never write the same cache, download, kept, or managed-library root at the same time.

## Ownership rules

The parser classifies recognized input rather than copying arbitrary keys.

| Class | Result |
| --- | --- |
| Deployment bootstrap | Report for manual review; never change the running image, backend, listener, database, key ring, CORS, or mount policy. |
| Durable non-secret setting | Validate and add only when the target setting is absent. |
| Shared provider credential | Create encrypted and disabled by default after explicit acknowledgement. |
| User account | Attach only to the signed-in administrator when ownership is unambiguous. |
| Obsolete or unknown | Report and leave behind. |

The exact recognized aliases and dispositions are owned by `allstarr/Core/Configuration/LegacyEnvParser.cs`. Validation and durable application are owned by `LegacyEnvMigrationService.cs`. Tests in `allstarr.Tests/LegacyEnvParserTests.cs` and `LegacyEnvMigrationServiceTests.cs` are the executable contract.

Legacy Redis/Valkey, SQLite, mapping-file, cache-file, AIO, and Compose-overlay values are recognized only so the preview can explain that they are obsolete. Their data is not imported as runtime state.

## Safety

- Empty input means absent, not clear an existing target.
- Existing durable settings and accounts win over imported values.
- A preview is short-lived and bound to the administrator session that created it.
- Secrets may appear only in the authenticated preview and must not enter logs, jobs, audit metadata, or command output.
- Redact for sharing before screenshots or support exports.
- Import is idempotent and records lineage; replay must not duplicate accounts or settings.
- Spotify personal access requires a current account connection rather than treating old shared credentials as a signed-in user.

## What import does not do

- It does not switch Jellyfin and Subsonic backends.
- It does not copy media or provider-session volumes.
- It does not convert old mapping JSON or cache keys into authoritative matches.
- It does not grant every user access to an imported shared source.
- It does not preserve a rollback database inside the new runtime. Keep the stopped version 2 deployment separately until verification is complete.
