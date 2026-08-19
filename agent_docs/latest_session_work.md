# Latest Session Work

## Active execution — 2026-08-19

- Completed Phases 0–6 of the rich-dashboard package in six local commits after deployed baseline `07bc3c45124beebbe28b9b7131988fd75883e15c`: shared shell/rows, Integrations, Intelligence/imports, Home/Activity, Mappings/Cached/Kept, and external object/streaming/lyrics parity.
- Matching now queries verified identities plus title+artist, title+album, title, artist, and album; deduplicates the union; and scores it through the existing decision engine. `Crush` by Selena Gomez & The Scene guards the weak-artist regression.
- The review dialog exposes every credible candidate, final and raw confidence, every component score, reasons, warnings, normalized titles, source/candidate ISRCs, artist overlap, album evidence, duration delta, route, and all provider IDs through an accessible disclosure. Manual provider search exposes the same available scoring evidence.
- Cached/Kept distinguish indexed, referenced, and diagnostic files. Cache-mode full streams publish only completed managed artifacts; unknown and interrupted files are never adopted.
- External relationships are provider-neutral in Jellyfin and Subsonic. Lyrics use source-native lookup, Odesli identity translation, and distinct configured fallbacks without fetching media merely for lyrics.
- The live Jellyfin kit exact-compares native music counts in addition to existing full native objects, native artwork bytes, dynamic external traversal, external playback/lyrics, Finer query-key file, Musiver playlist shape, Feishin-class headers, and WebSocket sessions.
- Replaced an unawaited download-sidecar test with a public, awaited PostgreSQL behavior test that proves the exact audio file, adjacent lyrics sidecar, and durable mapping are removed together.
- Local release gates pass: WebUI check clean, unit 46/46, build/budgets green at 46.2 KiB initial JavaScript and 22.7 KiB CSS, browser 92/92; PostgreSQL fast lane 2,212/2,212; release-critical 104/104; Apple gateway 20/20; format clean; three Compose profiles valid; deterministic Subsonic 5/5; release-manifest self-tests 2/2; shell syntax and diff checks clean.
- The fast PostgreSQL lane has no test above three seconds. Release-critical deliberately retains the measured migration, rollback, lineage, state-transfer, backup/restore, clone-pool, and 10,000-track contracts.
- Local sandboxed .NET test processes cannot create MSBuild named pipes, so the verified database lanes ran with approved escalation against the named disposable OrbStack PostgreSQL container `allstarr-release-postgres`. The exact container and its anonymous test-data volume were removed after verification.
- LAN/VPN access to `192.168.1.116` remains off. No push or deployment has occurred for the six local commits.

## Next entry point

Ask the user to enable LAN/VPN for push, deployment, and bounded live qualification.
