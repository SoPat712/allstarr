# Music ecosystem reference ledger

Status: upstream references and reuse notices.
Last audited: 2026-10-08.

This ledger records which upstream projects informed Allstarr, the exact revisions inspected, and the boundary between a useful idea and a dependency. It exists to prevent repeated archaeology, accidental code copying, and architecture-by-name-dropping. Re-audit a project before relying on a changed contract.

The audit clones are temporary working copies outside this repository. Allstarr does not vendor them, add them as submodules, or depend on their branch heads.

## Catalog and identity

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [MusicBrainz Docker `c8a225f`](https://github.com/metabrainz/musicbrainz-docker/tree/c8a225fab3ea3a4650d8b1e0fab41d98878adc29) | No repository-level license file detected; underlying components and data have their own terms | A replicated MusicBrainz deployment is operationally substantial and does not repair missing upstream metadata | Do not require or deploy it for the first Jellyfin-plus-provider use case. |
| [BrainzMash hearing-aid `c25bd6e`](https://github.com/statichum/brainzmash-hearring-aid/tree/c25bd6e8816592af33cac65c42dff6a35b9e0566) | No repository-level license detected | A community pool exposes a Lidarr-shaped API and a separately approved read-only MusicBrainz-compatible `/ws/2` front door | Use `https://api.brainzmash.cc/ws/2` as the selected canonical upstream after its operator approves Allstarr's honest user agent. Do not copy or deploy the pool software as part of Allstarr. |
| [BrainzMash bootstrap `0db5710`](https://github.com/statichum/brainzmash-bootstrap/tree/0db5710e9e983226fb88e0d30c3ccfd4eb1022f8) | No repository-level license detected | Shows how a contributor node is joined to the pool | Do not use it for an Allstarr-supported deployment. It tracks mutable branches/images, publishes internal services, stores secrets in generated configuration, and lacks the supply-chain and rollback controls required here. |
| [DroppedNeedle `5a337fc`](https://github.com/DroppedNeedle/DroppedNeedle/tree/5a337fc94b3b2edb0b00742dbccce605d93ee1ef) | AGPL-3.0 | It demonstrates separate recording/release identity, manual review, support-only fingerprint evidence, and the approved BrainzMash `/ws/2` consumer contract | Retain those independent matching, source-activation, privacy-disclosure, and client-behavior lessons without copying its catalog architecture or identity. |
| [Aurral `5fb0f3e`](https://github.com/lklynet/aurral/tree/5fb0f3e8c371de1fc5635594c7761a3e05f0ac0d) | MIT | Its Lidarr-shaped BrainzMash client demonstrates bounded concurrency, single-flight requests, caching, health state, and short retry budgets | Reuse resilience lessons in Allstarr's existing MusicBrainz client and canonical catalog owner, but do not add Aurral's second metadata dialect. |
| [Lidarr Metadata `e458ca6`](https://github.com/Lidarr/LidarrAPI.Metadata/tree/e458ca6393236a35411d28450e405fc7efb4c505) | No repository-level license detected | Shows how Lidarr builds rich metadata responses from a MusicBrainz mirror, search indexes, caches, and auxiliary metadata sources | Use as a data-shape and operational reference only. Do not add a second first-release catalog dialect or copy its implementation without permission. |
| [MusicBrainz Picard `efb6972`](https://github.com/metabrainz/picard/tree/efb6972b2d1db0c1a823fd5b379ea8c872b52f4d) | GPL-2.0-or-later | MusicBrainz IDs, release-track identity, file tags, and AcoustID evidence have distinct roles | Preserve those boundaries in Allstarr's independent matcher. A fingerprint supports identity; it does not erase version, edition, or manual-authority conflicts. |

## Playback, routing, and library ownership

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [Music Assistant server `ee38f52`](https://github.com/music-assistant/server/tree/ee38f522a1eb07c5d359566cc8c171538c89b5e2) | Apache-2.0 | A library entity can own mappings to several provider instances without making any provider ID canonical | Keep one Allstarr recording with multiple authorized routes. Let Settings order select playback only after each route independently passes match acceptance. |
| [AudioMuse `e78c87d`](https://github.com/NeptuneHub/AudioMuse-AI/tree/e78c87dccc2afbfb22b3d3264a8b36a50f3e9d3f) | AGPL-3.0 | Local-server identity, content-derived evidence, and recommendation features can be separated | AGPL reuse is acceptable with the same provenance, notice, source-offer, and modification records. Use AudioMuse only as optional evidence/enrichment through the existing Intelligence boundary. Never make it the catalog or routing authority. |
| [DroppedNeedle `5a337fc`](https://github.com/DroppedNeedle/DroppedNeedle/tree/5a337fc94b3b2edb0b00742dbccce605d93ee1ef) | AGPL-3.0 | Acquired files need verification, quarantine, provenance, atomic catalog admission, and eventual native-library reconciliation | Apply those lessons to Allstarr's existing download/keep pipeline instead of introducing a second library manager. Lidarr remains optional. |

## Playlist import

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [Jellyfin Spotify Import `93265c3`](https://github.com/Viperinius/jellyfin-plugin-spotify-import/tree/93265c3834744f4c92b5c6e2f3d8460b8c2b19cb) | GPL-3.0 | Web-player queries support public playlist/profile discovery with anonymous tokens and account-bound library/Liked Songs reads. Public profiles expose ordered playlist URI, owner, name, and revision fields. | Independently implement these verified wire shapes through the existing typed playlist capability and matcher. Do not copy implementation code or add its separate matcher or Premium Web API path. Free-account behavior still requires live qualification. |

## Lyrics

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [LyricsPlus `8b2e1fe`](https://github.com/ibratabian17/LyricsPlus/tree/8b2e1fe3f92536158d471177f7f17f9820407c5b) | Apache-2.0 | V2 title/artist lookup returns plain or millisecond line/word timelines; public misses and busy responses are distinct. | Independently implement the wire contract through the typed lyrics capability, with public-instance fallback or an exclusive operator-selected self-hosted URL. Preserve truthful line timing and use the existing cache. Public availability needs separate qualification. |
| [Navidrome Musixmatch plugin `22dc7c1`](https://github.com/Myzel394/navidrome-musixmatch-plugin/tree/22dc7c1e9c12ba7c900dd8abbd52c74efd9d4310) | MIT | The desktop macro endpoint supplies matched identity, richsync, subtitle and plain representations with nested API statuses. | Independently implement the request and parser using the configured encrypted desktop user token. Keep tokens out of logs and cache keys. Do not copy anonymous-token bootstrap, website scraping or raw-payload debug logging. |

## Listening and scrobbling

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [Multi-Scrobbler `fa2778f`](https://github.com/FoxxMD/multi-scrobbler/tree/fa2778f3b529a10b6996ad67e80d8bc27f825b12) | MIT | Many playback sources can feed selected per-user destinations through normalized play events, duplicate detection, durable ingress/dead queues, retry state, now-playing updates, health, and metrics | Use its event-lifecycle and test matrix as a reference for Allstarr playback observations and scrobble delivery. Allstarr keeps its SQLite durable-job owner; it does not embed Multi-Scrobbler or create another queue. |
| [DroppedNeedle `5a337fc`](https://github.com/DroppedNeedle/DroppedNeedle/tree/5a337fc94b3b2edb0b00742dbccce605d93ee1ef) | AGPL-3.0 | Listener identity, app passwords, per-user discovery accounts, live events, and protocol playback can coexist without making admin credentials global | Keep listener/account isolation in Allstarr's user ownership checks. Provider sharing is an explicit Personal/Shared choice, never inferred from an admin login. |

Multi-Scrobbler is useful for listening-state mechanics, not for canonical music identity. Koito, Maloja, ListenBrainz, Last.fm, Jellyfin, and Subsonic adapters in that project broaden the behavioral fixture matrix; they do not justify adding all of those services as Allstarr dependencies.

## Ideas deliberately rejected

- A provider ID as the primary artist, album, or recording ID.
- A separate canonical database owned by Lidarr, AudioMuse, a scrobbler, or each provider adapter.
- Hidden fallback from one metadata operator to another after a failure.
- Changing a configured catalog endpoint during startup migration.
- Copying an unlicensed implementation because its public HTTP responses can be observed.
- Executing a remote bootstrap script from a mutable branch.
- Using mutable container tags for a catalog release.
- Publishing PostgreSQL, cache, Solr, raw MusicBrainz, or Lidarr Metadata ports to an untrusted network.
- Treating transient failure as authoritative absence or permitting stale work from an old source generation to overwrite a new source.
- Concluding that a recording matches solely from a shared MusicBrainz ID, ISRC, title, or fingerprint when duration/version/manual evidence conflicts.

## Re-audit checklist

Before implementing or upgrading a dependency informed by this ledger:

1. Pin the exact upstream revision, release, and image digests.
2. Re-read the repository license and every data/API term involved.
3. Capture sanitized request, response, paging, redirect, and error fixtures for only the routes Allstarr will call.
4. Compare the upstream contract with Allstarr's existing owner before adding a new abstraction.
5. Record what changed in this ledger and the relevant architecture or operation document.
6. Keep temporary clones and generated audit output outside the repository.

## Deezer implementation notice

Allstarr's Deezer alternative-track resolution and throttle handling adapt the
[Octo-Fiesta implementation at 6841a2ea9c227b212157d81acc4aafa5965419b2](https://github.com/V1ck3s/octo-fiesta/tree/6841a2ea9c227b212157d81acc4aafa5965419b2),
licensed under [GPL-3.0](https://github.com/V1ck3s/octo-fiesta/blob/6841a2ea9c227b212157d81acc4aafa5965419b2/LICENSE).
The upstream files are `octo-fiesta/Services/Deezer/DeezerDownloadService.cs`,
`DeezerMetadataService.cs`, and `DeezerDecryptedStream.cs`. The original download
implementation records its origin as a C# port of DeezerDownloader JavaScript.

Modified for Allstarr on 2026-10-08 in `Providers/Deezer/DeezerMediaClient.cs` and
`DeezerHttpClient.cs`: credentials remain scoped to the current operation;
alternatives require a provider link or an exact ISRC and a readable, consistent
returned ID; the selected ID reaches both download and streaming decryption.
Retries are bounded, honor cancellation and Retry-After, and classify exhausted
quota responses without exposing credentials. Title-based guesses are excluded.
The shared catalog and media client retain Allstarr's existing parser, quality,
artifact ownership, and account authorization boundaries.

These adaptations are distributed under Allstarr's [GPL-3.0 license](../../LICENSE),
with their corresponding source in this repository. Optional provider services
and their media remain separately operated upstream resources.

## SpotiFLAC-Mobile runtime reuse

The extension compatibility wrappers and host functions are adapted from
[SpotiFLAC-Mobile](https://github.com/zarzet/SpotiFLAC-Mobile), inspected 2026-10-10:
`rust_backend/crates/extensions/src/prelude.js`, `file_transform.rs`,
`download/segments.rs`, `signed_session/protocol.rs`, `signed_session/fetch.rs`,
and `rust_backend/crates/core/src/matching.rs`. Allstarr retains its own
invocation paths, permission checks, artifact size limits and cancellation.

The copied portions carry this notice:

```text
MIT License

Copyright (c) 2026 zarzet

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
