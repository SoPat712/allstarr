<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import { providerColor } from "$lib/playlists";

  let {
    id,
    definition,
    label = definition?.name ?? id,
  }: { id: string; definition?: ProviderDefinition; label?: string } = $props();
  let failed = $state(false);
  const icon = $derived(
    definition?.icon && definition.icon !== "extension"
      ? definition.icon
      : label.toLowerCase().replace(/[^a-z0-9]/g, ""),
  );
  const source = $derived(definition?.logoUrl || `/images/providers/${encodeURIComponent(icon || id.toLowerCase())}.svg`);

  $effect(() => {
    source;
    failed = false;
  });
</script>

<span class="provider-mark" style={`--route-color:${providerColor(id)}`}>
  {#if !failed}
    <img src={source} alt="" onerror={() => { failed = true; }} />
  {:else}
    <span aria-hidden="true">{label[0]?.toUpperCase() ?? "?"}</span>
  {/if}
  <span class="sr-only">{label}</span>
</span>
