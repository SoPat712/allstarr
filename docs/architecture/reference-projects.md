# Music ecosystem reference ledger

Status: contributor research, not shipped behavior.
Last audited: 2026-09-14.

This ledger records which upstream projects informed Allstarr, the exact revisions inspected, and the boundary between a useful idea and a dependency. It exists to prevent repeated archaeology, accidental code copying, and architecture-by-name-dropping. Re-audit a project before relying on a changed contract.

The audit clones are temporary working copies outside this repository. Allstarr does not vendor them, add them as submodules, or depend on their branch heads.

## Catalog and identity

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [MusicBrainz Docker `c8a225f`](https://github.com/metabrainz/musicbrainz-docker/tree/c8a225fab3ea3a4650d8b1e0fab41d98878adc29) | No repository-level license file detected; underlying components and data have their own terms | A replicated MusicBrainz deployment is operationally substantial and does not repair missing upstream metadata | Do not require or deploy it for the first Jellyfin-plus-provider use case. |
| [BrainzMash hearing-aid `c25bd6e`](https://github.com/statichum/brainzmash-hearring-aid/tree/c25bd6e8816592af33cac65c42dff6a35b9e0566) | No repository-level license detected | A community front door can pool MusicBrainz mirrors | Research only. Do not integrate, deploy, copy, or ship BrainzMash as an Allstarr option for the current target. |
| [BrainzMash bootstrap `0db5710`](https://github.com/statichum/brainzmash-bootstrap/tree/0db5710e9e983226fb88e0d30c3ccfd4eb1022f8) | No repository-level license detected | Shows how a contributor node is joined to the pool | Do not use it for an Allstarr-supported deployment. It tracks mutable branches/images, publishes internal services, stores secrets in generated configuration, and lacks the supply-chain and rollback controls required here. |
| [DroppedNeedle `5a337fc`](https://github.com/DroppedNeedle/DroppedNeedle/tree/5a337fc94b3b2edb0b00742dbccce605d93ee1ef) | AGPL-3.0 | It demonstrates separate recording/release identity, manual review, and support-only fingerprint evidence | Retain those independent matching lessons only. Do not adopt its BrainzMash source integration for the current target. |
| [Aurral `5fb0f3e`](https://github.com/lklynet/aurral/tree/5fb0f3e8c371de1fc5635594c7761a3e05f0ac0d) | MIT | Its client demonstrates bounded concurrency, single-flight requests, caching, health state, and short retry budgets | Reuse resilience lessons in existing owners only. Do not adopt its BrainzMash origin or catalog architecture. |
| [Lidarr Metadata `e458ca6`](https://github.com/Lidarr/LidarrAPI.Metadata/tree/e458ca6393236a35411d28450e405fc7efb4c505) | No repository-level license detected | Shows how Lidarr builds rich metadata responses from a MusicBrainz mirror, search indexes, caches, and auxiliary metadata sources | Use as a data-shape and operational reference only. Do not add a second first-release catalog dialect or copy its implementation without permission. |
| [MusicBrainz Picard `efb6972`](https://github.com/metabrainz/picard/tree/efb6972b2d1db0c1a823fd5b379ea8c872b52f4d) | GPL-2.0-or-later | MusicBrainz IDs, release-track identity, file tags, and AcoustID evidence have distinct roles | Preserve those boundaries in Allstarr's independent matcher. A fingerprint supports identity; it does not erase version, edition, or manual-authority conflicts. |

## Playback, routing, and library ownership

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [Music Assistant server `ee38f52`](https://github.com/music-assistant/server/tree/ee38f522a1eb07c5d359566cc8c171538c89b5e2) | Apache-2.0 | A library entity can own mappings to several provider instances without making any provider ID canonical | Keep one Allstarr recording with multiple authorized routes. Let Settings order select playback only after each route independently passes match acceptance. |
| [AudioMuse `e78c87d`](https://github.com/NeptuneHub/AudioMuse-AI/tree/e78c87dccc2afbfb22b3d3264a8b36a50f3e9d3f) | AGPL-3.0 | Local-server identity, content-derived evidence, and recommendation features can be separated | AGPL reuse is acceptable with the same provenance, notice, source-offer, and modification records. Use AudioMuse only as optional evidence/enrichment through the existing Intelligence boundary. Never make it the catalog or routing authority. |
| [DroppedNeedle `5a337fc`](https://github.com/DroppedNeedle/DroppedNeedle/tree/5a337fc94b3b2edb0b00742dbccce605d93ee1ef) | AGPL-3.0 | Acquired files need verification, quarantine, provenance, atomic catalog admission, and eventual native-library reconciliation | Apply those lessons to Allstarr's existing download/keep pipeline instead of introducing a second library manager. Lidarr remains optional. |

## Listening and scrobbling

| Project and audited revision | License found | What it proves | Allstarr decision |
| --- | --- | --- | --- |
| [Multi-Scrobbler `fa2778f`](https://github.com/FoxxMD/multi-scrobbler/tree/fa2778f3b529a10b6996ad67e80d8bc27f825b12) | MIT | Many playback sources can feed selected per-user destinations through normalized play events, duplicate detection, durable ingress/dead queues, retry state, now-playing updates, health, and metrics | Use its event-lifecycle and test matrix as a reference for Allstarr playback observations and scrobble delivery. Allstarr keeps its PostgreSQL durable-job/outbox owner; it does not embed Multi-Scrobbler or create another queue. |
| [DroppedNeedle `5a337fc`](https://github.com/DroppedNeedle/DroppedNeedle/tree/5a337fc94b3b2edb0b00742dbccce605d93ee1ef) | AGPL-3.0 | Listener identity, app passwords, per-user discovery accounts, live events, and protocol playback can coexist without making admin credentials global | Keep listener/account isolation in Allstarr's existing tenant/user/account scopes. Provider sharing is an explicit scope choice, never inferred from an admin login. |

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
