<script lang="ts">
  import type { Snippet } from "svelte";
  import type { HTMLAttributes } from "svelte/elements";

  let { element = $bindable(), label = "Last 30 checks", children, ...attributes }: HTMLAttributes<HTMLDivElement> & {
    element?: HTMLDivElement;
    label?: string;
    children: Snippet;
  } = $props();
</script>

<div bind:this={element} class="check-strip" role="group" aria-label={label} {...attributes}>
  {@render children()}
</div>

<style>
  .check-strip { display: flex; align-items: center; justify-content: flex-end; width: max-content; max-width: 100%; height: 26px; gap: 2px; }
  .check-strip[tabindex] { cursor: pointer; }
  .check-strip:focus-visible { outline: 2px solid var(--color-ring); outline-offset: 4px; border-radius: var(--radius-sm); }
  .check-strip > :global(span), .check-strip > :global(button) { flex: 0 0 5px; width: 5px; min-width: 0; min-height: 0; height: 26px; border: 0; padding: 0; border-radius: 2px; background: var(--color-ink-muted); }
  .check-strip > :global(button) { min-height: 0 !important; cursor: pointer; }
  .check-strip > :global([data-active="true"]) { outline: 2px solid var(--color-ink); outline-offset: 1px; }
  .check-strip > :global([data-outcome="empty"]) { opacity: .18; }
  .check-strip > :global([data-outcome="neutral"]) { opacity: .55; }
  .check-strip > :global([data-outcome="ok"]) { background: var(--success); }
  .check-strip > :global([data-outcome="slow"]) { background: var(--warning); }
  .check-strip > :global([data-outcome="failed"]) { background: var(--danger); }
  @media (max-width: 899px), (pointer: coarse) { .check-strip { height: 44px; } }
</style>
