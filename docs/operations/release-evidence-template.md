# Allstarr release evidence

Copy this file for each release candidate as
`docs/releases/evidence/<version>.md`. A release gate is complete only when its
evidence link points to an immutable CI artifact, digest, report, screenshot,
backup manifest, or named manual verification record. Do not replace failed
required evidence with a known-issue note.

## Candidate identity

| Item | Recorded value | Evidence |
| --- | --- | --- |
| Application version | | |
| Git commit | | |
| Git tag | | |
| OCI image and digest | | |
| Build workflow run | | |
| Database migration set/hash | | |
| Extension SDK version | | |
| First-party extension registry digest | | |
| Apple gateway lock digest | | |
| Compose file digest | | |
| Release notes | | |

## Reproducibility

| Requirement | Result | Evidence |
| --- | --- | --- |
| Base images are digest-pinned | | |
| Runtime packages are lockfile-pinned | | |
| Extension packages have immutable versions/checksums | | |
| Sidecars have immutable versions/checksums | | |
| Clean-checkout image digest recorded | | |
| Rollback image and configuration retained | | |

## Automated gates

| Gate | Result | Evidence |
| --- | --- | --- |
| Formatting | | |
| Compiler warnings policy | | |
| Unit tests | | |
| Integration tests | | |
| Native PostgreSQL tests | | |
| Migration and rollback tests | | |
| Provider contract tests | | |
| WebUI tests | | |
| Accessibility checks | | |
| Container health smoke test | | |
| Authenticated release-candidate browser smoke test | | |

## Playlist journey

Record the account/provider used without recording credentials, cookies,
private URLs, or raw media identifiers.

| Scenario | Result | Evidence |
| --- | --- | --- |
| Discover/search/paginate source playlists | | |
| Source artwork and fallback icon | | |
| Discover/search/paginate Jellyfin targets | | |
| Discover/search/paginate Subsonic targets | | |
| Target artwork and fallback icon | | |
| No-write preview totals and warnings | | |
| Create and first sync | | |
| Resync and idempotency | | |
| Pause and resume | | |
| Edit behavior | | |
| Confirmed removal | | |
| Optional provider outage degradation | | |

## Apple Music

| Scenario | Result | Evidence |
| --- | --- | --- |
| Per-user MusicKit connection and encrypted storage | | |
| Playlist browse, search, pagination, and artwork | | |
| Playlist track retrieval | | |
| Revoked MusicKit recovery message | | |
| Download gateway remains separately authenticated | | |
| Lyrics route through a lyrics-capable provider | | |

## Extensions

| Scenario | Result | Evidence |
| --- | --- | --- |
| Registry selection and refresh | | |
| Package version/checksum review | | |
| Capability and permission review | | |
| Install and activation | | |
| Disable and enable | | |
| Update | | |
| Rollback | | |
| Uninstall with account-retention choice | | |
| Failure classifications and recovery text | | |
| Readable lifecycle events | | |

## Event log

| Scenario | Result | Evidence |
| --- | --- | --- |
| Playlist and matching events | | |
| Cache, stream, and download events | | |
| Scrobble and provider events | | |
| Extension and administrative events | | |
| Safe track context and redaction | | |
| Time/severity/category/provider/playlist/correlation filters | | |
| Stable bounded cursor pagination | | |
| High-volume sampling or summarization | | |

## Performance and resilience

Attach machine/runtime details, provider fan-out, query count, allocations or
working set, and the five slowest operations to every timing report.

| Workload | Cold | Warm | Evidence |
| --- | --- | --- | --- |
| 50-track playlist | | | |
| 500-track playlist | | | |
| 5,000-track playlist | | | |

| Failure scenario | Result | Evidence |
| --- | --- | --- |
| User cancellation | | |
| Provider timeout | | |
| Database interruption | | |
| Duplicate job delivery | | |
| Application restart during sync | | |
| Deep diagnostic timeout and cleanup | | |

## Browser and accessibility matrix

Test at 100% and 200% zoom. Include keyboard-only and screen-reader checks for
dialogs, tabs, tables/repeated data, menus, disclosures, and status updates.

| Engine | 360 | 390 | 768 | 1024 | 1440 | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| Chromium | | | | | | |
| Firefox | | | | | | |
| Safari/WebKit | | | | | | |

## Migration and rollback rehearsal

| Requirement | Result | Evidence |
| --- | --- | --- |
| Redacted real 2.x `.env` preview | | |
| Apply report with unknown/deprecated keys | | |
| Shared accounts disabled pending review | | |
| Personal account reconnection | | |
| Playlist recreation through Playlists wizard | | |
| Verified PostgreSQL backup | | |
| Secret key-ring backup | | |
| Media backup | | |
| Isolated restore rehearsal | | |
| Full rollback rehearsal | | |

## Known issues

| Severity | Issue | Workaround | Owner | Target |
| --- | --- | --- | --- | --- |
| | | | | |

## Sign-off

| Role | Name | Decision | Timestamp | Evidence |
| --- | --- | --- | --- | --- |
| Release owner | | | | |
| Migration verifier | | | | |
| WebUI/accessibility verifier | | | | |
| Provider/extension verifier | | | | |

