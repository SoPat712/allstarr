# Release checklist

Run this checklist before publishing a release or updating a deployment.

- Run the warnings-as-errors build, both .NET test lanes, formatting, Compose contracts, and WebUI check, unit, build, budget, and browser checks.
- Exercise native and external playback, artwork, provider account tests, and a linked playlist on the candidate build.
- Verify migrations and backup restoration against an existing database. Preserve a matching database, encryption key ring, configuration, and previous image for rollback.
- Check the current [GAMDL release](https://github.com/glomatico/gamdl/releases/latest) against `sidecars/apple-gateway/pyproject.toml`. Qualify a changed pin with the frozen lock, gateway tests, actual CLI options, and the account-bound wrapper entrypoint. Record the date, version, and any failed qualification before release.
- Confirm optional Apple services remain disabled unless explicitly configured. Gateway API 2 is required for account-bound media; older gateways must be updated.
- Check extension revisions, signed sessions, and the upstream license/provenance ledger.

## GAMDL qualification record

On 2026-10-08, [GAMDL 3.9.1](https://github.com/glomatico/gamdl/releases/tag/3.9.1) was the current stable release, published 2026-09-22. The gateway pin was updated from 3.8.2. All 83 gateway and account-binding fixtures and 15 actual CLI parser cases passed; the Docker-pinned uv version accepted the lock and a frozen install. Its Linux wheels support the gateway's Python 3.12 Bookworm image on x64 and ARM64. These offline checks do not constitute a live Apple subscription or wrapper playback test.

The gateway additionally tests its account-bound entrypoint because upstream wrapper mode does not use `--cookies-path`. Selected-account and wrapper-account identities must match before wrapper-backed media can run. The selected account remains the catalog and personal-library API owner.
