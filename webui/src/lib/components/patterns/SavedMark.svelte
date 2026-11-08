<script lang="ts">
  import { fade, fly } from "svelte/transition";
  import { motion } from "$lib/motion";

  let { show, label = "Saved" }: { show: boolean; label?: string } = $props();
</script>

{#if show}
  <small class="saved-mark" role="status" in:fly={{ y: 3, duration: motion(160) }} out:fade={{ duration: motion(240) }}>
    <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M2.5 6.5 5 9l4.5-6" /></svg>{label}
  </small>
{/if}

<style>
  .saved-mark { display: inline-flex; align-items: center; gap: 4px; color: var(--success); font-size: var(--text-xs); }
  svg { width: 12px; height: 12px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
  path { stroke-dasharray: 12; stroke-dashoffset: 12; animation: saved-draw 280ms var(--ease-emphasized) 80ms forwards; }
  @keyframes saved-draw { to { stroke-dashoffset: 0; } }
  @media (prefers-reduced-motion: reduce) { path { animation: none; stroke-dashoffset: 0; } }
</style>
