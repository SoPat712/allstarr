# Media metadata cache ownership

This inventory assigns one future cache owner to reusable playlist, track, album, artist,
and artwork payloads rendered by the WebUI. It complements
[`redis-key-inventory.md`](redis-key-inventory.md): that document describes current
Valkey and file families, while this one describes request surfaces that currently fetch
or retain the same provider resource independently.

## Ownership contract

`IMediaMetadataCache` is the sole future owner of reusable media metadata. Structured
metadata is a reconstructable, expiring PostgreSQL cache record. Artwork bytes and
generated variants live in bounded disk/object storage; PostgreSQL stores only identity,
provenance, validators, dimensions, byte counts, expiry, and the storage reference.
Process memory may hold a small measured hot set, never an unbounded second source of
truth.

Stable keys contain:

`tenant / authorization-scope / account / storefront / provider / resource-kind / provider-resource-id-or-canonical-recording-id / revision / variant`

Keys never contain credentials, cookies, bearer tokens, signed URLs, raw user queries,
or display names. Global public catalog data may omit user/account scope only when the
provider contract proves it is identical across accounts and storefronts.

## Fetch and ownership inventory

| Surface | Current fetch or retained payload | Current owner / duplication | Future stable identity and owner | TTL and invalidation | Negative cache and fallback |
| --- | --- | --- | --- | --- | --- |
| Add playlist: source results | Provider playlist summaries, owner, order, track count, revision, artwork reference | `PlaylistLinksController` calls the selected `IProviderPlaylistCapability` on each browse; provider adapters may retain their own references | `IMediaMetadataCache` playlist-summary entry keyed by tenant, selected account, storefront, provider playlist ID, and provider revision | 5 minutes while browsing; invalidate on account revision, source refresh, provider revision/ETag change, or explicit refresh | Coalesce identical pages; cache typed not-found/forbidden briefly (30 seconds); show the last still-valid page as stale while one refresh runs |
| Add playlist: source artwork | Up to 4 MiB from `SourcePlaylistArtwork`; Spotify separately keeps a 30-minute process dictionary of resolved artwork URLs | Provider adapter plus `SpotifyPathfinderPlaylistClient` memory dictionary plus `private, max-age=300` browser cache | Artwork object keyed by the playlist-summary identity and thumbnail variant; metadata record retains provider revision, ETag/Last-Modified, dimensions, bytes, and content type | Thumbnail 24 hours, revalidate after 30 minutes; invalidate when provider revision or validator changes | Cache not-found for 5 minutes; fall back to provider icon/playlist placeholder; never persist expiring signed URLs as durable artwork identity |
| Add playlist: target results and artwork | Jellyfin/Subsonic playlist summaries and backend artwork bytes | Backend playlist target adapter plus a separate five-minute browser response cache | Playlist-summary/artwork entries keyed by tenant, backend instance, verified principal/library scope, backend playlist ID, artwork reference, and variant | Summary 5 minutes; artwork 24 hours with validator revalidation; invalidate on target snapshot/artwork reference change | Coalesce backend requests; 60-second negative entry; fall back to media-server icon |
| Managed playlist list and detail | Playlist metadata, cover, ordered tracks, match totals, provider breakdown, and per-track album URLs | `PlaylistController`, Spotify compatibility fetcher, Valkey playlist/matched rows, and first-track artwork fallback each derive overlapping payloads | Durable source snapshot and canonical match records remain authoritative; `IMediaMetadataCache` owns a derived playlist-view entry keyed by tenant, playlist link/source identity, source revision, target snapshot, policy version, and page | 2 minutes for view pages; invalidate on source refresh, match decision, sync/materialization, target revision, or policy change | Serve the durable snapshot when upstream is unavailable; never fabricate totals from stale aggregates; placeholder cover only after playlist and first-track artwork both miss |
| Mapping review and shared track rows | Source title, artists, album, duration, ISRC, provider IDs, confidence, artwork URL, and current route | Mapping/activity controllers read durable snapshots while each WebUI surface previously rebuilt presentation and retained upstream URLs | Canonical recording metadata entry keyed by tenant, canonical recording ID, metadata revision, and artwork variant; the shared track-row renderer consumes this DTO | Metadata 24 hours; thumbnail 7 days with validator revalidation; invalidate on accepted metadata enrichment, identity merge/split, or provider revision | Coalesce by canonical recording; cache a missing thumbnail for 1 hour; fall back to another verified identity, then provider icon/placeholder |
| Event log details | Event facts plus source/target track text and artwork URL copied from external snapshot payloads | `AdminUiController` projects snapshot URLs into each event response; event records can repeat the same remote URL | Events retain immutable IDs and facts only; they reference canonical recording/provider identity. `IMediaMetadataCache` resolves the current safe thumbnail at read time | Event history never expires through cache cleanup; resolved metadata follows canonical entry TTL | Missing artwork never hides the event; provider icon is the deterministic fallback; cache cleanup cannot alter event text, outcome, or identifiers |
| Playback/activity artwork | Jellyfin metadata and 96px primary image bytes | `JellyfinPlaybackMetadataResolver` has independent metadata and artwork `ConcurrentDictionary` caches; external resolver has no artwork | Canonical/backend track metadata and 96px artwork variant keyed by tenant, backend instance, verified principal, item ID, backend image tag, and variant | Metadata 5 minutes, artwork 24 hours; invalidate on backend item/image-tag revision or account/session revision | Existing short failure TTL becomes a bounded 30-second negative entry; fall through to verified provider identity and then placeholder |
| Provider track/album/artist artwork | Public provider URLs carried in search results and metadata objects | Provider-specific services and Valkey key families cache overlapping model payloads; callers fetch image URLs independently | Provider-resource metadata keyed by provider, storefront/catalog namespace, resource kind, stable external ID, revision, and variant; canonical records reference verified provider identities | Catalog metadata 24 hours, artwork 7 days with HTTP revalidation; invalidate on provider revision or enrichment decision | Bounded negative entry (1 hour for missing art, 30 seconds for transient metadata failure); try another verified identity before placeholder |
| Cached and kept media inventory | File metadata, provider route, codec/quality facts, and optional artwork | Download/file scanners are authoritative for files; WebUI previously had no shared artwork owner | Kept-media metadata remains durable; display metadata references canonical recording and uses the same canonical thumbnail entry | File facts update on scanner revision; artwork follows canonical TTL | File remains manageable when metadata/artwork is absent; filename and provider icon are fallback |

## Coalescing, limits, and cleanup

- One in-flight fetch exists per stable key and variant. Waiters share the result and
  cancellation of one request does not cancel work still needed by other waiters.
- Stale-while-revalidate is allowed only for previously authorized data in the same
  tenant/account/storefront scope.
- Full artwork is lazy. List surfaces request bounded thumbnails (96, 160, or 320 pixels);
  full-size variants are fetched only for an explicit detail view or export.
- Every metadata entry has a payload byte limit. Every artwork variant has byte and pixel
  limits. Global byte/count quotas use deterministic LRU/age cleanup.
- Cleanup removes orphaned variants after their metadata reference expires. It may never
  delete canonical recordings, provider identities, match decisions, playlist links,
  event history, accounts, or kept-media ownership.
- Metrics distinguish metadata hits, artwork hits, negative hits, stale serves,
  coalesced waiters, evictions, upstream requests, and upstream bytes avoided.

## Migration order

1. Introduce the scoped metadata/artwork contract and metrics without changing callers.
2. Route Add playlist and target artwork endpoints through it.
3. Route canonical track rows, mapping review, and event details through canonical
   metadata references.
4. Route playback and provider metadata resolvers through it and remove their private
   dictionaries.
5. Remove legacy Valkey/file readers only after restart, cache-loss, authorization,
   stampede, expiry, stale-revision, and quota-pressure parity tests pass.

