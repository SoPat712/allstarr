# Allstarr documentation

These documents describe the code that is currently shipped. Planned work does not belong here; implementation planning lives in the ignored `apis/steering` workspace.

## Start here

- [Architecture overview](architecture/overview.md): runtime boundaries and ownership.
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

## Documentation rules

1. Describe current behavior only.
2. Link to the owning code instead of duplicating long lists that can drift.
3. Keep deployment choices in operator guides and unfinished work in `apis/steering`.
4. Do not document SQLite, Redis, Valkey, AIO images, Compose overlays, bundled extension registries, or automatic legacy-state conversion as supported runtime features.
5. When code and documentation disagree, fix the documentation in the same completed implementation chunk.
