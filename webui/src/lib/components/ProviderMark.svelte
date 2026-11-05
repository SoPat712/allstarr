<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import { providerColor } from "$lib/playlists";
  import { providerMarkSources } from "$lib/provider-marks";
  import { untrack } from "svelte";
  import { imageHasLoaded, reconcileImageSources, type ImageLoadState } from "$lib/image-loading";

  let {
    id,
    definition,
    label = definition?.name ?? id,
    decorative = false,
  }: { id: string; definition?: ProviderDefinition; label?: string; decorative?: boolean } = $props();
  let image = $state<HTMLImageElement>();
  let loadState = $state<ImageLoadState>(reconcileImageSources(undefined, []));
  const sources = $derived(providerMarkSources(id, definition, label));
  const source = $derived(sources[loadState.index]);

  $effect(() => {
    const next = reconcileImageSources(untrack(() => loadState), sources, untrack(() => image));
    if (next !== untrack(() => loadState)) loadState = next;
  });
  $effect(() => {
    if (imageHasLoaded(image, source)) loadState.loaded = true;
  });
</script>

<span class="provider-mark" class:provider-mark--monogram={!loadState.loaded} style={`--route-color:${providerColor(id)}`}>
  {#if !loadState.loaded}<span class="provider-monogram" aria-hidden="true">{(label.trim() || id)[0]?.toUpperCase() ?? "?"}</span>{/if}
  {#if source}
    <img bind:this={image} src={source} alt="" hidden={!loadState.loaded} onload={() => { loadState.loaded = true; }} onerror={() => { loadState.index += 1; loadState.loaded = false; }} />
  {/if}
  {#if !decorative}<span class="sr-only">{label}</span>{/if}
</span>

<style>
  .provider-mark--monogram {
    border-color: transparent;
    border-radius: var(--radius-sm);
    background: var(--md-sys-color-primary-container);
    color: var(--md-sys-color-on-primary-container);
  }
  .provider-monogram { display: grid; width: 100%; height: 100%; place-items: center; }
  img[hidden] { display: none; }
</style>
