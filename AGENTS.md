# Working on Allstarr

This is the single repository guide for coding agents and automated contributors. Human contributors should also read [CONTRIBUTING.md](CONTRIBUTING.md).

## Product goal

Allstarr is a self-hosted music gateway in front of a Jellyfin or Subsonic/OpenSubsonic backend. It preserves the listener's normal client while adding provider-neutral search, matching, playback, playlists, lyrics, scrobbling, history, and discovery.

The application is moving toward a public beta. Favor changes that make ordinary setup and daily use clearer, safer, faster, and easier to verify. Do not add speculative frameworks, duplicate owners, or edge-case machinery without a demonstrated user or protocol need.

## Start with the right source of truth

Read only what the task needs:

1. [README.md](README.md) for supported product behavior and installation.
2. [docs/README.md](docs/README.md) for the documentation map.
3. [docs/architecture/overview.md](docs/architecture/overview.md) before changing ownership or boundaries.
4. [DESIGN.md](DESIGN.md) before changing the WebUI.
5. [CONTRIBUTING.md](CONTRIBUTING.md) for validation and pull-request expectations.
6. The nearest operation, protocol, extension, or module document for the area being changed.

Running code, migrations, tests, and checked-in configuration are authoritative when documentation disagrees. Fix drift in the same change.

## Repository map

| Path | Responsibility |
| --- | --- |
| `allstarr/Program.cs` | Application composition and middleware order |
| `allstarr/Controllers/` | Admin APIs and Jellyfin/Subsonic protocol surfaces |
| `allstarr/Core/Capabilities/` | Provider contracts and registration |
| `allstarr/Core/Routing/` | Capability and account selection |
| `allstarr/Core/Matching/` | Canonical identity, evidence, and match decisions |
| `allstarr/Core/Playlists/` | Playlist ingestion, projection, and synchronization |
| `allstarr/Core/Playback/` | Playback observations and client sessions |
| `allstarr/Core/Intelligence/` | Listening history, recommendations, and AudioMuse integration |
| `allstarr/Core/Jobs/` | Durable jobs, schedules, leases, retries, and outbox |
| `allstarr/Core/Storage/` | PostgreSQL model, migrations, and state transfer |
| `allstarr/Core/Extensions/` | Extension package lifecycle and permissions |
| `allstarr/Services/` | Built-in provider adapters and external gateways |
| `webui/` | Svelte 5/SvelteKit administration interface |
| `allstarr.Tests/` | .NET unit, integration, protocol, and migration coverage |
| `webui/tests/` | Browser behavior and responsive coverage |
| `tools/tests/` | Qualification, timing, and live smoke tools |
| `sidecars/apple-gateway/` | Bounded Apple/GAMDL compatibility gateway |
| `docs/` | User, operator, architecture, protocol, and extension documentation |

Keep responsibilities modular. Extend the existing owner instead of creating a second matching, routing, playlist, credential, cache, or background-work system.

## Product invariants

- One deployment exposes either Jellyfin or Subsonic/OpenSubsonic, never both catch-all protocol surfaces.
- PostgreSQL is the only durable database. Audio, artwork, cache payloads, backups, and the encryption key ring remain files.
- Original backend library files are read-only inputs. Only explicitly owned managed, cache, download, or kept paths may be written.
- Provider credentials are encrypted and resolved only for the exact tenant, user, library, capability, and account scope.
- Local backend objects pass through unchanged. A matched item uses the complete original backend object; a virtual item must be internally consistent and clearly external.
- Provider capabilities are interchangeable typed contracts. Built-ins and extensions meet at the same registry without letting extensions replace reserved built-in IDs.
- Stateful or retryable work uses the durable job system. Do not launch detached controller tasks for downloads, matching, playlist changes, scrobbling, imports, or extension lifecycle work.
- Optional providers and sidecars degrade their own capability when unavailable; they must not prevent core startup or native proxy use.
- Never expose secrets, tokens, cookies, signed media URLs, private identifiers, or raw provider payloads in logs, errors, fixtures, or documentation.

## Change workflow

1. Define the user-visible or protocol-visible acceptance condition.
2. Trace the request through its existing controller, core owner, persistence boundary, adapter, and projection.
3. Fix the shared cause in that owner; avoid route-specific or provider-specific copies.
4. Add the smallest deterministic regression that would have caught the problem.
5. Run focused checks while iterating and the affected lane at the integration boundary.
6. Update the owning documentation when behavior, setup, architecture, permissions, or recovery changes.
7. Review the final diff for unrelated edits, generated output, credentials, and weakened assertions.

Preserve unrelated work in a dirty tree. Stage exact files only; never use `git add .`, destructive resets, or broad cleanup commands.

## Verification

Use the smallest relevant checks first.

### .NET

```bash
dotnet build allstarr.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test allstarr.Tests/allstarr.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~AreaBeingChanged"
dotnet format allstarr.sln --no-restore --verify-no-changes --verbosity minimal
```

PostgreSQL integration tests require an isolated database through `ALLSTARR_TEST_POSTGRES`. Release validation runs both CI lanes: `Lane!=ReleaseCritical` and `Lane=ReleaseCritical`.

### WebUI

```bash
cd webui
npm run check
npm test
npm run build
npm run check:budgets
npm run test:e2e:existing-build
```

Run browser checks after the production build when using `test:e2e:existing-build`. Preserve keyboard, responsive, light/dark, reduced-motion, and no-overflow coverage for touched flows.

### Deployment and protocol work

- Validate the affected Compose profile with `docker compose ... config --quiet`.
- Use fixtures and mocked providers in automated tests; never require live personal credentials.
- Protocol changes need request/response fixtures and the relevant qualification checks in `tools/tests/`.
- Migration, backup, restore, and destructive behavior require an isolated target and exact ownership checks.

Do not weaken discovery, assertions, isolation, compatibility, accessibility, or security to make a gate pass.

## WebUI rules

Follow [DESIGN.md](DESIGN.md). Reuse the existing Svelte, Bits UI, Tailwind, Lucide, and shared component system before adding a dependency or page-specific control. Keep the interface dense where comparison matters and explanatory where setup or empty state needs guidance.

Integrations owns Services, Accounts, Extensions, and Routing. Intelligence owns listening history, imports, discovery, automation, and its built-in AudioMuse connection. Settings owns deployment and operator behavior. Do not scatter the same configuration across these areas.

## Documentation rules

- `README.md` is the public product and installation entry point.
- `docs/user-guide.md` explains the dashboard and common workflows.
- `docs/operations/` owns deployment and recovery procedures.
- `docs/architecture/` owns durable boundaries and code ownership.
- `docs/extensions/` owns the public extension contract.
- Module README files stay beside specialized code or tools.

Describe shipped behavior, not aspirations. Prefer one canonical explanation and link to it instead of copying it. Do not commit local agent state, prompts, session logs, design-tool state, private infrastructure details, generated reports, or credentials.
