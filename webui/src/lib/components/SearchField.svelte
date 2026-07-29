<script lang="ts">
  import { Search } from "lucide-svelte";

  let {
    value = $bindable(""),
    label,
    placeholder,
    hiddenLabel = false,
    class: className = "",
  }: {
    value: string;
    label: string;
    placeholder: string;
    hiddenLabel?: boolean;
    class?: string;
  } = $props();
</script>

<label class={`search-field ${className}`}>
  <span class:sr-only={hiddenLabel}>{label}</span>
  <span class="search-input">
    <Search size={16} aria-hidden="true" />
    <input bind:value type="search" {placeholder} />
  </span>
</label>
