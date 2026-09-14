# Allstarr documentation

User and operator guides describe shipped behavior. Contributor assessments explicitly distinguish current implementation from proposed work. Start with one audience and follow links to the owning document instead of reading the whole tree.

## Start here

- [User guide](user-guide.md): dashboard map, setup order, imports, playlists, cache, and Intelligence.
- [Architecture overview](architecture/overview.md): runtime boundaries and code ownership.
- [Jellyfin and provider routing plan](architecture/jellyfin-provider-routing-plan.md): accepted first-use-case matching, route order, audio quality, fallback, and live qualification contract.
- [Music ecosystem reference ledger](architecture/reference-projects.md): pinned upstream projects, reusable lessons, license boundaries, and rejected ideas.
- [Configuration](operations/configuration.md): deployment-owned values, durable settings, and secrets.
- [Deployment profiles](operations/deployment-profiles.md): install, update, optional services, backup, and restore.
- [Storage](operations/storage.md): PostgreSQL ownership, migration, backup, and recovery.
- [Extension SDK v1](extensions/sdk-v1.md): package, capability, permission, and account contracts.

## Operator guides

- [Legacy `.env` import](operations/legacy-env-import.md)
- [Apple download provider](operations/apple-download-provider.md)
- [Spotify lyrics service](operations/spotify-lyrics-sidecar.md)
- [Client compatibility](operations/client-compatibility.md)
- [Jellyfin v12 music surface](operations/jellyfin-music-surface-v12.md)

## Contributor guides

- [Repository agent guide](../AGENTS.md)
- [Contributing](../CONTRIBUTING.md)
- [WebUI design system](../DESIGN.md)
- [Test and qualification tools](../tools/tests/README.md)
- [Provider capability module](../allstarr/Core/Capabilities/README.md)
- [Release readiness assessment](release-readiness.md): dated feature inventory, code-churn evidence, and proposed first-release gates; not a shipped-support contract.

## Documentation rules

1. User and operator guides describe current behavior only. Contributor plans must lead with a visible status and must not read like shipped setup instructions.
2. Link to the owning code instead of duplicating long lists that can drift.
3. Keep deployment choices in operator guides and unfinished work out of public user documentation.
4. Do not document SQLite, Redis, Valkey, AIO images, Compose overlays, bundled extension registries, or automatic legacy-state conversion as supported runtime features.
5. When code and documentation disagree, fix the documentation in the same completed implementation chunk.
