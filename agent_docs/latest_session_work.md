# Latest Session Work

## Active execution — 2026-08-15

- Established a truthful baseline: 2,354 discovered .NET tests; main PostgreSQL lane 2,282/2,282; state transfer 90/90; WebUI check/unit/build/budget and 130-case original browser matrix; Apple 19/19; all Compose profiles valid.
- Added machine-readable Playwright timings and a 600-second configurable timing-wrapper watchdog. The self-test and a live one-case timing proof pass.
- Deleted the complete retired SquidWTF slice plus its endpoint discovery/racing helpers and direct tests. Focused deletion coverage passes 94/94 and the application/test projects compile cleanly.
- Removed false provider tests, merged duplicate Jellyfin DTO assertions, and reduced repeated E2E viewport work while preserving explicit responsive and interaction coverage.
- Removed the hand-built playlist glass CSS/hooks.
- Added official shadcn-svelte shared controls and migrated dashboard checkboxes, progress, skeletons, all 56 status pills, and normal dashboard actions to shared accessible components. Deleted the superseded global CSS and removed unused generated Card, Table, Tabs, and ScrollArea source instead of carrying it as dead code. A measured Bits Tabs attempt was reverted because it added 7.8 KiB initial gzip without useful deletion.
- Consolidated six relative-time formatters, the duplicate humanizer, provider lookup/labels, eight refresh timers, and shared surface gutters. Replaced a Spotify Pathfinder source-text test with emitted HTTP-query assertions.
- Deleted the six source-reading protocol-gateway tests after observable replacement lanes passed 116/116, plus six low-value operational source assertions with real behavior coverage elsewhere. Deleted the one-implementation `IAudioContentStream` and `IListeningProfileService` abstractions while retaining real protocol/provider/test seams.
- WebUI check is clean with zero diagnostics, unit tests pass 45/45, build/budgets pass, and the complete browser matrix passes 84/84. Initial JavaScript is 45.5 KiB gzip, CSS is 21.6 KiB gzip, and `app.css` is 6,874 lines. The focused PostgreSQL intelligence lane passes 15/15 against OrbStack.
- Canvas UI remains blocked from vendoring: the reviewed commit uses MIT plus Commons Clause, whose redistribution restriction is not established as GPL-3.0-compatible. No Canvas UI source was copied.
- Consolidated Sources and playlist detail onto the shared Bits dialog geometry; all modal focus/portal behavior remains library-owned and the Svelte check remains clean.
- Made generic download-only extensions use the existing typed download-backed streaming adapter, so readiness and CTS treat them as playable providers while explicit streaming hooks retain their own adapter.
- Added managed track-cache publication for completed full provider streams in Cache mode and moved the related settings beside Cached content; interrupted and partial responses do not publish. The focused cache/path/archive lane passes 12/12.
- Fixed provider-agnostic track URL translation at one shared Odesli owner, including Qobuz's `/track/` route and SpotiFLAC aliases. Deezer-to-LRCLib metadata fallback and external artist primary/appearance relationships have focused passing coverage.
- Replaced the Spotify playlist-filter source assertion with an emitted Pathfinder request assertion and removed stale references to the deleted protocol-gateway contract; the focused support/catalog lane passes 18/18.
- Finished the shared Button pass for retries, pagination, destructive list actions, recommendation actions, and routing controls, then removed their duplicate CSS plus dead Accounts/extension-handoff/maintenance selectors. A measured eager RouteError Button attempt was reverted because it increased initial JavaScript from 45.5 KiB to 59.5 KiB.
- Deleted the unreachable `SafeJsonProxyClient` stack, its five DI registrations, and three tests that could not represent a production route. The real outbound-request guard remains; the focused safety lane passes 27/27 and the rebuilt final .NET fast lane passes 2,205/2,205.
- Final local gates pass: .NET fast 2,205/2,205, release-critical 100/100, WebUI check 0 diagnostics, unit 45/45, build and budgets, browser 84/84, Apple gateway 19/19, all three Compose profiles, `dotnet format`, shell syntax, and `git diff --check`.
- Current measured WebUI output is 45.5 KiB initial JavaScript and 21.2 KiB CSS. `app.css` is 6,683 lines, down from 7,124. The exact staged revision is net -3,297 production lines, -2,082 test lines, +78 WebUI lines, +168 tooling/CI lines, and +173 durable documentation lines: 2,393 additions versus 7,353 deletions overall (net -4,960). No Canvas UI or generated migration/model source is vendored.

## Next entry point

Commit and push the exact tested revision, fast-forward the clean source deployment on `root@192.168.1.116`, then run live browser-only protocol smoke. Keep Canvas UI effects queued until a compatible grant or relicensing decision exists.
