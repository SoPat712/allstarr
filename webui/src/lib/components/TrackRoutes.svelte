<script lang="ts">
  import { ChevronDown } from "@lucide/svelte";
  import { Popover } from "$lib/components/ui/popover";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import type { PlaylistTrack, ProviderDefinition } from "$lib/api";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  let { track, providers, backend }: {
    track: PlaylistTrack; providers: ProviderDefinition[]; backend: string;
  } = $props();
  const local = $derived(track.routeKind === "local");
  const routes = $derived([...new Set([
    ...(track.routeProviderId ? [track.routeProviderId] : []),
    ...track.providerRoutes.map((route) => route.providerId),
  ])]);
  const name = (id: string) => providerDisplayName(providers, id, id);
</script>

{#snippet summary()}
  <span class="route-summary">
    <strong>{local ? name(backend) : "External [A]"}</strong>
    <small>{local ? "Local library" : `${routes.length} mapped ${routes.length === 1 ? "provider" : "providers"}`}</small>
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
        <strong>{local ? "Local library + external mappings" : "Mapped providers"}</strong>
        <ul aria-label={`Mapped providers for ${track.title}`}>
          {#if local}
            <li><ProviderMark id={backend} definition={findProviderDefinition(providers, backend)} /><span>{name(backend)}<small>Local · used by this playlist row</small></span></li>
          {/if}
          {#each routes as id}
            <li>
              <ProviderMark {id} definition={findProviderDefinition(providers, id)} />
              <span>{name(id)}<small>{track.providerRoutes.some((route) => route.providerId === id && route.pinned) ? "Manual pin" : "Verified mapping"}</small></span>
            </li>
          {/each}
        </ul>
        <p>For external [A] songs, playback uses eligible mappings in your streaming order. An authorized cached copy may play first; manual pins and account access still apply.</p>
        <small>Mappings are not a live availability check. The provider actually serving a song is reported during playback.</small>
      </Popover.Content>
    </Popover.Portal>
  </Popover.Root>
{/if}
