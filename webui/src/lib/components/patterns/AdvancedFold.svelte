<script lang="ts">
  import type { Snippet } from "svelte";

  let { label = "Advanced", description = "", open = $bindable(false), class: className = "", children }: {
    label?: string;
    description?: string;
    open?: boolean;
    class?: string;
    children?: Snippet;
  } = $props();
</script>

<details class={`settings-fold ${className}`} bind:open>
  <summary><span>{label}{#if description}: {description}{/if}</span></summary>
  {@render children?.()}
</details>

<style>
  details { min-width: 0; overflow: hidden; border: 0; border-top: 1px solid var(--color-edge); background: var(--color-panel); }
  summary { display: flex; min-height: 44px; align-items: center; justify-content: space-between; gap: var(--space-3); cursor: pointer; list-style: none; padding: var(--space-3) var(--space-4); color: var(--color-signal-text); font-size: var(--text-sm); font-weight: 500; }
  summary::-webkit-details-marker { display: none; }
  summary::after { width: 7px; height: 7px; flex: 0 0 auto; border-right: 1.5px solid currentColor; border-bottom: 1.5px solid currentColor; transform: rotate(45deg); content: ""; }
  details[open] > summary { border-bottom: 1px solid var(--color-edge); }
  details[open] > summary::after { transform: rotate(225deg); }
</style>
