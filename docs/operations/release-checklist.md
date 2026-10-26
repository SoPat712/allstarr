# Release checklist

Run this checklist before publishing a release or updating a deployment.

- Run the warnings-as-errors build, both .NET test lanes, formatting, Compose contracts, and WebUI check, unit, build, budget, and browser checks.
- Exercise native and external playback, artwork, provider account tests, and a linked playlist on the candidate build.
- Verify migrations and backup restoration against an existing database. Preserve a matching database, encryption key ring, configuration, and previous image for rollback.
- Check the current [GAMDL release](https://github.com/glomatico/gamdl/releases/latest) against `sidecars/apple-gateway/pyproject.toml`. Qualify a changed pin with the frozen lock, gateway tests, actual CLI options, and the account-bound wrapper entrypoint. Record the date, version, and any failed qualification before release.
- Confirm optional Apple services remain disabled unless explicitly configured. Gateway API 2 is required for account-bound media; older gateways must be updated.
- Check extension revisions, signed sessions, and the upstream license/provenance ledger.

## Candidate and approval

Freeze the candidate at one commit and save its `tools/create-release-manifest.sh` output with the qualification results. Include the exact application version, container image digest, migration inputs and rollback image. Changes after qualification require a new candidate and the affected checks again.

The release owner must run the real version 2 to version 3 upgrade against a stopped copy of the existing installation before merging the release to `main` or publishing it. The automated upgrade fixture does not replace this check. Verify sign-in, native and provider playback, seeking, existing playlists, favorites and encrypted accounts. Keep the original installation and its matching backup available for rollback.

Before tagging, move the shipped entries from **Unreleased** in the [changelog](../../CHANGELOG.md) into a dated entry matching `allstarr/AppVersion.cs`. Describe the changes listeners and operators will notice, including any setup or compatibility changes.

## Tester release channel

The publication workflow requires a version tag matching the application version, such as `v3.1.0`. A successful release publishes the version image and its multi-platform digest. A plain `major.minor.patch` release then updates `ghcr.io/sopat712/allstarr:stable` from that verified digest. Branch builds, manual branch builds and preview tags do not move `stable`. The image health smoke test runs before publication, and the workflow verifies the stable alias after promotion.

For a test group, pin the announced version or digest in `.env`:

```dotenv
ALLSTARR_IMAGE=ghcr.io/sopat712/allstarr:<version>
```

Replace `<version>` with the published version. To keep the exact bytes, use `ghcr.io/sopat712/allstarr@sha256:<digest>` instead. `stable` follows later releases; an exact version or digest keeps a group on its chosen build. Before the first plain-version release, use an explicitly published preview version rather than assuming the `stable` alias exists.

Keep existing version tags fixed. Publish another version for a correction. Announce the version, short changelog and any required setup together, then verify one tester's sign-in, playback and seek before inviting the rest of the group.

## GAMDL qualification record

On 2026-10-08, [GAMDL 3.9.1](https://github.com/glomatico/gamdl/releases/tag/3.9.1) was the current stable release, published 2026-09-22. The gateway pin was updated from 3.8.2. All 83 gateway and account-binding fixtures and 15 actual CLI parser cases passed; the Docker-pinned uv version accepted the lock and a frozen install. Its Linux wheels support the gateway's Python 3.12 Bookworm image on x64 and ARM64. These offline checks do not constitute a live Apple subscription or wrapper playback test.

The gateway additionally tests its account-bound entrypoint because upstream wrapper mode does not use `--cookies-path`. Selected-account and wrapper-account identities must match before wrapper-backed media can run. The selected account remains the catalog and personal-library API owner.
