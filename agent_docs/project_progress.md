# Project Progress

## Active package — rich control dashboard and provider parity

### Outcome

Turn the existing administrator WebUI into one coherent, data-rich music control dashboard while preserving native Jellyfin/Subsonic fidelity and the existing provider-neutral backend owners.

This is a replacement and consolidation package. Do not create parallel matching, routing, scrobbling, caching, scheduling, or recommendation systems. Delete each old UI surface after its replacement passes.

### Verified starting state

- Branch, local HEAD, and `origin/dev` are all `07bc3c45124beebbe28b9b7131988fd75883e15c`.
- That revision is already deployed and its last CI, release, protocol, and responsive gates are green.
- Current WebUI baseline is 45.3 KiB initial JavaScript and 21.3 KiB CSS; unit is 46/46 and browser is 84/84.
- Canvas UI remains blocked by MIT plus Commons Clause redistribution terms. A focused replacement review found no permissive renderer that would delete more code than it adds, so use Svelte 5, shadcn-svelte, Bits UI, Lucide, CSS, SVG, and native Web Animations.
- Koito `a079fa693569d21e03c00df163f20ac5e137c490`, Explo `4fc75874de691ff1e26b10d88b859cfac8ee2992`, and Multi-Scrobbler `bc28de66b14db1c99eb79ad75d1cdf4c9dfff7cc` are MIT reference inputs. Adapt useful behavior and presentation into existing owners; do not import their application architectures.
- LAN/VPN access to `192.168.1.116` is off. Complete every local gate first, then ask the user to enable it for exact-revision deployment and live qualification only.

### Product rules

- Show every useful supported fact, using overview → expansion → detail so primary screens remain scannable.
- Never display invented popularity, media, listening, readiness, latency, or provider facts.
- Work only from observed production behavior, captured client shapes, public protocol contracts, and donor workflows deliberately adopted here. Do not build speculative edge cases.
- Preserve authentication, authorization, secret redaction, destructive-operation, migration, data-loss, and protocol safety coverage.
- Matched native items relay the complete backend object unchanged. Virtual objects expose every available field with stable, internally consistent relationships.

## Phase 0 — package and donor baseline

- [x] Record exact donor UI paths, adaptation boundaries, licenses, and destination owners in the reference ledger.
- [x] Capture current route request counts, response sizes, render timings, long tasks, bundle sizes, and table geometry.
- [x] Record the old Signal Boot implementation and current shared UI owners before editing.

Acceptance: reproducible before-state evidence exists and no donor code enters production without provenance.

## Phase 1 — shared shell, tables, motion, and visual language

- [x] Restore Signal Boot for real authentication/bootstrap work with no artificial delay, reduced motion, and retryable failure state.
- [x] Standardize desktop rows, artwork, numeric columns, actions, gutters, and mobile cards across playlists, Integrations, Activity, Mappings, Cached, Kept, and Intelligence history.
- [x] Centralize provider colors/icons so Jellyfin, Deezer, Apple, Spotify, YouTube, and extensions remain distinguishable.
- [x] Add restrained native motion for focused artwork, mapping decisions, delivered scrobbles, and expanding detail; never reorder content under the pointer.
- [x] Delete superseded per-page table, loading, icon, and animation CSS.

Acceptance: geometry is aligned at desktop/tablet/mobile, keyboard and reduced-motion behavior pass, and no renderer dependency is added.

## Phase 2 — unified Integrations hub

- [x] Rename Sources to Integrations with Services, Accounts, Extensions, and Routing tabs.
- [x] Group logical services with their built-in, extension, sidecar, backend, and account implementations.
- [x] Put configuration beside the exact implementation; keep lifecycle/permissions in Extensions and secrets/audience in Accounts.
- [x] Move quality, provider ordering, local preference, and extension penalty to Routing; leave only app-wide system/maintenance controls in Settings.
- [x] Add stable service/implementation projection facts and deep links; redirect old Sources/Accounts/Extensions routes.
- [x] Show capability coverage, readiness, latest probe, CTS, managed p95, last failure, account scope, version, and routing priority without ambiguous blanks.
- [x] Delete duplicate provider/account/extension/settings forms after the replacement passes.

Acceptance: one route owns discovery, setup, health, accounts, configuration, extensions, and routing without duplicating durable state.

## Phase 3 — Intelligence and imports

- [x] Replace the five equal tabs with Overview, History & Imports, Discover, and Automation.
- [x] Adapt Koito period controls, heatmap, totals, streaks, top music, history density, and recap presentation.
- [x] Adapt Explo recommendation, generated-playlist, schedule, run-now, next-run, and prior-run presentation.
- [x] Redesign imports as multi-file drag/drop with automatic previews, valid files selected by default, one batch summary, per-file detail, and Add all ready files.
- [x] Add bounded grouped daily/monthly and source/provider/client aggregates using the existing listening-occurrence authority.
- [x] Refresh Overview, History, and Discover after imports and replace generic prerequisites with exact Integrations deep links.

Acceptance: imported and live history agree, recommendations use saved data, and rich analytics require no second database.

## Phase 4 — Home and Activity

- [x] Add one aggregate Home read endpoint composed from existing owners while retaining individual endpoints for compatibility.
- [x] Show active listeners, playlist/playable/unresolved totals, cache/kept usage, playable-source health, jobs, scrobbles, recent trend, top music, and actionable setup problems.
- [x] Expand now playing with user/client/device, actual implementation, cache route, progress, scrobble threshold, and per-target delivery state.
- [x] Reuse existing delivery checkpoints/activity state; add no second persistence or realtime channel.
- [x] Adapt Multi-Scrobbler status, now-playing, retry, and per-target outcome patterns.
- [x] Redesign Activity with colored icons, provider accents, rich filters, concise summaries, retry/auth state, and expandable redacted details.

Acceptance: Home loads through the aggregate plus now-playing update, and every stat and scrobble mark reflects durable state.

## Phase 5 — Mappings, Cached, and Kept

- [x] Search verified identities first, then run title+artist, title+album, title, artist, and album queries and score the deduplicated union.
- [x] Prevent weak artist mismatches from becoming automatic suggestions and keep every credible candidate accessible with complete scoring evidence.
- [x] Bump the matcher revision and use the existing preview/rematch job for unresolved, suggested, ambiguous, and stale decisions only.
- [x] Add the Selena Gomez `Crush` regression and protect accepted, pinned, rejected, and manual decisions.
- [x] Present Mappings as All, Review, Unresolved, and History with full score/identity/route details on expansion; accepted rows leave Review immediately.
- [x] Add cache/kept totals, provider/lifecycle facts, last access, expiry, quality, publication, references, filters, and previewed bulk actions.
- [x] Reindex only completed files with valid Allstarr ownership metadata; show unknown files as diagnostics and never adopt/delete them automatically.

Acceptance: automatic suggestions are credible, manual decisions remain authoritative, and Cached/Kept agree with managed-file owners.

## Phase 6 — external object, streaming, and lyrics parity

- [x] Use one provider-neutral external relationship projection for primary albums, credited tracks, and Appears On in Jellyfin and Subsonic.
- [x] Keep external IDs, artwork, relationships, pagination, and traversal stable and internally consistent.
- [x] Discover and qualify every ready streaming or download-backed implementation dynamically.
- [x] Verify metadata → PlaybackInfo → bounded audio → range/cancellation → artwork → lyrics while recording the selected implementation/account.
- [x] Preserve configured quality and return a truthful failure instead of silently substituting another track.
- [x] Run source-native lyrics first, then Odesli identity translation and distinct configured fallbacks without downloading media merely to find lyrics.

Acceptance: native objects remain exact, virtual objects satisfy the full client contract, unrelated Appears On albums fail, and real external playback failures are classified.

## Phase 7 — performance, release, and live delivery

- [ ] Keep initial JS/CSS within existing budgets and reject unexplained growth above 10%; keep route chunks under 100 KiB gzip.
- [ ] Use grouped queries, lazy routes/artwork, keyed row updates, and off-screen content visibility; add no charting/rendering/virtualization framework.
- [ ] Expand the Jellyfin kit to union-key native comparison, native artwork/count parity, dynamic external traversal, every ready playable provider, and checked Finer/Feishin/Musiver request shapes.
- [ ] Run focused owner tests, WebUI check/unit/build/budget/E2E, both PostgreSQL lanes, format, Apple gateway, Compose, shell, and deterministic protocol kits.
- [ ] Ask the user to enable LAN/VPN only after the exact final SHA is locally green.
- [ ] After exact-revision authorization, push, deploy, run bounded browser/provider/client qualification, and record the deployed SHA.

Acceptance: local release evidence is complete before LAN access, no unrun gate is called passing, and live failures are separated into provider/configuration versus Allstarr defects.

## Next action

Implement Phase 6 locally: external relationship parity, dynamic playable-provider qualification, truthful playback failures, and provider-neutral lyrics fallback. Then complete the release matrix before requesting LAN/VPN access.

---

## Completed reference — deletion-first consolidation and Canvas UI redesign

### Outcome

Reduce Allstarr's handwritten code and test burden, replace repeated WebUI styling with the libraries already configured for the project, and redesign the main control-dashboard routes with a small, deliberate set of Canvas UI effects.

This is a deletion package first. A large file is not automatically bad, and low coverage alone does not prove code is dead. Every removal needs caller, composition, contract, and test evidence.

### Verified starting evidence

- The counted source/test/tool surface is 313,733 lines. Generated EF migration designers and the model snapshot account for 100,897 lines and are excluded from handwritten-code reduction targets.
- `webui/src/app.css` is 7,124 lines. WebUI Svelte, TypeScript, and CSS total 19,880 lines.
- The WebUI already has Tailwind 4, Bits UI, Lucide, and a valid `components.json` for shadcn-svelte. The redesign should finish using that stack instead of adding another control library.
- The WebUI repeats at least 120 button-class uses and 61 panel-class uses. `relativeTime`, provider lookup/label logic, polling setup, and humanization are repeated across routes.
- The current hand-built playlist glass effect adds about 70 CSS lines plus four markup hooks. It must be removed before the Canvas UI version lands.
- SquidWTF is not composed in `Program.cs`, but 2,473 production lines, 1,486 direct test lines, settings, compatibility branches, and provider labels remain.
- `RoundRobinFallbackHelper` and `EndpointBenchmarkService` have no production owner outside the retired SquidWTF slice.
- The .NET suite has 1,707 Fact/Theory attributes, no skip markers, eight delay markers, and 18 files that inspect methods through reflection.
- Several provider tests pass without awaiting the operation or asserting an outcome. These are false coverage and are listed in Phase 2.
- Canvas UI ships Svelte 5 source through the shadcn registry. Its HTML-in-canvas effects are experimental and its source uses MIT plus Commons Clause terms.

### Non-negotiable keep rules

- Keep authentication, authorization, secret redaction, outbound-request safety, filesystem safety, and destructive-operation tests.
- Keep Jellyfin/Subsonic protocol fixtures and direct object-parity checks unless the same observable contract is proven elsewhere.
- Keep PostgreSQL migration lineage, rollback, corruption rejection, durable queue, state transfer, and data-loss tests.
- Keep playlist create, edit, reorder, share, sync, rollback, and delete safety coverage.
- Do not squash or delete generated EF migrations in this package. That needs a separate production-upgrade and backup decision.
- Do not remove a public route, stored field, environment migration, or protocol shape merely because local coverage did not hit it.
- Preserve the current dirty worktree. Do not overwrite unrelated or user-owned edits.
- Track handwritten application code and vendored library source separately so a library import cannot hide net growth.

## Phase 0 — establish a truthful baseline

- [x] Add the explicit .NET test-project marker if current CLI discovery still depends on an implicit/false-green path. `dotnet test --list-tests` discovers the suite without one, so no marker was added.
- [x] Run list/discovery checks before edits and record the real .NET, WebUI unit, E2E, Apple gateway, and Compose inventories.
- [x] Run the current release lanes with PostgreSQL in OrbStack and capture slowest tests and total time.
- [x] Make Playwright emit machine-readable per-test timings so the timing report can show slow E2E cases.
- [x] Add a generous configurable watchdog to the timing wrapper only after recording the longest protected migration/state-transfer lane.
- [x] Record handwritten lines, generated lines, initial JS/CSS gzip size, route chunk sizes, CSS selectors, and browser performance before redesign work.
- [x] Record Canvas UI's exact commit, imported files, dependencies, and license text. Confirm that publishing the selected source inside GPL-3.0 Allstarr is permitted before merging it. If needed, obtain written permission from the author.
- [x] Write the visual brief in `DESIGN.md`: Allstarr is a music control room, not a generic glass SaaS page; effects support state and focus, never replace readable DOM controls.

### Phase 0 evidence

- .NET discovery lists 2,354 tests. The main release lane, excluding only durable state transfer, passes 2,282/2,282 in about 80 seconds against isolated PostgreSQL in OrbStack. The separate state-transfer lane passes 90/90 in about 106 seconds. Both exceed the 60-second target and remain optimization work.
- The prior single main-lane failure was the runtime ownership contract missing the newly composed `ManagedTrackCacheService`; the service now has an explicit `managed audio cache` owner and the full lane passes.
- WebUI baseline: check passes with zero diagnostics, unit 44/44, build and budget pass, E2E 130/130 in about 55 seconds. Initial gzip is 52.6 KiB JS and 21.2 KiB CSS; the largest measured route chunk is Playlists at 76.1 KiB.
- Apple gateway baseline is 19/19. Default, Spotify, and Apple Compose profiles all pass `docker compose config --quiet`.
- Counted source/test/tool surface is 313,733 lines, including 100,897 generated EF migration/snapshot lines. WebUI Svelte/TypeScript/CSS is 19,880 lines; `app.css` is 7,124 lines.
- `timing_report.py --self-test` passes. A focused Playwright proof reports one discovered/executed/passed case and its exact 650 ms result timing; CI now supplies the JSON output path. The child watchdog defaults to 600 seconds, above the measured 106-second protected state-transfer lane.
- Canvas UI main was inspected at `2dd45d70394b890a8130740061cdcc957e89dc35` and its current upstream license was rechecked on 2026-08-17. It remains MIT plus Commons Clause and restricts redistribution of the components themselves. Vendoring into GPL-3.0 Allstarr remains blocked pending a compatible grant or written permission; no Canvas UI source has been copied.

Acceptance: every later deletion has a real before-state, the test runner cannot pass with zero tests, and Canvas UI has a recorded legal/provenance decision.

## Phase 1 — delete unquestionably dead production slices

- [x] Remove the entire retired SquidWTF implementation, startup validator, endpoint discovery/catalog, configuration defaults, legacy environment mappings, special provider branches, Odesli alias, status filtering, documentation, and direct tests.
- [x] After the SquidWTF removal compiles, remove `RoundRobinFallbackHelper`, `EndpointBenchmarkService`, their registrations, and their now-orphaned tests.
- [x] Keep one composition assertion that SquidWTF is absent only if it guards against accidental re-registration; otherwise delete the historical name entirely.
- [x] Remove provider-label fixture rows that exist only to preserve the retired SquidWTF name; use a live generic extension/provider fixture where the label algorithm still needs coverage.
- [x] Delete the hand-built `.glass-object` CSS and the four class hooks from playlist markup.
- [x] Search configuration, API responses, WebUI catalogs, docs, Compose, and migration readers for other retired names. Delete only complete, uncomposed vertical slices with no supported migration obligation.

Acceptance: no SquidWTF or dead endpoint-racing code remains in shipped assemblies, configuration, UI, docs, or tests; the focused composition/provider lanes pass; the diff is net-negative before the visual redesign begins.

Evidence: application and test projects compile with zero warnings/errors; the focused retired-provider/settings/routing/cache regression set passes 94/94. The old provider name remains only in one host-composition test that proves no registry, metadata service, or download service can compose it. The hand-built glass selector and all four markup hooks are gone; a repository search found no other shipped SquidWTF, endpoint-racing, or glass-object remnants.

## Phase 2 — remove false, duplicate, and wasteful tests

### Delete or rewrite false coverage

- [x] Delete or rewrite `LrclibServiceTests` cases that only assert a Task is non-null without awaiting it. Keep awaited request, result, failure, and cache behavior.
- [x] Delete the SquidWTF test file with its retired production slice.
- [x] Delete or rewrite the Deezer/Qobuz fire-and-forget album tests and `Assert.True(true)` cases. Keep one bounded observable behavior test per real contract.
- [x] Delete Qobuz async quality tests that never await and only assert service construction, or replace them with one parameterized request-format test.
- [x] Replace the LocalLibrary null-provider no-op placeholder with a persisted-store assertion, or delete it if the store contract already proves the behavior.

### Merge duplicate coverage

- [x] Merge duplicate top-level Jellyfin song/album/artist DTO assertions into `JellyfinResponseBuilderTests`; retain unique nested media-source, stream, and tree-shape checks.
- [x] Reduce the repeated Playwright route matrix to mobile and desktop representatives while keeping explicit breakpoint-boundary, keyboard, accessibility, destructive, and overflow checks.
- [x] Convert repeated cases to table-driven tests when that deletes setup without hiding which contract failed.
- [x] Do not split a large fixture merely because it is large. Delete duplication first.

### Remove tests of private implementation details

- [x] Replace reflection tests for search limits, search interleaving, route parsing, image tag extraction, and query redaction with public controller/adapter behavior where that behavior is not already covered.
- [x] Delete a reflection test only after an observable contract proves the same rule.
- [x] Keep security-sensitive redaction and image-auth rules even if the replacement is not smaller.
- [x] Replace low-risk source-text assertions with behavior tests; retain source-absence guards where absence itself is the security or legacy-retirement contract.

### Slow-test policy

- [x] Treat tests over three seconds as release-critical only. Keep migration lineage/rollback, durable queue/state transfer, corruption/data-loss, playlist mutation, and the 10,000-track bound.
- [x] Move protected slow integration cases out of the fast PR lane when isolation allows it; do not hide them from the release gate.
- [x] Replace arbitrary waits with fake time, a completion signal, or deadline polling. Preserve live smoke's stable-five and exact-ID cleanup semantics.
- [x] Report every removed test by old contract and replacement evidence; never use a lower test count as the success metric by itself.

Acceptance: no test succeeds on an unawaited operation or placeholder assertion, per-test timing is visible, protected coverage remains, and both fast and release lanes have explicit inventories.

Evidence: the five LRC no-op cases are now three awaited request/cache contracts; the Deezer/Qobuz fire-and-forget and construction-only cases and the LocalLibrary placeholder are deleted. Focused provider tests pass 48/48. Duplicate top-level Jellyfin DTO assertions moved into the builder suite while nested response-shape coverage remains; focused response tests pass 60/60. The representative Playwright matrix now discovers 84 cases instead of 130 while retaining explicit breakpoint and interaction cases. Six reflection-heavy Jellyfin test owners now call explicit internal pure seams, eliminating unsafe uninitialized-controller construction; Search/Hints now captures the real upstream path/query, and the focused set passes 46/46. Spotify Pathfinder persisted-query coverage now asserts the emitted HTTP query instead of reading production source, and passes 3/3. The final fast lane passes 2,208/2,208 and the 100-case release-critical lane passes 100/100; release-critical retains every measured over-three-second migration, data-loss, durable-queue, operator-transfer, and 10,000-track contract. Live Jellyfin consistency waits now use a configurable elapsed deadline with 100 ms polling while preserving five stable observations and exact-ID cleanup.

Additional deletion evidence: the six source-reading protocol-gateway contracts were deleted after the observable provider-streaming, provider-playlist, and route-fixture lanes covered their routing and actor/account behavior; those replacement lanes pass 116/116. Six low-value operational source-text assertions were also removed: timeout/cancellation behavior is covered by awaited `MultiProviderMetadataServiceTests`, EF admin-session persistence by `AdminAuthSessionServiceTests`, and the remaining security/authority source-absence guards stay in place. The Spotify Pathfinder playlist-filter source assertion was moved into the emitted-request test, the provider URL cases are one table-driven contract, and stale references to the deleted gateway test were removed from the support matrix and catalog. The large protocol fixtures remain intact because they are the observable compatibility contract, not duplicated setup.

## Phase 3 — consolidate the WebUI on shadcn-svelte and existing libraries

- [x] Use the existing `components.json` and install only the shadcn-svelte controls Allstarr actually uses: button, input, textarea, checkbox, select, badge, card, dialog/alert-dialog, dropdown menu, tabs, tooltip, table, progress, skeleton, scroll area, and toast/feedback if needed.
- [x] Keep Bits UI as the accessible behavior layer under shadcn-svelte. Do not add a second primitive/control framework.
- [x] Migrate shared controls first, then delete the matching global `button-*`, `status-pill`, dialog, menu, and checkbox CSS. Keep the small shared `panel`, notice, and feedback surfaces where a component wrapper would add code.
- [x] Use one badge/status component with named tones instead of adjacent unstyled state text.
- [x] Use one dialog shell with consistent header, body, footer, padding, radius, focus trap, and mobile behavior.
- [x] Use one table/surface gutter rule so headers and rows align across Sources, Activity, mappings, downloads, and playlists.
- [x] Consolidate duplicate `humanize`, relative-time formatting, provider label lookup, and polling lifecycle code into the smallest existing shared owner.
- [x] Remove unused selectors after each route migration. Keep route-specific CSS only for layout that cannot be expressed cleanly by the shared components or Tailwind utilities.
- [x] Measure whether an OpenAPI-generated WebUI client can replace a meaningful portion of the 1,848-line handwritten `api.ts`. Adopt it only if it preserves auth/error behavior and deletes more code than it vendors or generates.

Acceptance: raw Bits UI imports are limited to the shared UI layer, common controls do not depend on page-specific CSS, `app.css` is substantially smaller, and keyboard/screen-reader behavior remains intact.

Evidence in progress: shadcn-svelte CLI 1.5.0 supplied the shared controls, but unused generated Card, Table, Tabs, and ScrollArea families were removed instead of being carried as dead code. The checkbox now replaces every dashboard-route raw checkbox; its obsolete global checkbox CSS is deleted. The always-loaded sign-in checkbox remains native to avoid a measured 21 KiB initial-JavaScript regression. Progress and Skeleton replace the repeated route implementations, and the Home route is lazy-loaded. All 56 status-pill instances now use the single shared Badge with named state tones; the old status CSS is deleted. The shared Button now owns normal dashboard actions, including retries, pagination, destructive list actions, recommendation actions, and routing controls; native buttons remain only for semantic card/tab/row surfaces or the eager shell. A measured RouteError Button attempt was reverted because it raised initial JavaScript from 45.5 KiB to 59.5 KiB. Six repeated relative-time formatters, the duplicate humanizer, provider lookup/labels, and eight refresh timers now have one shared owner. A single `--surface-gutter` aligns Sources tables and playlist headers/rows. All modal surfaces use the Bits focus/portal primitives and one shared dialog geometry/mobile sheet rule; the separately positioned Sources and playlist-detail shells now inherit it. A Bits Tabs swap was measured and reverted because it added 7.8 KiB initial gzip without deleting useful behavior. Swagger can describe the running admin API, but no committed admin OpenAPI contract or client generator exists; adopting one would add a generator and generated client alongside the existing auth/error wrapper rather than delete it, so `api.ts` remains. A final selector/call-site pass removed the dead sidebar-arrow, Settings-account, extension-handoff, and maintenance-transfer rules while retaining dynamically constructed badge tones. Svelte check is clean with zero diagnostics, unit tests pass 45/45, build/budgets pass, and the complete browser matrix passes 84/84. Initial JavaScript is 45.5 KiB gzip and CSS is 21.2 KiB gzip; `app.css` is 6,683 lines versus the 7,124-line baseline.

## Phase 4 — integrate Canvas UI deliberately

- [ ] Import only the approved Svelte source files through the Canvas UI shadcn registry, pinned to the reviewed commit. Keep their license and provenance beside the source.
- [ ] Start with Canvas UI `Glass Object` for playlist artwork in detail dialogs and heroes.
- [ ] Outside dialogs, activate the Canvas UI artwork effect only for the focused/hovered/selected visible item so a long playlist does not create a WebGL context per row.
- [ ] Keep the normal `<img>` as the accessible, printable, no-WebGL, load-failure, and reduced-motion fallback.
- [ ] Lazy-load the effect, stop it off-screen, destroy it on unmount, and cap active canvases per route.
- [ ] Verify provider artwork works through Allstarr's image proxy/CORS rules without exposing provider credentials or signed URLs.
- [ ] Build a route-by-route Canvas UI component map after the artwork gate. Use the library broadly where it replaces hand-built presentation or adds a useful state transition; do not impose an arbitrary component-count limit.
- [ ] Candidate effects include Ripple for mapping/rematch decisions, Decrypt Reveal for new operation labels, Liquid/Glass for a focused Home surface, Particle Reveal/Scroll for bounded empty or onboarding moments, and one object effect for media artwork. Confirm each choice against the live component catalog before importing it.
- [ ] Prefer one shared imported effect used in several coherent places over several nearly identical local effects.
- [ ] Reject an effect if it harms scanning, input latency, focus visibility, GPU use, bundle budgets, or browser fallback. Do not place a canvas behind every card or table row.

Acceptance: the hand-built glass CSS is gone; the selected Canvas UI source is the only effect engine; core actions remain plain interactive DOM; unsupported browsers and reduced-motion users lose decoration, not function.

## Phase 5 — redesign the control dashboard route by route

- [x] Shell and Home: make current listeners, active track, source route, account, progress, and scrobble completion the focal operational surface. Preserve the multi-user horizontal rail and make state changes understandable without animation.
- [x] Playlists: fix header overlap, artwork, counts, source/target wording, modes, filters, and modal density. Keep full track/object detail available without crowding the main scan path.
- [x] Mappings: present source, candidate, score reasons, and decision as one clear flow. Accepted items leave the review queue immediately; history remains available elsewhere.
- [x] Activity: replace the dense log table with grouped operational events, clear state pills, useful timestamps, and expandable technical detail. New-event motion must not reorder content under the pointer.
- [x] Sources: align table gutters, explain readiness and timing, keep configuration in the Configuration tab, and distinguish metadata/download/streaming/lyrics capabilities without empty timing cells.
- [x] Extensions: show installed/update/available state once, keep lifecycle/permissions here, and deep-link provider configuration to Sources.
- [x] Intelligence: make imported history, saved listening data, recommendation prerequisites, and automatic schedules agree. Use styled checkboxes and clear empty/error states.
- [x] Cached/Kept/Settings: keep cache controls next to cached content, remove duplicate settings, and make retained versus temporary files obvious.
- [x] Test every migrated route at 320/390 mobile, tablet boundaries, and desktop without adding four copies of the same smoke assertion.

Acceptance: each page has one clear purpose and primary action, status text is structured, geometry is consistent, and the redesign does not duplicate configuration ownership.

Evidence in progress: Sources now owns provider/account configuration and shows saved field values in its Configuration tab. Capability readiness uses status badges; API timing, managed p95, and click-to-stream timing have distinct labels. Download-only extensions are projected through the existing typed download-backed streaming adapter, so they participate in readiness and CTS; CTS may perform the provider's complete bounded download because its metric is time until playable audio. The Cached route now owns the track-cache controls, and completed full provider streams in Cache mode are published into the managed cache only after the response finishes; interrupted/partial responses are discarded. The focused cache/path/archive lane passes 12/12. External artist primary and contributing-album routes are separated by relationship, with the FinerPlayer-style fixture passing. Deezer and other known external providers share one correct Odesli track-URL builder, and Deezer metadata fallback to LRCLib is covered; the focused lyrics/download/artist lane passes 5/5.

Route completion evidence: Home renders an active-listener rail with user/client, provider, track, progress, and scrobble state. Mapping acceptance calls the durable resolve owner and reloads the queue. Activity groups adjacent operations into expandable details. Extensions expose one installed/update/available projection, permission review, and exact Sources deep links. Playlists retain track identity and detail while the responsive tests enforce dialog geometry, scroll ownership, and no blank tail. Intelligence has deterministic import, saved-history, recommendation, schedule, empty, partial, and error-state coverage. Cached owns storage/retention controls while Kept remains permanent-media focused. The representative 84-case browser matrix covers mobile, desktop, and explicit breakpoint boundaries without repeating the full route grid at every viewport.

## Phase 6 — review backend hand-rolled infrastructure and large owners

- [x] Generate a caller/composition map for helpers, managers, builders, adapters, and legacy migration paths. Rank by removable callers and duplicated responsibility, not line count.
- [x] Review the remaining `RetryHelper` call sites against Microsoft's HTTP resilience handlers. Replace it only if provider-specific status handling, cancellation, response disposal, and `Retry-After` behavior remain correct and the result is a net deletion.
- [x] Review custom fuzzy matching against the residual benchmark and current provider identity rules. Use a mature library only if it improves or preserves the benchmark and deletes code; never trade proven matching behavior for fewer lines.
- [x] Keep ASP.NET WebSocket, HTTP, JSON, scheduling, and EF primitives where the platform already owns the problem.
- [x] Review large active owners such as `ExtensionManager`, playlist orchestration, matching commands, controllers, and state transfer for duplicated branches or dead compatibility paths. Split only when it enables deletion or establishes a real ownership boundary.
- [x] Delete interfaces with one implementation only when they are not protocol/provider seams, test boundaries, or replaceable capability contracts.
- [x] Delete stale comments, unused configuration, duplicated DTO builders, and unreachable defensive branches only with call/contract evidence.

Acceptance: every new dependency replaces a named body of owned code, has one production owner, and reduces maintenance; no speculative framework or parallel engine is introduced.

Evidence in progress: the caller/composition audit deleted `IAudioContentStream` and `IListeningProfileService`, each of which had one implementation and no replacement/test boundary. It also deleted the unconsumed `SafeJsonProxyClient` vertical slice, its five DI registrations, and three tests that exercised no reachable production route; the real `OutboundRequestGuard` remains and the focused diagnostics/outbound/query-safety lane passes 27/27. Provider, protocol, durable-storage, clock, transport, and multi-backend interfaces remain because they have multiple implementations, fakes, or externally meaningful capability boundaries. `RetryHelper` remains because no resilience package is installed and its bounded 429/503 disposal/cancellation behavior would not become smaller with a new dependency. The fuzzy matcher remains because it is coupled to current identity rules and no installed library has benchmark evidence that preserves them.

Final owner evidence: a definition/caller scan found no orphaned private methods in `ExtensionManager`, playlist orchestration, matching commands, playlist links, or either protected state-transfer owner. Their remaining single-implementation interfaces are protocol/provider capabilities, DI test seams, or replaceable storage/transport boundaries. Jellyfin search's duplicated 67-line recursive JSON converter and a stale rebuild marker were removed in favor of `System.Text.Json`; 13 focused search/artist protocol checks and format verification pass. State-transfer validation and migration compatibility remain untouched.

## Phase 7 — verification, review, and delivery

- [x] Run focused checks after each deletion/migration slice.
- [x] Run Svelte check, WebUI unit, one build, budget, and focused E2E after each complete route group.
- [x] Run affected .NET lanes after each backend owner, then the complete release matrix once at the end.
- [x] Run the Apple gateway and all Compose contract profiles.
- [x] Run browser-only desktop/tablet/mobile smoke tests. Do not use Computer Use, Musiver, or automated Feishin control. The deployed in-app Browser passes 36/36 live checks across 320px, 390px, tablet, and desktop on Home, Playlists, Mappings, Cached, Kept, Intelligence, Sources, Activity, and Extensions. Every route has main content, no horizontal overflow, no route error, and no console error; the viewport override was reset afterward. The deterministic matrix also passes 84/84 in CI.
- [x] Run the direct-Jellyfin and Subsonic smoke suites and keep native/uninjected object parity exact.
- [x] Add live WebSocket handshake/frame/auth forwarding and allowed session lifecycle coverage without opening the deliberately denied remote-control surface.
- [ ] Compare initial and route bundle sizes, CSS size, active WebGL contexts, off-screen pause, reduced motion, keyboard flow, and no-WebGL fallback. **BLOCKED —** bundle/CSS, reduced-motion, and keyboard evidence pass, but Canvas/WebGL checks require a GPL-compatible Canvas UI grant before source can be imported.
- [x] Report production deletions, test deletions, vendored Canvas UI lines, generated lines, and net handwritten change separately.
- [x] Commit, push, deploy, and run live checks only after exact revision authorization.

Live evidence: production revision `07bc3c45124beebbe28b9b7131988fd75883e15c` is deployed on both clean `.116` checkouts; all five main containers and the dedicated Subsonic stack are healthy. The authenticated Jellyfin LAN smoke at the unchanged protocol parent completes 201 checks with zero failures and four declared fixture/stateful blocks. An exact-revision server-key run passes 150/152 checks; only the configured-library root checks return upstream 404 because the API key has no actor able to access that user-scoped root (`actor_bound=0`). The route preserves the explicit `UserId`, and its 98 focused protocol fixtures pass. Native objects, search, external routes, artwork bytes, stream bytes, cancellation, and security checks all pass. The direct-Navidrome versus Allstarr Subsonic smoke remains 67/67 with zero failures and two expected stateful blocks; no Subsonic production path changed afterward. The reusable live WebSocket gate remains 5/5; later revisions add only its reusable script, Apple startup, CI, WebUI, and root-query changes. Music-session discovery and remote-control routes are allowed and compared; broad/admin session routes remain denied. A final admin-key read-only scan across every Jellyfin user finds zero playlists with the harness's exact `Allstarr smoke ` prefix, so the prior orphan is gone without another delete. The deployed in-app browser now opens Intelligence and Sources successfully. Apple Music – GAMDL reports all four capabilities healthy and logged in; a configured CD-quality `alac-16-44` stream returned FLAC headers in 0.7 ms, completed 35.9 MB in 5.87 seconds, and left no temporary artifact.

Final local release evidence: backend and release-owned code passes at `61d97d451d4645f5a73d089c8a10151f70edc6f5` with a zero-warning Release build, fast PostgreSQL lane 2,208/2,208 in 66.1 seconds, release-critical PostgreSQL lane 104/104 in 121.8 seconds, and clean format, Apple gateway 19/19, three Compose profiles, shell syntax, and Python timing/manifest/smoke checks. The child revision `0d635b04dd7af26da6def5219f484f370656a795` changes only WebUI dependencies and icon import paths: Svelte check has zero diagnostics, unit tests pass 45/45, build and budgets pass at 45.3 KiB initial JavaScript and 21.2 KiB CSS, the Impeccable detector is empty, and Playwright passes 84/84 with no retry. The deprecated icon package is replaced by its official scoped successor, SvelteKit is patched to 2.70.2, and Nano ID is pinned to 3.3.18. No moderate, high, or critical npm advisory remains; the current SvelteKit dependency still exposes one upstream low-severity `cookie` advisory, reported through six dependency rollups, and no unsupported transitive major override was forced.

Push/CI evidence: `0d635b04dd7af26da6def5219f484f370656a795` was pushed to `origin/dev` after exact authorization. CI run 1237 passed Compose, formatting, Apple, and the main 2,208-test backend lane but exposed two E2E defects: a test navigated with a portaled confirmation still open, and AudioMuse result text could exceed a Linux-rendered mobile grid. Revision `01c410597817e3e016937873fecd3e041cde64c3` closes the dialog before navigation and adds shrink/wrap constraints at the existing responsive owner. Svelte check, build, budgets, the Impeccable detector, 40 repeated focused cases, and the complete 84-case matrix pass locally. The isolated release-critical CI lane also failed without a public test annotation; the exact 104-test lane passes 104/104 in 2 minutes 17 seconds both natively and in a disposable Linux ARM64 container using CI's .NET 10.0.301 SDK and PostgreSQL 18 client, so no safety assertion was weakened for an unreproduced runner failure.

CI run 1238 reproduced both browser failures and made the release-critical cause locally reproducible. The cache action was outside its clipped desktop grid, while the match rejection flow could overlap sibling modal locks. Revision `b5936d996dbb148eace76bd0352eb47e1941fe64` keeps the parent match dialog open under its confirmation, gives confirmations the correct shared layer, disables only the redundant nested scroll lock, uses stable scrollbar geometry, removes a dead download breakpoint, and makes the cache grid fit before it changes to cards. The two browser regressions pass 20/20 repeated runs and the complete browser matrix passes 84/84; Svelte check, unit 45/45, build, and budgets pass at 45.3 KiB initial JavaScript and 21.2 KiB CSS. The exact Linux x64 CI shape failed 103/104 solely because the release-critical job did not install `pg_dump`; adding the same PostgreSQL 18 client step used by the main job makes the exact lane pass 104/104 in 2 minutes 10 seconds.

Final delivery evidence: revision `07bc3c45124beebbe28b9b7131988fd75883e15c` preserves configured Apple playback quality, starts the Apple streaming response before the cold GAMDL preparation, constrains the Intelligence grid, prevents route content from widening the document, and preserves explicit Jellyfin library-root user scope. Local WebUI check has zero diagnostics, unit tests pass 46/46, build and budgets pass at 45.3 KiB initial JavaScript and 21.3 KiB CSS, and Playwright passes 84/84. CI run `32064966557` passes every backend, release-critical, formatting, WebUI, Apple, Compose, and release-manifest job. Both approved source deployments fast-forwarded cleanly to that exact revision.

### Completion gate

Measured package revision before the final dependency-only cleanup: production is 461 additions and 3,758 deletions (net -3,297); tests are 500 additions and 2,582 deletions (net -2,082); WebUI is 1,014 additions and 936 deletions (net +78); tooling/CI is 209 additions and 41 deletions (net +168); durable plan/handoff documentation is net +173. The final dependency cleanup adds two net lock/manifest lines and only substitutes import paths. Vendored Canvas UI and generated migration/model lines are both zero.

This package is complete only when:

1. retired production slices and their tests are gone;
2. false/no-op tests are gone or replaced by real observable checks;
3. the WebUI uses shadcn-svelte/Bits UI for controls and Canvas UI for the approved visual effects;
4. the hand-built glass effect and obsolete global CSS are removed;
5. native Jellyfin/Subsonic behavior, durable data safety, security, and destructive-operation boundaries remain proven;
6. the final result is a measured net deletion in handwritten application/test/CSS code, reported separately from vendored/generated source; and
7. the complete release and browser gates pass.

### Immediate next action

Obtain a GPL-compatible Canvas UI grant before importing its restricted source, then finish the Canvas/WebGL checks. Revision `07bc3c45124beebbe28b9b7131988fd75883e15c` is already pushed, fully green, and deployed to both clean stacks; its live responsive gate passes 36/36 and the prior throwaway playlist is absent.
