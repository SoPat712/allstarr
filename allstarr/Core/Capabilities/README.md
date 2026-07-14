# Capability Core

This folder owns the provider-facing contracts shared by built-ins and the future SDK bridge. It does not
select accounts, choose a provider, translate identities, persist recordings, or shape Jellyfin and Subsonic
responses. Those jobs stay in their own layers.

The first contract version covers metadata, streaming, download, playlist, lyrics, and health. Each call gets
an explicit `ProviderExecutionContext`, so provider code never has to discover the actor, account, library,
policy, deadline, cancellation token, or idempotency key from ambient HTTP state.

External IDs keep the provider, resource kind, catalog, and opaque source value separate. They never contain
account access. Cross-provider use needs a verified identity link supplied by the host.

`ProviderOutcome<T>` is the only provider result shape. Provider implementations classify safe failures and
keep raw response bodies, credentials, signed URLs, cookies, and authorization headers inside their adapter.

`ProviderDescriptor` is metadata. It does not implement a capability. `ProviderRegistry` validates SDK v1
hooks, settings, permissions, account requirements, support state, and package-relative entry points, then
returns providers in stable ID order. An operational descriptor is accepted only with exactly one implementation
of its declared typed interface. Legacy-only built-ins stay visible as `ConfiguredOnly` or `Unavailable` and are
not routable.

`ProviderRouter` lives in `Core/Routing`. It applies provider policy, account scope/revision, capability state,
health/circuits, sidecar readiness, quality, deadline, and durable download rules before returning an ordered
plan. Cross-provider track candidates require an exact verified translation from `TrackIdentityService` in
`Core/Matching`. Missing or ambiguous identity links never trigger a guessed fallback.

Deezer public metadata is the first real built-in capability adapter. It wraps the existing metadata service so
current controllers keep their behavior while the core gets typed IDs, outcomes, registry ownership, and routing.

Focused contract tests live in:

- `ProviderExecutionContextTests`
- `ProviderRegistryTests`
- `ProviderCapabilityContractTests`
- `ProviderRouterTests`
- `TrackIdentityServiceTests`
- `DeezerMetadataCapabilityAdapterTests`
