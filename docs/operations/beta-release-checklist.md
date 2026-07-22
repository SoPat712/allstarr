# Version 3.1 Beta Release Checklist

This checklist is the release gate for `v3.1.0-beta.1`. It complements the
[legacy `.env` import contract](legacy-env-import.md), the
[deployment profiles](deployment-profiles.md), and the
[WebUI engineering standard](../steering/webui-engineering.md). A beta image is
not ready because it builds; every required item below needs recorded evidence
from the release candidate.

## Product Boundary

The beta is provider-neutral streaming and playlist middleware. It connects a
user-owned provider playlist to a Jellyfin or Subsonic-compatible target,
matches each source track against the local library first, and then follows the
configured provider routes for missing tracks.

The beta does not manage a music library, infer or rewrite folder naming
schemes, organize files with Beets, or use MusicBrainz to mutate a library.
Those ideas remain outside the release scope. `Playlist links` is the internal
durable model; the user-facing workflow is simply **Playlists** and must not
expose raw link IDs or duplicate an `Injected playlists` concept.

## Release Candidate Identity

- [ ] The application, image, sidebar, diagnostics export, and release notes
  report `v3.1.0-beta.1` from one canonical version source.
- [ ] The Git commit, image digest, database migration set, extension SDK
  version, Compose files, and Apple gateway lock are recorded together.
- [ ] Release notes identify beta limitations, known provider restrictions,
  migration requirements, and the rollback procedure.
- [ ] No unpublished or mutable extension, sidecar, package, or base-image tag
  is required to reproduce the candidate.

## Supported Playlist Journey

- [ ] The Playlists page has one consistent tab/navigation treatment and no
  duplicate `Playlist links`, `Injected`, or `External playlists` workflows.
- [ ] The create flow is **Source, Target, Behavior, Review** and hides target
  credentials and protocol-specific fields that do not apply.
- [ ] Source discovery lists every enabled account with playlist capability,
  supports search and cursor pagination, and shows playlist artwork or a stable
  fallback icon.
- [ ] Target discovery lists the signed-in tenant's Jellyfin or Subsonic
  playlists and shows artwork or a stable fallback icon.
- [ ] Review performs a no-write preview and reports total, local matches,
  provider matches, unresolved tracks, target changes, and warnings before the
  first mutation.
- [ ] Create, sync, resync, pause, edit behavior, and remove are available from
  the same workflow with an explicit destructive confirmation where required.
- [ ] A missing or failed optional provider degrades only that route and leaves
  local matching and other providers usable.
- [ ] Desktop, tablet, and narrow mobile layouts keep tracks, status, last sync,
  primary action, overflow menu, and pagination visible and operable.

## Apple Music

- [ ] Built-in Apple MusicKit account connection accepts and encrypts a user's
  media-user token without logging, exporting, or sharing it globally.
- [ ] Apple playlist intake uses the user account and supports playlist browse,
  search, artwork, pagination, and track retrieval.
- [ ] Apple download gateway authentication remains a separate account and is
  never presented as MusicKit authorization.
- [ ] Lyrics are routed through an enabled lyrics-capable provider or extension;
  the MusicKit media-user token is not described as a lyrics provider unless a
  tested Apple endpoint actually supplies the requested lyrics.
- [ ] Expired/revoked MusicKit and download sessions produce separate,
  actionable recovery messages.

## Provider Diagnostics

- [ ] Every provider/account capability test records endpoint latency and shows
  an accessible four-bar quality meter with exact milliseconds and measurement
  time in its tooltip or details.
- [ ] Bar thresholds and labels are defined once and shared by all diagnostics
  views; failures display zero bars rather than a misleading latency class.
- [ ] Deep click-to-stream testing is opt-in, names the test track, performs a
  real resolve/download/stream probe, and reports resolve time, first-byte time,
  throughput, estimated click-to-stream time, selected route, and cache state.
- [ ] Deep tests have strict timeout, cancellation, size, concurrency, and
  cleanup limits and do not silently keep or add test media to a user's library.
- [ ] Provider diagnostics distinguish metadata, playlists, streaming,
  download, lyrics, and scrobbling instead of treating one healthy endpoint as
  proof of every capability.

## Extensions

- [ ] Install supports registry selection, version/checksum review, permission
  review, activation, update, rollback, disable, and uninstall.
- [ ] Uninstall confirms whether provider accounts are retained, uses optimistic
  revision checks, removes package content, and leaves an auditable result.
- [ ] Capability badges use the shared icon system and human-readable labels;
  permissions are separated from capabilities and explain their effect.
- [ ] Extension list columns align at supported widths and collapse into the
  standard responsive repeated-data pattern rather than horizontal clipping.
- [ ] Registry and package failures explain whether the failure was trust,
  compatibility, download, checksum, permission, activation, or runtime health.
- [ ] The extension activity panel contains readable extension lifecycle events,
  not implementation event codes such as `Runtime.Log`.

## Event Log

- [ ] The sidebar uses **Event log** below Sources instead of an ambiguous
  Activity destination.
- [ ] Major playlist, matching, cache, streaming, download, scrobble, provider,
  extension, job, and administrative events have a stable event type, severity,
  actor/scope, timestamp, correlation ID, and readable summary.
- [ ] Track-level events show safe title/artist context when allowed and never
  expose credentials, session tokens, raw cookies, private URLs, or unbounded
  payloads.
- [ ] Filters cover time, severity, category, provider, playlist, and correlation
  ID; pagination is bounded and newest-first.
- [ ] High-volume events are sampled or summarized so playback and matching do
  not turn the durable database into an unbounded request log.

## Performance And Reliability

- [ ] Provider fan-out, playlist discovery, artwork retrieval, matching, and
  deep diagnostics have bounded concurrency and cancellation propagation.
- [ ] Matching does not scan or rewrite a global mapping document per track;
  lookups are indexed/batched and writes are idempotent.
- [ ] Playlist sync avoids per-track database and provider round trips when a
  batch endpoint or one prefetched lookup can satisfy the operation.
- [ ] Repeated-data APIs use cursor or stable bounded pagination and do not load
  an unbounded tenant history into memory.
- [ ] The release evidence includes representative cold and warm timings for a
  50-, 500-, and 5,000-track playlist, provider fan-out counts, query counts,
  allocation/working-set observations, and the slowest five operations.
- [ ] Cancellation, provider timeout, database interruption, duplicate job
  delivery, and application restart leave playlist and matching state
  recoverable and do not duplicate target tracks.

## WebUI Contract

- [ ] All changed views follow
  [`docs/steering/webui-engineering.md`](../steering/webui-engineering.md) and
  [`docs/design/webui-design-system.md`](../design/webui-design-system.md).
- [ ] Tabs, buttons, dialogs, disclosures, tables/repeated data, empty states,
  status chips, artwork, connectivity meters, and overflow menus reuse shared
  primitives rather than page-local variants.
- [ ] Keyboard navigation, focus visibility, labels, dialog focus management,
  reduced motion, contrast, zoom to 200%, and screen-reader names are checked.
- [ ] Loading, empty, partial, stale, permission-denied, provider-down, and retry
  states are designed rather than represented by blank content.
- [ ] Browser verification covers current Chromium, Firefox, and Safari engines
  at 360, 390, 768, 1024, and 1440 CSS-pixel widths.

## Migration And Operations

- [ ] A real redacted 2.x `.env` completes preview and apply according to the
  [legacy import contract](legacy-env-import.md), with unknown and deprecated
  keys reported rather than guessed.
- [ ] The old deployment is stopped before v3 can write media; v2 and v3 never
  share writable download, kept, cache, or managed-library roots.
- [ ] Personal accounts are reconnected by their owners, imported shared
  accounts remain disabled until reviewed, and playlist handoffs are recreated
  through the provider-neutral wizard.
- [ ] A verified Postgres backup, matching key ring backup, media backup, prior
  image reference, and isolated restore rehearsal exist before cutover.
- [ ] Upgrade, restart, backup, restore, profile enable/disable, and rollback are
  rehearsed with the same Compose profile intended for beta users.

## Required Automated Gates

- [ ] Formatting, compiler warnings policy, unit tests, integration tests,
  migration tests, WebUI tests, accessibility checks, and container health smoke
  checks pass from a clean checkout.
- [ ] Native Postgres tests cover current migrations, optimistic concurrency,
  playlist creation/preview/sync, event pagination, extension lifecycle, and
  legacy import transaction rollback.
- [ ] Provider contract tests cover success, empty, pagination, throttling,
  timeout, cancellation, malformed response, expired credentials, and capability
  mismatch without requiring production credentials in CI.
- [ ] A release-candidate deployment passes authenticated browser smoke tests for
  login, provider setup, playlist wizard, preview, sync, event log, extension
  lifecycle, settings, backup, and redaction.

## Sign-Off Evidence

Create one release evidence record that links each checked item to a CI run,
test report, screenshot, benchmark artifact, migration report, backup manifest,
or named manual verifier. Items without evidence remain incomplete. Record known
issues with severity, workaround, owner, and intended release; do not convert a
failed required gate into an undocumented beta limitation.

Every successful main CI job uploads `release-manifest-<commit>` with the
canonical application version, commit, clean/dirty state, and SHA-256 digests
for database migrations, Compose inputs, first-party extension locks, Apple
gateway locks, and WebUI dependencies. Attach that artifact to the candidate
record; do not regenerate it from a different checkout.

Start from the
[release evidence template](release-evidence-template.md) and commit the filled
record under `docs/releases/evidence/` with the release candidate.
