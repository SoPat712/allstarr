<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";

  let {
    id,
    definition,
    label = definition?.name ?? id,
    decorative = false,
    class: className = "",
  }: {
    id: string;
    definition?: ProviderDefinition;
    label?: string;
    decorative?: boolean;
    class?: string;
  } = $props();
</script>

<span class={`media-art provider-art ${className}`.trim()}>
  <ProviderMark {id} {definition} {label} {decorative} />
</span>
