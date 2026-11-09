<script lang="ts">
  import type { Snippet } from "svelte";

  let { label = "Advanced", description = "", open = $bindable(false), placement = "card", class: className = "", children }: {
    label?: string;
    description?: string;
    open?: boolean;
    // "card" is a card's last row, "standalone" is a card of its own, "flush" lines up with the content around it, as in a side panel.
    placement?: "card" | "standalone" | "flush";
    class?: string;
    children?: Snippet;
  } = $props();
</script>

<details class={`settings-fold ${className}`} data-placement={placement} bind:open>
  <summary>{label}{#if description}: {description}{/if}</summary>
  {@render children?.()}
</details>

<style>
  details { min-width: 0; overflow: hidden; border: 0; border-top: 1px solid var(--color-edge); background: var(--color-panel); }
  details[data-placement="standalone"] { border: 1px solid var(--color-edge); border-radius: var(--radius-list); }
  details[data-placement="flush"] { background: none; }
  summary { display: flex; min-height: 44px; align-items: center; justify-content: space-between; gap: var(--space-3); cursor: pointer; list-style: none; padding: var(--space-3) var(--space-4); color: var(--color-signal-text); font-size: var(--text-sm); font-weight: 500; }
  [data-placement="flush"] > summary { padding-inline: 0; }
  summary::-webkit-details-marker { display: none; }
  summary::after { width: 7px; height: 7px; flex: 0 0 auto; border-right: 1.5px solid currentColor; border-bottom: 1.5px solid currentColor; transform: rotate(45deg); content: ""; }
  details[open] > summary { border-bottom: 1px solid var(--color-edge); }
  details[open] > summary::after { transform: rotate(225deg); }
</style>
