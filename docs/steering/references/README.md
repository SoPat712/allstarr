# Allstarr Overhaul References

This directory contains the owned detailed specifications for the root [OVERHAUL.md](../../../OVERHAUL.md). Future agents should start at `OVERHAUL.md`, identify its phase and locked direction, then open the reference file for the area they are changing.

The root plan owns cross-cutting decisions, phase order, and exit criteria. These references own the detailed contract for their listed area. Current-code facts and target-overhaul requirements must stay visibly distinct; do not treat a target requirement as an implemented behavior.

## Reference Files

- [code-map.md](code-map.md): current Allstarr code anchors and migration points.
- [protocols.md](protocols.md): Jellyfin, Subsonic, OpenSubsonic, and Last.fm protocol references, parity matrix, backend playlist materialization, execution context, and adapter rules.
- [providers-and-extensions.md](providers-and-extensions.md): provider split, account-scoped routing and identity translation, SpotiFLAC-style registries, Apple MusicKit, and the SDK v1 package/runtime contract.
- [metadata-matching-and-placement.md](metadata-matching-and-placement.md): MusicBrainz, provider-neutral recording identity, scoped matching, virtual and materialized playlist lifecycle, beets/Picard naming, hardlinks, and add-only managed storage.
- [recommendations-and-intelligence.md](recommendations-and-intelligence.md): Jellyfin InstantMix, AudioMuse-AI, dashboards, and recommendation extension points.
- [runtime-and-compose.md](runtime-and-compose.md): mandatory PostgreSQL runtime and controlled state transfer, filesystem media ownership, durable jobs/outbox, Valkey, modular compose files, sidecars, migrations, backups, and resource profiles.

When a change spans files, update the root decision and every affected owned specification in the same patch. Keep low-level code maps and current behavior in the parent steering files rather than duplicating them here.

## External Source Index

- [Jellyfin OpenAPI index](https://fra1.mirror.jellyfin.org/files/files/openapi/)
- [Jellyfin stable OpenAPI JSON](https://fra1.mirror.jellyfin.org/files/files/openapi/jellyfin-openapi-stable.json)
- [OpenSubsonic API](https://opensubsonic.netlify.app/docs/opensubsonic-api/)
- [OpenSubsonic getLyricsBySongId](https://opensubsonic.netlify.app/docs/endpoints/getlyricsbysongid/)
- [Subsonic API](https://www.subsonic.org/pages/api.jsp)
- [MusicBrainz API](https://musicbrainz.org/doc/MusicBrainz_API)
- [beets path formats](https://beets.readthedocs.io/en/stable/reference/pathformat.html)
- [Picard file naming scripts](https://picard-docs.musicbrainz.org/en/latest/tutorials/naming_script.html)
- [SpotiFLAC extension repository](https://github.com/spotiflacapp/SpotiFLAC-Extension)
- [SpotiFLAC registry](https://raw.githubusercontent.com/spotiflacapp/SpotiFLAC-Extension/main/registry.json)
- [SpotiFLAC docs](https://spotiflac.zarz.moe/docs)
- [Apple Music API](https://developer.apple.com/documentation/applemusicapi/)
- [MusicKit](https://developer.apple.com/documentation/MusicKit/)
- [MusicKit Song.hasLyrics](https://developer.apple.com/documentation/musickit/song/haslyrics)
- [AudioMuse-AI](https://github.com/NeptuneHub/AudioMuse-AI)
- [AudioMuse Jellyfin plugin](https://github.com/NeptuneHub/audiomuse-ai-plugin)
- [Jellyfin plugins docs](https://jellyfin.org/docs/general/server/plugins/)
- [Postgres Docker image](https://hub.docker.com/_/postgres)
- [Docker Compose profiles](https://docs.docker.com/compose/how-tos/profiles/)
- [Docker multiple compose files](https://docs.docker.com/compose/how-tos/multiple-compose-files/)
- [Npgsql EF Core](https://www.npgsql.org/efcore/)

## Local Source Index

- [apis/specifications/jellyfin/openapi-12.0.0.json](../../../apis/specifications/jellyfin/openapi-12.0.0.json)
- [octo-fiesta at the pinned reference revision](https://github.com/V1ck3s/octo-fiesta/tree/a1ec833fc9805db6a5170a1a777a39534dae0eef)
- [Jellyfin Last.fm plugin at the pinned reference revision](https://github.com/danielfariati/jellyfin-plugin-lastfm/tree/8e060337953b52d2683aab4dc8c9c6fb7383ddf7)
- [allstarr/Services/Common/ExtensionManager.cs](../../../allstarr/Services/Common/ExtensionManager.cs)
- [allstarr/Services/Common/MultiProviderMetadataService.cs](../../../allstarr/Services/Common/MultiProviderMetadataService.cs)
- [allstarr/Services/Common/MultiProviderDownloadService.cs](../../../allstarr/Services/Common/MultiProviderDownloadService.cs)
- [allstarr/Services/Common/ProviderStatusManager.cs](../../../allstarr/Services/Common/ProviderStatusManager.cs)
- [allstarr/Services/MusicBrainz/MusicBrainzService.cs](../../../allstarr/Services/MusicBrainz/MusicBrainzService.cs)
- [allstarr/Services/Common/FuzzyMatcher.cs](../../../allstarr/Services/Common/FuzzyMatcher.cs)
- [allstarr/Controllers/JellyfinController.Audio.cs](../../../allstarr/Controllers/JellyfinController.Audio.cs)
- [allstarr/Controllers/JellyfinController.Search.cs](../../../allstarr/Controllers/JellyfinController.Search.cs)
- [allstarr/Controllers/JellyfinController.PlaylistHandler.cs](../../../allstarr/Controllers/JellyfinController.PlaylistHandler.cs)
- [allstarr/Controllers/JellyfinController.Spotify.cs](../../../allstarr/Controllers/JellyfinController.Spotify.cs)

## API Reference Ownership

`apis/` contains only deliberate, versioned upstream specifications used by tests. The Jellyfin document above is
pinned by version, source URL, and SHA-256 in
[`protocol-source-lock.json`](../../../allstarr.Tests/Fixtures/Protocols/protocol-source-lock.json). Never silently
replace it under the same filename. Review the upstream diff, update the lock, and update affected fixtures and
support-matrix rows together.

Do not put private captures, provider responses, temporary scripts, cloned repositories, or generated notes in
`apis/`. Live captures can contain tokens, account identifiers, signed media URLs, listening history, library
metadata, or copyrighted payloads. Keep temporary research outside the repository. Reduce behavior needed for
permanent coverage to the smallest synthetic fixture under `allstarr.Tests/Fixtures` and record its provenance.
