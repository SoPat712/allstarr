# Contributing To Allstarr

Contributions are welcome. Allstarr sits in the middle of authentication, personal provider accounts, media files, and two compatibility surfaces, so a small-looking change can have a wide blast radius. Please keep changes focused and prove the behavior you touched.

## Development Setup

Clone the repository and install the .NET SDK version pinned by the project. Standard Compose is the easiest way to run the full durable stack:

```bash
git clone https://github.com/SoPat712/allstarr.git
cd allstarr
cp .env.example .env
```

Create the Postgres password and Allstarr key-ring files using the instructions in [README.md](README.md), review `.env`, then start the development Compose stack described by the checked-in Compose files.

For a direct application run, use an explicitly configured local database and persistent paths. SQLite deliberately requires a one-shot bootstrap confirmation for a missing database. Follow [docs/operations/storage.md](docs/operations/storage.md); do not weaken that guard just to make a local run shorter.

```bash
dotnet restore allstarr.sln
dotnet build allstarr.sln
dotnet test allstarr.sln
```

## Before You Change Code

Read [OVERHAUL.md](OVERHAUL.md) for the locked product decisions, then read the relevant document under [`docs/steering`](docs/steering/INTRODUCTION.md). Those documents describe boundaries that are easy to break accidentally, including protocol relay behavior, account scope, provider routing, downloads, playlists, scrobbling, storage, and testing.

In particular:

- one deployment serves one selected proxy protocol;
- Postgres and SQLite contain control-plane state, never audio bytes;
- original library files are read-only inputs;
- user-owned work needs a verified backend identity and exact tenant scope;
- provider credentials are secret references resolved just in time;
- optional work belongs in durable jobs, not detached controller tasks;
- streaming and downloading are separate provider capabilities;
- optional external services degrade their own capability instead of breaking startup;
- third-party extension packages are untrusted until verified.

## Tests And Fixtures

Every behavior change, bug fix, contract change, and migration rule needs focused coverage. Run the smallest relevant tests while iterating, then run the full Release suite before asking for review:

```bash
dotnet test allstarr.sln -c Release
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

Migration work must be checked against SQLite and Postgres. A passing SQLite test does not prove a Postgres migration, type, constraint, or locking claim.

## Provider Extensions

Provider SDK packages live outside the core implementation boundary and must declare their hooks, scope, network access, and secret permissions. Use the packaging and verification workflow documented in [docs/extensions/sdk-v1.md](docs/extensions/sdk-v1.md). Do not add an activation shortcut that bypasses checksum, permission review, staged lifecycle, or rollback.

First-party packages should pass the same SDK compatibility suite as third-party packages. Do not auto-enroll users in an external registry.

## Documentation

Update the owner document when behavior changes. Keep the root docs useful to operators and contributors; keep detailed invariants in the appropriate steering reference. Use the project's direct, normal voice. Prefer exact statements over promotional claims, and label planned behavior as planned.

Check local Markdown links after renaming or removing files. Never paste real secrets, signed URLs, account names, or private library paths into examples.

## Pull Requests

1. Fork the repository and create a focused branch.
2. Make the change with tests and any required fixtures or migrations.
3. Run the focused tests and the full Release suite.
4. Check Compose configuration if deployment files changed.
5. Explain the user-visible behavior, compatibility risk, migration impact, and verification in the pull request.

Keep commits small enough to review. Follow the existing code patterns, use clear names and explicit failure paths, and avoid drive-by formatting in unrelated files. If your work changes a client-visible contract, provider permission, durable schema, filesystem boundary, or recovery procedure, call that out directly.

## Security And Bug Reports

Use the repository issue templates for normal bugs and feature requests. Do not include credentials or private logs. If a report describes an exploitable secret, authentication, filesystem, package-verification, or cross-tenant problem, avoid publishing sensitive reproduction details in a public issue and use the repository's private security-reporting channel when available.
