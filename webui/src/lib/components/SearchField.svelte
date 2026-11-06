<script lang="ts">
  import { Search } from "@lucide/svelte";

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

<style>
  .search-field { display: grid; min-width: 0; gap: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-sm); }
  .search-input { position: relative; display: block; min-width: 0; }
  .search-input :global(svg) { position: absolute; top: 50%; left: .875rem; width: 1rem; height: 1rem; color: var(--color-ink-muted); transform: translateY(-50%); pointer-events: none; }
  label.search-field .search-input input { width: 100%; min-width: 0; height: var(--control-md); border: 1px solid var(--color-edge-strong); border-radius: 999px; padding-inline: 2.5rem .875rem; background: transparent; color: var(--color-ink); font: inherit; }
  label.search-field .search-input input:focus-visible { border-color: var(--color-signal); outline: 2px solid var(--color-signal); outline-offset: 2px; }
  @media (max-width: 899px) { label.search-field .search-input input { height: 44px; } }
</style>
