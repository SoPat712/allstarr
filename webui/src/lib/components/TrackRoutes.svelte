<script lang="ts">
  import { ChevronDown } from "@lucide/svelte";
  import { Popover } from "$lib/components/ui/popover";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import type { PlaylistTrack, ProviderDefinition } from "$lib/api";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";
  import { isReviewTrack } from "$lib/playlists";

  let { track, providers, backend }: {
    track: PlaylistTrack; providers: ProviderDefinition[]; backend: string;
  } = $props();
  const local = $derived(track.routeKind === "local");
  const review = $derived(isReviewTrack(track));
  const routes = $derived([...new Set([
    ...(track.routeProviderId ? [track.routeProviderId] : []),
    ...track.providerRoutes.map((route) => route.providerId),
  ])]);
  const name = (id: string) => providerDisplayName(providers, id, id);
  // Different sources can reach the same service (built-in and extension Apple Music); list each service once.
  const services = $derived.by(() => {
    const byName = new Map<string, { id: string; name: string; pinned: boolean; count: number }>();
    for (const id of routes) {
      const label = name(id);
      const pinned = track.providerRoutes.some((route) => route.providerId === id && route.pinned);
      const known = byName.get(label.toLowerCase());
      if (known) { known.count++; known.pinned ||= pinned; }
      else byName.set(label.toLowerCase(), { id, name: label, pinned, count: 1 });
    }
    return [...byName.values()];
  });
</script>

{#snippet summary()}
  <span class="route-summary">
    <strong>{local ? name(backend) : "External [A]"}</strong>
    <small>{local
      ? track.targetStatus === "duplicate" ? "Duplicate · not added twice" : "Local library"
      : track.targetStatus === "streamed" ? "Streams through Allstarr"
      : `${routes.length} mapped ${routes.length === 1 ? "provider" : "providers"}`}</small>
  </span>
{/snippet}

{#if track.routeKind === "unmatched" || track.routeKind === "unresolved"}
  <span class="route-summary"><strong>Unresolved</strong><small>Not playable</small></span>
{:else if routes.length === 0}
  {@render summary()}
{:else}
  <Popover.Root>
    <Popover.Trigger class="track-routes-trigger" aria-label={`Routes for ${track.title}`}>
      {@render summary()}
      <ChevronDown size={14} aria-hidden="true" />
    </Popover.Trigger>
    <Popover.Portal>
      <Popover.Content class="bits-menu track-routes-panel" sideOffset={6} align="end">
        <header>
          <strong>Where this song plays</strong>
          <small>{local ? `From ${name(backend)} first, then a mapped source` : "Streams through Allstarr from the first available source"}</small>
        </header>
        <ul aria-label={`Mapped providers for ${track.title}`}>
          {#if local}
            <li><span class="route-tile"><ProviderMark id={backend} definition={findProviderDefinition(providers, backend)} /></span><span>{name(backend)}<small>In your library</small></span></li>
          {/if}
          {#each services as service (service.name)}
            <li>
              <span class="route-tile"><ProviderMark id={service.id} definition={findProviderDefinition(providers, service.id)} /></span>
              <span>{service.name}<small>{service.pinned ? "Manually chosen" : review ? "Suggested source — check recording" : track.matchState === "accepted" ? "Automatically matched" : "Playback source"}{service.count > 1 ? ` · ${service.count} sources` : ""}</small></span>
            </li>
          {/each}
        </ul>
        <p>Playback follows your streaming order; pins and account access still apply. Matches aren't a live availability check.</p>
      </Popover.Content>
    </Popover.Portal>
  </Popover.Root>
{/if}

<style>
  :global(.track-routes-panel) { display: grid; gap: var(--space-3); }
  :global(.track-routes-panel header) { display: grid; gap: .125rem; }
  :global(.track-routes-panel header strong) { font-size: var(--text-sm); font-weight: 600; }
  :global(.track-routes-panel ul) { display: grid; gap: .25rem; margin: 0; padding: var(--space-3) 0 0; list-style: none; border-top: 1px solid var(--color-edge); }
  :global(.track-routes-panel li) { display: flex; align-items: center; gap: var(--space-3); padding-block: .375rem; font-size: var(--text-sm); }
  :global(.track-routes-panel li small) { display: block; }
  :global(.track-routes-panel .route-tile) {
    display: grid; flex: none; width: 2rem; height: 2rem; place-items: center; overflow: hidden;
    border-radius: .5rem; background: color-mix(in srgb, var(--color-ink) 7%, transparent);
  }
  :global(.track-routes-panel .route-tile .provider-mark) { width: 100%; height: 100%; border: 0; border-radius: 0; background: transparent; }
  :global(.track-routes-panel .route-tile .provider-mark img) { width: 1.25rem; height: 1.25rem; border-radius: .3rem; object-fit: contain; }
  :global(.track-routes-panel > p) { margin: 0; padding-top: var(--space-3); border-top: 1px solid var(--color-edge); line-height: 1.45; }
</style>
