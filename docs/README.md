# Allstarr documentation

Start with the guide for your task. These pages describe the current implementation.

## Start here

- [User guide](user-guide.md): dashboard, setup, playlists, cache and For you.
- [Client API](api/README.md): authentication, versions, errors and limits.
  [v2](api/v2.md) is the native API for new clients; [v1](api/v1.md) covers existing integrations.
- [Architecture overview](architecture/overview.md): runtime boundaries and code ownership.
- [Music ecosystem reference ledger](architecture/reference-projects.md): pinned upstream projects, reusable lessons, license boundaries, and rejected ideas.
- [Configuration](operations/configuration.md): deployment-owned values, durable settings, and secrets.
- [Deployment profiles](operations/deployment-profiles.md): install, update, optional services, backup, and restore.
- [Storage](operations/storage.md): SQLite ownership, data folders, backup, and recovery.
- [Scrobble delivery and recovery](operations/scrobbling.md): saved destinations, retries and uncertain-send repair.
- [Extension SDK v1](extensions/sdk-v1.md): package, capability, permission, and account contracts.

## Operator guides

- [Release checklist](operations/release-checklist.md)
- [HTTPS, private dashboard access and SSO](operations/webui-access.md)
- [Legacy `.env` import](operations/legacy-env-import.md)
- [Apple download provider](operations/apple-download-provider.md)
- [Lyrics sources](operations/lyrics.md)
- [Spotify lyrics service](operations/spotify-lyrics.md)
- [Client compatibility](operations/client-compatibility.md)
- [Jellyfin v12 music surface](operations/jellyfin-music-surface-v12.md)
- [AudioMuse status](operations/audiomuse.md): shelved integration, retained in the build and hidden from the normal dashboard.

## Contributor guides

- [Contributing](../CONTRIBUTING.md)
- [WebUI design system](../DESIGN.md)
- [Test and qualification tools](../tools/tests/README.md)
- [Provider capability module](../allstarr/Providers/Contracts/README.md)

## Documentation rules

1. Describe current behavior. Mark shelved functionality plainly.
2. Link to the owning code instead of duplicating long lists that can drift.
3. Keep deployment choices in operator guides and unfinished work out of public user documentation.
4. SQLite is the durable store. Redis and Valkey are not required. Legacy imports require an explicit preview and confirmation. Source builds use the overlay under `tools/`.
5. When code and documentation disagree, fix the documentation in the same completed implementation chunk.
