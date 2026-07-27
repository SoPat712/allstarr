<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";

  let {
    id,
    definition,
    label = definition?.name ?? id,
    class: className = "",
  }: {
    id: string;
    definition?: ProviderDefinition;
    label?: string;
    class?: string;
  } = $props();
</script>

<span class={`media-art provider-art ${className}`.trim()}>
  <ProviderMark {id} {definition} {label} />
</span>
