<script lang="ts">
  import { segmentShares, type StatusSegment } from "./helpers";
  let { segments, label, compact = false }: { segments: readonly StatusSegment[]; label?: string; compact?: boolean } = $props();
  const shares = $derived(segmentShares(segments));
</script>

<div class="status-gauge" class:status-bar={!compact} class:compact role={label ? "img" : undefined} aria-label={label} aria-hidden={label ? undefined : true}>
  {#each shares as segment}
    {#if segment.percentage > 0}<span data-tone={segment.tone} style:width={`${segment.percentage}%`} title={`${segment.label}: ${segment.value}`}></span>{/if}
  {/each}
</div>

<style>
  .status-gauge { display: flex; width: 100%; height: 10px; gap: 2px; overflow: hidden; border-radius: var(--radius-sm); background: var(--color-panel-raised); }
  .compact { height: 5px; gap: 1px; }
  span { height: 100%; min-width: 4px; flex-shrink: 1; background: var(--color-ink-muted); }
  .compact span { min-width: 1px; }
  [data-tone="good"] { background: var(--success); }
  [data-tone="warn"] { background: var(--warning); }
  [data-tone="stop"] { background: var(--danger); }
  [data-tone="info"] { background: var(--color-signal); }
  [data-tone="accent"] { background: var(--md-sys-color-tertiary); }
  [data-tone="empty"] { background: transparent; }
</style>
