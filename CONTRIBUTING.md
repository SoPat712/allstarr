# Contributing To Allstarr

Contributions are welcome. Allstarr sits in the middle of authentication, personal provider accounts, media files, and two compatibility surfaces, so a small-looking change can have a wide blast radius. Please keep changes focused and prove the behavior you touched.

## Development Setup

Clone the repository and install the .NET 10 SDK (CI uses 10.0.301) and Node.js 22 or later for the WebUI. Use the developer script to build and run the full stack from source:

```bash
git clone https://github.com/SoPat712/allstarr.git
cd allstarr
./tools/develop.sh mode source
./tools/develop.sh up
```

The developer script combines the standard Compose file with `tools/docker-compose.source.yml`. `./tools/develop.sh up` builds the current checkout. `./tools/develop.sh update` requires a clean tracked tree on a tracked branch, fast-forwards it and builds before recreating the containers. Return to published images with `./tools/develop.sh mode release`. Optional profiles saved with `./allstarr.sh enable` work in source mode too.

Deployment overrides in `.env` are optional. Choose and verify the backend in the dashboard on first startup. `BACKEND_TYPE` and `JELLYFIN_URL` or `SUBSONIC_URL` are optional startup defaults; a saved connection takes precedence. See [deployment](docs/operations/deployment-profiles.md).

For a direct application run, set `Storage__DataDirectory` to a disposable local folder. Follow [docs/operations/storage.md](docs/operations/storage.md).

```bash
dotnet restore allstarr.sln
dotnet build allstarr.sln
dotnet test allstarr.sln
```

## Before You Change Code

Read [README.md](README.md) for supported behavior, the [documentation map](docs/README.md), the [architecture overview](docs/architecture/overview.md), and the owning operation or module document. WebUI changes also follow [DESIGN.md](DESIGN.md). Running code, migrations, tests, and checked-in configuration are authoritative when documentation disagrees; fix drift in the same change.

## Repository Map

| Path | Responsibility |
| --- | --- |
| `allstarr/Program.cs` | Application composition and middleware order |
| `allstarr/Controllers/` | Admin APIs and Jellyfin/Subsonic protocol surfaces |
| `allstarr/Providers/Contracts/` | Provider contracts, registration, and account selection |
| `allstarr/Providers/` | Built-in provider adapters and source settings |
| `allstarr/Core/Music/` | Search, provider streams, mixes, and catalog merges shared by both protocols |
| `allstarr/Core/Protocols/` | Provider gateway, per-protocol search adapters, and relay shaping |
| `allstarr/Core/Matching/` | Canonical identity, evidence, and match decisions |
| `allstarr/Core/Playlists/` | Playlist ingestion, projection, and synchronization |
| `allstarr/Core/Playback/` | Playback observations and client sessions |
| `allstarr/Core/ListeningHistory/` | Listening-history imports and retention |
| `allstarr/Core/AudioMuse/` | AudioMuse connection and mixes built from how songs sound |
| `allstarr/Core/Migrations/` | Library Import: moving a Spotify or Apple Music collection into Allstarr |
| `allstarr/Shelved/Intelligence/` | Development-only recommendation workspace |
| `allstarr/Core/ClientApi/`, `allstarr/Core/ClientApiV2/` | Client contracts and resource readers |
| `allstarr/Core/Jobs/` | Durable jobs, schedules, restart recovery, and retries |
| `allstarr/Core/Storage/` | SQLite model, migrations, and backups |
| `allstarr/Providers/Extensions/` | Extension package lifecycle and permissions |
| `allstarr/Services/` | Shared services, backend adapters, and external gateways |
| `webui/` | Svelte 5/SvelteKit administration interface |
| `allstarr.Tests/` | .NET unit, integration, protocol, and migration coverage |
| `webui/tests/` | Browser behavior and responsive coverage |
| `tools/tests/` | Qualification, timing, and live smoke tools |
| `sidecars/apple-gateway/` | Bounded Apple/GAMDL compatibility gateway |
| `sidecars/session-verifier/` | Optional extension sign-in renewal service |
| `docs/` | User, operator, architecture, protocol, and extension documentation |

Keep responsibilities modular. Extend the existing owner instead of creating a second matching, routing, playlist, credential, cache, or background-work system.

## Product Invariants

- One deployment exposes either Jellyfin or Subsonic/OpenSubsonic, never both catch-all protocol surfaces.
- SQLite is the only durable database. Audio, artwork, cache payloads, backups, and the encryption key ring remain files.
- Original backend library files are read-only inputs. Only explicitly owned managed, cache, download, or kept paths may be written.
- One deployment serves one household. A user is identified by the exact backend type, instance, and principal; administrator status comes from the backend.
- Provider accounts are Personal (one owner) or Shared (no owner). Administrative management never grants playback through another user's Personal account.
- Provider credentials are encrypted and resolved just in time for the exact user, account revision, capability, and credential purpose. Backend library access is checked separately for local items.
- Local backend objects pass through unchanged. A matched item uses the complete original backend object; a virtual item must be internally consistent and clearly external.
- Provider capabilities are interchangeable typed contracts. Built-ins and extensions meet at the same registry without letting extensions replace reserved built-in IDs.
- Stateful or retryable work uses the durable job system. Do not launch detached controller tasks for downloads, matching, playlist changes, scrobbling, imports, or extension lifecycle work.
- Optional providers and sidecars degrade their own capability when unavailable; they must not prevent core startup or native proxy use.
- Never expose secrets, tokens, cookies, signed media URLs, private identifiers, or raw provider payloads in logs, errors, fixtures, or documentation.
- Streaming and downloading are separate provider capabilities.
- Third-party extension packages are untrusted until verified.

## Change Workflow

1. Define the user-visible or protocol-visible acceptance condition.
2. Trace the request through its existing controller, core owner, persistence boundary, adapter, and projection.
3. Fix the shared cause in that owner; avoid route-specific or provider-specific copies.
4. Add the smallest deterministic regression that would have caught the problem.
5. Run focused checks while iterating and the affected lane at the integration boundary.
6. Update the owning documentation when behavior, setup, architecture, permissions, or recovery changes.
7. Review the final diff for unrelated edits, generated output, credentials, and weakened assertions.

Preserve unrelated work in a dirty tree. Stage exact files only; never use `git add .`, destructive resets, or broad cleanup commands.

## Tests And Fixtures

Every behavior change, bug fix, contract change, and migration rule needs focused coverage. Run the smallest relevant tests while iterating. Database tests create isolated temporary SQLite files and run without an external database or opt-in variable. CI runs six regular shards and a separate release-critical job. These filters select the two groups:

```bash
dotnet test allstarr.sln -c Release --filter "Lane!=ReleaseCritical"
dotnet test allstarr.sln -c Release --filter "Lane=ReleaseCritical"
```

Verify the Release build and formatting before submitting:

```bash
dotnet build allstarr.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet format allstarr.sln --no-restore --verify-no-changes --verbosity minimal
```

Useful focused examples:

```bash
dotnet test allstarr.Tests/allstarr.Tests.csproj -c Release --filter "FullyQualifiedName~Subsonic"
dotnet test allstarr.Tests/allstarr.Tests.csproj -c Release --filter "FullyQualifiedName~Storage"
```

Protocol changes need real response/request fixtures for the affected Jellyfin or Subsonic support-matrix row.
Provider and external-gateway tests use local fixtures, fake providers, or mocked HTTP. Do not add live credentials
or live provider calls to the automated suite. Apple gateway tests must not assume wrapper-v2 itself implements the
Allstarr search/download contract.

Migration, backup, restore, and destructive behavior require an isolated disposable data folder and exact ownership checks. Validate affected Compose configuration with `docker compose ... config --quiet`. Do not weaken discovery, assertions, isolation, compatibility, accessibility, or security to make a check pass.

## WebUI

Follow [DESIGN.md](DESIGN.md). Reuse the existing Svelte, Bits UI, Tailwind, Lucide, and shared component system before adding a dependency or page-specific control. Keep the interface dense where comparison matters and explanatory where setup or empty state needs guidance.

The page map lives in `webui/src/routes/[...path]/+page.svelte` and the Settings categories in `webui/src/lib/components/SettingsView.svelte`. Sources owns Services (including **Add extension**) and Accounts. Library owns Playlists, Matches, Cached, Kept, To library and Import. For you owns mixes, New releases, Your taste, Recap and listening-history imports. Settings is one list of categories grouped as You, Everyone and This server; source order lives in **Settings → Music sources**, and the AudioMuse connection in **Settings → Mixes and discovery**. The older Intelligence workspace appears only with the `development` release profile; keep its boundary. When you move a setting, update `webui/src/lib/settings-search.ts` so **Find a setting** still finds it, and keep old addresses redirecting to the new page.

Run these checks from `webui/`:

```bash
npm run check
npm test
npm run build
npm run check:budgets
npm run test:e2e:existing-build
```

Run browser checks after the production build. Preserve keyboard, responsive, light/dark, reduced-motion, and no-overflow coverage for touched flows.

For a separate, read-only accessibility and consistency audit, run `npm run audit:ui -- --workers=1` after building. It uses the browser fixtures, blocks API writes and external requests, and writes measurements and annotated screenshots to `webui/test-results/ui-audit/`. Review `report.md` for coverage, limitations, and the most widespread findings; `report.json` contains individual measurements. Rare styles and wording differences are review candidates, not automatic failures. This audit is excluded from ordinary browser runs and CI.

## Provider Extensions

Provider SDK packages live outside the core implementation boundary and must declare their hooks, scope, network access, and secret permissions. Use the packaging and verification workflow documented in [docs/extensions/sdk-v1.md](docs/extensions/sdk-v1.md). Do not add an activation shortcut that bypasses checksum, permission review, or staged activation.

Do not bundle provider packages or auto-enroll users in an external registry.

## Documentation

Update the owner document when behavior changes. Keep README and the user guide useful to operators; keep detailed invariants in architecture, operation, protocol, extension, or module documents. Use the project's direct, normal voice. Prefer exact statements over promotional claims, and do not put planned behavior in user documentation.

Documentation ownership:

- `README.md`: what Allstarr is, installation, first run and the documentation map.
- `docs/user-guide.md`: dashboard and common workflows.
- `docs/operations/`: deployment and recovery procedures.
- `docs/architecture/`: durable boundaries and code ownership.
- `docs/extensions/`: public extension contract.
- `docs/api/`: versioned client API guides.
- `docs/contributing/`: maintainer procedures, such as the [release checklist](docs/contributing/release-checklist.md).
- Module README files: specialized code and tools beside their implementation.

Do not commit local design-tool state, generated test reports, private deployment details, or duplicate working documents.

Check local Markdown links after renaming or removing files. Never paste real secrets, signed URLs, account names, or private library paths into examples.

## Pull Requests

1. Fork the repository and create a focused branch.
2. Make the change with tests and any required fixtures or migrations.
3. Run the focused tests and the full Release suite.
4. Check Compose configuration if deployment files changed.
5. Explain the user-visible behavior, compatibility risk, migration impact, and verification in the pull request.

Keep commits small enough to review. Follow the existing code patterns, use clear names and explicit failure paths, and avoid drive-by formatting in unrelated files. If your work changes a client-visible contract, provider permission, durable schema, filesystem boundary, or recovery procedure, call that out directly.

## Security And Bug Reports

Use the repository issue templates for normal bugs and feature requests. Do not include credentials or private logs. If a report describes an exploitable secret, authentication, filesystem, package-verification, or user-isolation problem, avoid publishing sensitive reproduction details in a public issue and use the repository's private security-reporting channel when available.
