<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import { providerColor } from "$lib/playlists";
  import { providerMarkSources } from "$lib/provider-marks";

  let {
    id,
    definition,
    label = definition?.name ?? id,
    decorative = false,
  }: { id: string; definition?: ProviderDefinition; label?: string; decorative?: boolean } = $props();
  let sourceIndex = $state(0);
  let loaded = $state(false);
  const sources = $derived(providerMarkSources(id, definition, label));
  const source = $derived(sources[sourceIndex]);

  $effect(() => {
    sources;
    sourceIndex = 0;
    loaded = false;
  });
</script>

<span class="provider-mark" class:provider-mark--monogram={!loaded} style={`--route-color:${providerColor(id)}`}>
  {#if !loaded}<span class="provider-monogram" aria-hidden="true">{(label.trim() || id)[0]?.toUpperCase() ?? "?"}</span>{/if}
  {#if source}
    <img src={source} alt="" hidden={!loaded} onload={() => { loaded = true; }} onerror={() => { sourceIndex += 1; loaded = false; }} />
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
