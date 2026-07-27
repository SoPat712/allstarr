# Storage operations

This runbook covers Allstarr's durable storage baseline. PostgreSQL is the only supported database for runtime, backup, restore, and controlled state transfer.

## What Postgres stores

Postgres does not store song audio. Songs stay as normal files in the media folders that Allstarr, Jellyfin, Navidrome, and your own backup tools can reach.

The standard Compose mounts are:

| Data | Container path | Default host or volume location |
| --- | --- | --- |
| Downloaded songs | `/app/downloads` | `${DOWNLOAD_PATH:-./downloads}` |
| Kept or favorited songs | `/app/kept` | `${KEPT_PATH:-./kept}` |
| Durable database | Postgres data directory | `postgres-data` named volume |
| App state and database backup artifacts | `/app/state` | `allstarr-state` named volume |
| Rebuildable app cache | `/app/cache` | `allstarr-cache` named volume |

The database stores application state such as users, backend identities, provider accounts, encrypted secret versions, durable jobs, outbox events, provider health, matches, playlist links, recommendation state, audit events, and the backup catalog. An audio file still belongs in a configured media root. A database row can point at a song. It does not contain the song.

Recommendation candidates retain their canonical recording and scoped provider-account provenance
when known, source revision, weighted evidence/score signals, exclusions, generated-set membership,
and user feedback. Negative or dismissed feedback becomes an explainable exclusion on later runs in
the same tenant, user, backend, and library scope. Full and selective state transfers preserve this
lineage.


## PostgreSQL is mandatory at runtime

The application runtime requires `Postgres` in `Storage:Provider`. It never falls back to SQLite. If PostgreSQL
is unavailable or has a pending migration, readiness fails and state-changing requests are rejected. A bounded
runtime probe keeps checking the same database; mutations, durable jobs, and outbox delivery resume only after
that PostgreSQL database returns with the current schema.

### Standard Compose: Postgres

The checked-in `docker-compose.yml` explicitly sets:

```text
Storage__Provider=Postgres
Storage__ConnectionString=Host=postgres;Port=5432;Database=...;Username=...;Include Error Detail=false
Storage__PasswordFile=/run/secrets/postgres_password
```

The Postgres password is read from the mounted secret file and is not placed on a process command line.


`Storage:RuntimeProbeIntervalSeconds` controls the bounded PostgreSQL check cadence and
defaults to 5 seconds. `Storage:RuntimeProbeTimeoutSeconds` also defaults to 5 seconds.

## Fresh standard install

Pre-overhaul runtime state is not imported automatically. Start the new durable baseline as a separate fresh install. This avoids carrying old Redis, cache, mapping, extension, or job formats into the new database and keeps the old stack available for rollback. Stop the old stack before the new deployment can write media, and give each version separate writable download, kept, cache, and managed-library roots. The existing backend library may be mounted read-only for matching, but the two versions must never write the same media roots concurrently.

After the new database is ready and the administrator signs in, the WebUI can preview an uploaded legacy `.env`.
It imports allowlisted non-secret settings and creates eligible shared provider accounts in a disabled state.
Deployment values remain a checklist, personal credentials must be reconnected by their owners, and playlist
definitions remain ownership/target handoffs. The exact behavior is defined in the
[legacy `.env` import contract](legacy-env-import.md).

Run these commands from the repository root:

```bash
cp .env.example .env
mkdir -p secrets downloads kept
umask 077
openssl rand -base64 32 > secrets/postgres-password.txt
key="$(openssl rand -base64 32)"
printf '{"activeKeyId":"key-1","keys":{"key-1":"%s"}}\n' "$key" > secrets/allstarr-keyring.json
unset key
chmod 600 .env secrets/postgres-password.txt secrets/allstarr-keyring.json
```

Review `.env` before startup. In particular, set the backend, media-server URL, image tag, and host paths for `DOWNLOAD_PATH` and `KEPT_PATH`. Then validate and start the deployment:

```bash
docker compose config --quiet
docker compose pull
docker compose up -d
docker compose ps
curl --fail --silent --show-error http://127.0.0.1:5274/health/ready
```

Use the configured proxy address and port instead of `127.0.0.1:5274` if you changed them. A healthy response means the selected database, schema, required directories, and required secret key ring passed readiness.

A normal stop or recreation keeps the named volumes:

```bash
docker compose down --remove-orphans
docker compose up -d
```

To discard the new app state and perform another genuinely fresh setup, remove the named volumes too:

```bash
docker compose down --volumes --remove-orphans
```

That command deletes the Postgres, app-state, and app-cache named volumes. It does not delete the bind-mounted `downloads` and `kept` folders or the files in `secrets`. Leave those media folders alone if you want to keep the songs. Regenerate `.env` and both secret files only when you also intend to reset the deployment credentials and encrypted application secrets.

## Schema migrations and the migration lock

`Storage__AutoMigrate` is `true` in standard Compose unless `STORAGE_AUTO_MIGRATE` says otherwise. On startup, Allstarr takes a database-specific lock before applying Entity Framework migrations:

- Postgres uses an advisory lock scoped to the current database.
- The default lock wait is 120 seconds. `Storage:MigrationLockTimeoutSeconds` accepts 5 through 1800 seconds.

Only the lock holder applies migrations. Other instances wait and do not become ready against a partially migrated schema. If the lock cannot be acquired, readiness reports `migration_lock_unavailable`.

With automatic migration disabled, Allstarr only checks connectivity and pending migrations. A pending migration leaves it unready with `schema_migration_required`. The offline storage command deliberately does not provide an unreviewed migration-only shortcut. Keep automatic migration enabled for the checked-in additive migrations, or run a reviewed application startup during a maintenance window.

Startup also compares every applied migration ID with the migrations known to the running image. If the database contains a migration from a newer or otherwise unknown build, Allstarr does not try to migrate over it. Readiness reports `schema_version_unsupported`. Use the image that owns that schema or restore a verified backup made for the image you are running.

Before an application upgrade, create and copy out a verified database backup. Keep the prior `ALLSTARR_IMAGE` tag recorded. Then upgrade:

```bash
docker compose pull
docker compose up -d
curl --fail --silent --show-error http://127.0.0.1:5274/health/ready
```

Do not treat a schema down-migration as rollback. Use a database restored into an isolated name together with the compatible application image, as described below.

## Create and retain a verified backup

Sign in to the admin UI, open **Settings**, and select **Create database backup**. The authenticated admin endpoint is `POST /api/admin/storage/backups`, but the UI is the supported way to provide the existing admin session.

You can also create the same verified backup while the normal web host is stopped:

```bash
docker compose stop allstarr
docker compose run --rm --no-deps allstarr storage backup
```

The command prints one JSON object containing the artifact path, manifest path, SHA-256, schema version, and creation time. It does not start workers or the HTTP server.

Backup creation is synchronous inside the request even though the endpoint returns an accepted response. Before it records a backup as `verified`, Allstarr does the following:

- Postgres creates a custom-format `pg_dump`, computes SHA-256, and checks the dump catalog with `pg_restore --list`.
- Allstarr writes a versioned neighboring `.manifest.json` with the artifact name, provider, schema version, application version, checksum, and `SecretKeyMaterialIncluded: false`.

Restore treats that manifest as the source of truth for the backup, not as a note for people. Missing, repeated,
unknown, or incorrectly typed fields are rejected. The provider, artifact filename, checksum, and schema must
agree with the requested restore and the running image. The manifest identity, application version, creation
time, and secret-material policy are also strictly validated before they enter the backup catalog. A manifest
for an older or newer schema is rejected even when its checksum is valid. Keep the artifact and its manifest
together.

Artifacts are written under `/app/state/backups`. Keeping the only backup in the same named volume is not enough. Copy the artifacts off the Docker host or into your normal backup system:

```bash
mkdir -p allstarr-backups
docker compose cp allstarr:/app/state/backups/. ./allstarr-backups/
```

List the in-container artifacts at any time:

```bash
docker compose exec -T allstarr find /app/state/backups -maxdepth 1 -type f -print
```

For an extra Postgres check before restore, choose the dump filename without its directory and run:

```bash
DUMP_NAME=allstarr-postgres-20260710T120000Z-replace-this-id.dump

docker compose exec -T -e DUMP_NAME="$DUMP_NAME" allstarr sh -lc '
  set -eu
  cd /app/state/backups
  expected="$(grep Sha256 "$DUMP_NAME.manifest.json" | head -n 1 | cut -d \" -f 4)"
  test "${#expected}" -eq 64
  printf "%s  %s\n" "$expected" "$DUMP_NAME" | sha256sum -c -
  pg_restore --list "$DUMP_NAME" >/dev/null
'
```

The runtime image contains `pg_dump`, `pg_restore`, and `psql`. The database password stays in `/run/secrets/postgres_password` and should be passed through `PGPASSWORD`, never as a command-line argument.

## Restore Postgres into an isolated database

Never run `pg_restore --clean` against the live database. Restore into a new database name, inspect it, and only then switch Allstarr to it. The following example assumes the default Postgres user `allstarr`. Set `DB_USER` to the value of `POSTGRES_USER` if you changed it.

If the selected dump only exists in your off-host copy, copy the dump and its manifest back first:

```bash
DUMP_NAME=allstarr-postgres-20260710T120000Z-replace-this-id.dump
docker compose cp "./allstarr-backups/$DUMP_NAME" "allstarr:/app/state/backups/$DUMP_NAME"
docker compose cp "./allstarr-backups/$DUMP_NAME.manifest.json" "allstarr:/app/state/backups/$DUMP_NAME.manifest.json"
```

Run the checksum and `pg_restore --list` check from the previous section. Record the SHA-256 emitted by
`storage backup`, stop the normal host, and create a fresh target:

```bash
RESTORE_DB=allstarr_restore_20260710
DB_USER=allstarr
DUMP_SHA256=replace-with-the-recorded-64-character-hash

docker compose stop allstarr

docker compose exec -T -e RESTORE_DB="$RESTORE_DB" postgres sh -lc '
  set -eu
  createdb --username "$POSTGRES_USER" "$RESTORE_DB"
'
```

Use the offline restore command for the actual restore. It receives target credentials through an environment
variable created inside the one-off container. The password does not appear in the process command line:

```bash
docker compose run --rm --no-deps \
  --entrypoint sh \
  -e DUMP_NAME="$DUMP_NAME" \
  -e DUMP_SHA256="$DUMP_SHA256" \
  -e RESTORE_DB="$RESTORE_DB" \
  -e DB_USER="$DB_USER" \
  allstarr -lc '
    set -eu
    password="$(cat /run/secrets/postgres_password)"
    export ALLSTARR_RESTORE_TARGET="Host=postgres;Port=5432;Database=$RESTORE_DB;Username=$DB_USER;Password=$password;Include Error Detail=false"
    unset password
    exec dotnet allstarr.dll storage restore-postgres \
      --artifact "/app/state/backups/$DUMP_NAME" \
      --sha256 "$DUMP_SHA256" \
      --target-connection-env ALLSTARR_RESTORE_TARGET \
      --confirm-isolated-target-database "$RESTORE_DB" \
      --confirm-destructive-restore
  '
```

The target database name confirmation is intentionally separate from the destructive confirmation. It must
exactly match the database in `ALLSTARR_RESTORE_TARGET`, and that name must differ from the database configured
for the running Allstarr instance. The command rejects the configured current database before it invokes
`pg_restore`, even when `--confirm-destructive-restore` is present.

The command prints `"status":"verified"` only after it has strictly parsed the manifest, matched it to the
artifact and requested SHA-256, checked the Postgres dump catalog, completed `pg_restore`, opened the target,
and confirmed that its complete migration history exactly matches the running image. It records the restore
verification result in the backup catalog. Any restore or verification failure returns a nonzero exit code and
does not report the target as verified.

Inspect the users, provider accounts, queued jobs, and audit state appropriate to your deployment. Confirm that the matching key-ring file is available before testing an encrypted provider credential.

For cutover, save the current configuration, change `POSTGRES_DB` in `.env` to the isolated database name, and recreate the services:

```bash
cp .env .env.before-storage-cutover
chmod 600 .env.before-storage-cutover
docker compose up -d
curl --fail --silent --show-error http://127.0.0.1:5274/health/ready
```

The original database is still present. Do not delete it until the restored deployment has passed your normal backend login, search, playlist, job, and provider-account smoke checks.

To roll back that cutover, restore the saved `.env`, pin `ALLSTARR_IMAGE` to the application version compatible with the original database, and recreate the services:

```bash
cp .env.before-storage-cutover .env
chmod 600 .env
docker compose up -d
curl --fail --silent --show-error http://127.0.0.1:5274/health/ready
```

For an application rollback after a forward schema migration, restore the pre-upgrade dump into another new database name and start the prior image against that database. Do not point the prior image at a database already migrated by the newer image unless that exact compatibility has been tested.

## Controlled PostgreSQL state transfer

Database-native dumps are the preferred PostgreSQL backup and restore format. The provider-neutral state-transfer format is reserved for controlled PostgreSQL-to-PostgreSQL moves where selective application state, rather than the whole database, must be transferred.

The transfer has these hard rules:

- Writes must be quiesced before export.
- Source and target must both be PostgreSQL and run the same Allstarr application/schema version.
- Import verifies the artifact checksum and strictly parses the manifest before touching the target.
- Unknown, repeated, missing, or incorrectly typed manifest fields and archive entries are rejected.
- Every durable target table must be empty.
- Encrypted secret bytes transfer unchanged; encryption key material, media files, and caches do not.

Stop every normal Allstarr instance that can write the source, then export:

```bash
docker compose stop allstarr
docker compose run --rm --no-deps allstarr storage export \
  --output /app/state/transfers \
  --confirm-writes-stopped
```

Configure an explicitly selected empty PostgreSQL target and import with the same image:

```bash
docker compose run --rm --no-deps allstarr storage import \
  --artifact /app/state/transfers/allstarr-state-replace.zip \
  --sha256 replace-with-the-recorded-64-character-hash \
  --confirm-empty-target
```

Carry the original key ring through a separate protected channel. Validate credentials, row counts, job recovery, audit history, and `/health/ready` before cutover.

## Key-ring handling

The database contains encrypted provider-secret versions. The 32-byte keys that decrypt them live only in the external JSON key ring mounted at `/run/secrets/allstarr_keyring`.

- Keep `secrets/allstarr-keyring.json` owner-readable only. The application rejects a file that is group-readable, group-writable, other-readable, or other-writable on Unix.
- Back it up separately using encrypted, access-controlled storage. Do not place it next to general database dumps.
- A restored database needs the key IDs used by its encrypted secret versions. A healthy database alone does not prove that every provider secret can be opened.
- Never commit the key ring, Postgres password, `.env`, dumps, or copied state to Git.

The database password secret and encryption key ring solve different problems. Losing the Postgres password blocks database access. Losing a key-ring key permanently blocks decryption of every secret version that names that key, even if the Postgres dump itself restores cleanly.

### Rotate active provider secrets

Take a verified database backup and a separate protected copy of the current key ring before rotation. Then add a
new base64-encoded 32-byte key to the `keys` object and set its ID as `activeKeyId`. Keep the old key in the file.
New or replaced secrets use the active key immediately.

Stop every Allstarr process that can write the database and run the bulk rotation command:

```bash
docker compose stop allstarr
docker compose run --rm --no-deps allstarr storage rotate-secrets \
  --confirm-writes-stopped
```

The command opens each active, non-revoked secret with its current key and creates a new encrypted version under
the active key. Its JSON result includes the active key ID plus examined, rotated, and already-active counts. A
missing old key, an undecryptable value, an unavailable database, or an incompatible schema makes the command
fail instead of skipping that reference.

After a successful command, restart Allstarr, check readiness, and test each configured provider account:

```bash
docker compose up -d
curl --fail --silent --show-error http://127.0.0.1:5274/health/ready
```

Do not remove the old key yet. Retired secret versions and older database backups can still name it. Keep it for
the full backup and rollback retention window, and take a new verified database backup with a separately
protected copy of the new key ring before considering key retirement.

## Recovery checklist

Before calling a restore or migration complete, verify all of the following:

1. The artifact checksum and provider-specific integrity check pass.
2. The restored schema has the expected migration history.
3. The matching application image starts and `/health/ready` succeeds.
4. Backend login resolves the expected Allstarr user and tenant.
5. Provider accounts can open their encrypted credentials without exposing them.
6. Pending and retrying jobs are present once, with no duplicate work introduced by the cutover.
7. Search, streaming, and playlist operations can still reach the bind-mounted song folders.
8. The original database and prior image remain available until the rollback window closes.
9. Database artifacts, media folders, and the key ring each exist in the separate backup location intended for them.
