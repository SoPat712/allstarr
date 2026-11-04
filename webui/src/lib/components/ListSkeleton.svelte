<script lang="ts">
  let { rows = 6, avatar = true, label = "Loading" }: { rows?: number; avatar?: boolean; label?: string } = $props();
</script>

<div class="list-skeleton" role="status" aria-busy="true" aria-label={label}>
  {#each Array.from({ length: rows }) as _}
    <div class="list-skeleton-row" aria-hidden="true">
      {#if avatar}<span class="list-skeleton-placeholder list-skeleton-avatar"></span>{/if}
      <div class="list-skeleton-text">
        <span class="list-skeleton-placeholder list-skeleton-bar"></span>
        <span class="list-skeleton-placeholder list-skeleton-bar list-skeleton-short"></span>
      </div>
    </div>
  {/each}
</div>

<style>
  .list-skeleton { display: grid; gap: var(--space-4); padding: var(--surface-gutter); border-radius: var(--radius-lg); background: var(--color-panel); }
  .list-skeleton-row { display: flex; align-items: center; gap: var(--space-3); min-width: 0; }
  .list-skeleton-placeholder { display: block; background: var(--color-surface-raised); border-radius: var(--radius-sm); }
  .list-skeleton-avatar { flex: none; width: 2.75rem; height: 2.75rem; }
  .list-skeleton-text { display: grid; flex: 1; gap: var(--space-2); min-width: 0; }
  .list-skeleton-bar { width: 65%; height: .75rem; }
  .list-skeleton-short { width: 40%; height: .625rem; }
  @media (prefers-reduced-motion: no-preference) {
    .list-skeleton-placeholder { background: linear-gradient(100deg, var(--color-surface-raised) 25%, var(--md-sys-color-surface-container-highest) 50%, var(--color-surface-raised) 75%); background-size: 200% 100%; animation: shimmer 2.4s ease-in-out infinite; }
  }
  @keyframes shimmer { from { background-position: 200% 0; } to { background-position: -200% 0; } }
</style>
