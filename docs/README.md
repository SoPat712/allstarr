# Allstarr documentation

Start with the guide for your task. These pages describe the current implementation.

## Run Allstarr

- [User guide](user-guide.md): the dashboard, first setup, playlists, matches, cache and For you.
- [Configuration](operations/configuration.md): deployment values, dashboard settings and saved secrets.
- [Deployment profiles](operations/deployment-profiles.md): install, update, optional services, backup and rollback.
- [Storage](operations/storage.md): the data folder, backups, restore and moving an installation.
- [HTTPS, private dashboard access and SSO](operations/webui-access.md): reverse proxies, remote music apps and single sign-on.
- [Legacy `.env` import](operations/legacy-env-import.md): bringing settings over from version 2.
- [Lyrics sources](operations/lyrics.md): where lyrics come from and how to set their order.
- [Scrobble delivery and recovery](operations/scrobbling.md): Last.fm and ListenBrainz delivery, retries and repair.
- [Client compatibility](operations/client-compatibility.md): which music apps work and their known limits.
- [Apple download provider](operations/apple-download-provider.md): optional Apple Music playback and downloads.
- [Apple download gateway](../sidecars/apple-gateway/README.md): the optional gateway service behind Apple downloads.
- [Extension session verifier](../sidecars/session-verifier/README.md): optional automatic renewal of extension sign-ins.
- [AudioMuse](operations/audiomuse.md): optional AudioMuse connection for mixes built from how songs sound.

## Build on Allstarr

- [Client API](api/README.md): authentication, versions, errors and limits for apps built for Allstarr.
  [v2](api/v2.md) is the native API for new apps; [v1](api/v1.md) covers existing integrations.
- [Extension SDK v1](extensions/sdk-v1.md): package, capability, permission and account contracts for extensions.
- [Jellyfin music surface](operations/jellyfin-music-surface-v12.md): the Jellyfin requests Allstarr allows and how it answers them.

## Contributors and maintainers

- [Contributing](../CONTRIBUTING.md): building from source, tests and the code layout.
- [WebUI design system](../DESIGN.md)
- [Architecture overview](architecture/overview.md): runtime boundaries and code ownership.
- [Music ecosystem reference ledger](architecture/reference-projects.md): upstream projects, reuse and license notices, and rejected ideas.
- [Provider capability module](../allstarr/Providers/Contracts/README.md)
- [Test and qualification tools](../tools/tests/README.md)
- [Release checklist](contributing/release-checklist.md)

## Documentation rules

1. Describe current behavior. Mark shelved or development-only functionality plainly.
2. Use the names the dashboard shows today for pages, tabs and settings.
3. Link to the owning code or page instead of copying long lists that can drift.
4. Keep deployment choices in operator guides, and keep unfinished work out of user documentation.
5. When code and documentation disagree, fix the documentation in the same change.
