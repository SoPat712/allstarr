# Scrobbling

> **IMPORTANT FOR AI ASSISTANTS**: Do NOT create summary markdown files unless explicitly requested by the user or for vital architectural features. Put summaries in chat only. Keep the repository focused on durable steering and product docs.

## Current Boundary

Playback reporting keeps the native Jellyfin or Subsonic response contract first. Optional Allstarr work starts only after authentication resolves the exact tenant, user, protocol, backend instance, and library scope. A route ID, payload user ID, display name, or opaque client token cannot select another user's history or provider account.

`Core/Playback` is the durable path for playback observations. It normalizes an authenticated observation, applies an idempotency key, and enqueues the follow-up work through the durable job system. The job records listening signals and delivers enabled scrobbles without controller-owned `Task.Run` work. Redis or Valkey may help runtime coordination, but it is never the source of truth for a playback event, retry, or recommendation signal.

The legacy `ScrobblingOrchestrator` and `Services/Scrobbling` contracts still preserve the threshold, Now Playing, retry, and provider API behavior while callers move through the durable boundary. Do not add a second in-memory source of truth around them.

## Delivery Rules

- Preserve the backend response even when optional signal or scrobble work fails.
- Deduplicate noisy start, progress, stop, and inferred-stop observations. A retry must not create a second external scrobble.
- Recover from missing start events when a valid authenticated progress or stop event contains enough information.
- Resolve Last.fm and ListenBrainz accounts for the exact owner and scope. Store only encrypted credential references in durable records and resolve the secret just in time.
- Keep Now Playing separate from a completed scrobble. Threshold and completion rules remain explicit and tested.
- Feed allowed observations into intelligence only when the exact intelligence policy is enabled. Retention and purge belong to that scoped policy.
- Never require Jellyfin to store a real item for an external track before reporting its playback.

## Admin And User Surface

The scrobbling surface reports configuration, account readiness, sanitized connection-test failures, durable delivery state, and retry-safe errors. Last.fm and ListenBrainz credentials are user-owned where supported, masked on reads, and never written into logs, job payloads, state-transfer archives, or API responses.

Intelligence opt-in is separate from scrobbling provider enablement. A user can disable and purge retained intelligence data without silently changing a provider connection, and can disable a provider without deleting unrelated listening history unless they explicitly request the scoped purge.

## Editing Guardrails

- Keep protocol authentication and response shaping in the adapters; keep durable observation, policy, and delivery work in the shared core.
- Preserve exact actor and backend scope in the observation, job, signal, and provider-account lookup.
- Treat dedupe keys, threshold changes, retention, cancellation, retries, and provider error classification as behavior changes that need focused tests.
- Do not place raw upstream bodies, tokens, URLs with credentials, filesystem paths, or exception text in user-visible failures.
