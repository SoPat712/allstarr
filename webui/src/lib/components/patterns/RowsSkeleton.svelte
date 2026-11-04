<script lang="ts">
  import { Skeleton } from "$lib/components/ui/skeleton";

  let { count = 6, label = "Loading items" }: { count?: number; label?: string } = $props();
  const rows = $derived(Array.from({ length: Number.isFinite(count) ? Math.max(0, Math.floor(count)) : 6 }));
</script>

<div class="rows-skeleton" role="status" aria-busy="true" aria-label={label}>
  <span class="sr-only">{label}…</span>
  {#each rows as _}
    <div class="skeleton-row" aria-hidden="true">
      <Skeleton class="row-square" />
      <div class="skeleton-copy"><Skeleton class="row-title" /><Skeleton class="row-subtitle" /></div>
    </div>
  {/each}
</div>

<style>
  .rows-skeleton { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); background: var(--color-panel); }
  .skeleton-row { position: relative; display: flex; min-height: var(--data-row-height); align-items: center; gap: var(--space-3); overflow: hidden; border-bottom: 1px solid var(--color-edge); padding: var(--space-3) var(--space-4); }
  .skeleton-row:last-child { border-bottom: 0; }
  .skeleton-row::after { position: absolute; inset: 0; transform: translateX(-100%); background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--color-ink) 4%, transparent), transparent); content: ""; animation: row-shimmer 1.6s ease-in-out infinite; }
  .skeleton-copy { display: grid; width: min(65%, 24rem); gap: var(--space-2); }
  .rows-skeleton :global([data-slot="skeleton"]) { animation: none; }
  .rows-skeleton :global(.row-square) { width: var(--data-artwork-size); height: var(--data-artwork-size); flex: 0 0 auto; border-radius: var(--radius-sm); }
  .rows-skeleton :global(.row-title) { width: 100%; height: var(--space-3); }
  .rows-skeleton :global(.row-subtitle) { width: 65%; height: var(--space-2); }
  @keyframes row-shimmer { to { transform: translateX(100%); } }
  @media (prefers-reduced-motion: reduce) { .skeleton-row::after { animation: none; display: none; } }
</style>
