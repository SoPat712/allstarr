<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import { providerColor } from "$lib/playlists";

  let {
    id,
    definition,
    label = definition?.name ?? id,
  }: { id: string; definition?: ProviderDefinition; label?: string } = $props();
  let failed = $state(false);
</script>

<span class="provider-mark" style={`--route-color:${providerColor(id)}`} title={label}>
  {#if (definition?.logoUrl || definition?.icon) && !failed}
    <img src={definition.logoUrl || `/images/providers/${definition.icon}.svg`} alt="" onerror={() => { failed = true; }} />
  {:else}
    <span aria-hidden="true">{label[0]?.toUpperCase() ?? "?"}</span>
  {/if}
  <span class="sr-only">{label}</span>
</span>
