<script lang="ts">
  import { ChevronDown } from "@lucide/svelte";

  let { title, description = "" }: { title: string; description?: string } = $props();
</script>

<span class="disclosure-label">
  <span><strong>{title}</strong>{#if description}<small>{description}</small>{/if}</span>
  <ChevronDown class="disclosure-chevron" size={16} aria-hidden="true" />
</span>
