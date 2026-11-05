<script lang="ts">
  import { segmentShares, type StatusSegment, type StatusTone } from "./helpers";

  let {
    items,
    segments = [],
    label = "Page status",
  }: {
    items: readonly { label: string; tone?: StatusTone; emphasis?: boolean }[];
    segments?: readonly StatusSegment[];
    label?: string;
  } = $props();

  const shares = $derived(segmentShares(segments));
  const segmentLabel = $derived(shares.map(segment => `${segment.label}: ${segment.value}`).join(", "));
</script>

<section class="page-status" aria-label={label}>
  <ul class="status-items">
    {#each items as item, index}
      <li data-tone={item.tone ?? "neutral"}>
        {#if index === 0 || item.emphasis}<strong>{item.label}</strong>{:else}<span>{item.label}</span>{/if}
      </li>
    {/each}
  </ul>
  {#if segments.length}
    <div class="status-bar" role="img" aria-label={segmentLabel}>
      {#each shares as segment}
        {#if segment.percentage > 0}
          <span data-tone={segment.tone} style:width={`${segment.percentage}%`} title={`${segment.label}: ${segment.value}`}></span>
        {/if}
      {/each}
    </div>
    <ul class="status-legend" aria-label="Status breakdown">
      {#each shares as segment}
        <li data-tone={segment.tone}>
          <span class="legend-swatch" aria-hidden="true"></span>
          <span>{segment.label}</span>
          <span class="legend-count">{segment.value}</span>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  .page-status { display: grid; min-width: 0; gap: var(--space-3); }
  .status-items, .status-legend { display: flex; min-width: 0; flex-wrap: wrap; align-items: baseline; gap: var(--space-2) var(--space-4); margin: 0; padding: 0; list-style: none; }
  li { display: inline-flex; min-width: 0; max-width: 100%; align-items: center; gap: var(--space-2); color: var(--color-ink-muted); font-size: var(--text-sm); overflow-wrap: anywhere; }
  strong { color: var(--color-ink); font-weight: 600; }
  .status-items [data-tone="warn"], .status-items [data-tone="warn"] strong { color: var(--warning); }
  .legend-swatch { width: 10px; height: 10px; flex: 0 0 auto; border-radius: calc(var(--radius-sm) / 4); background: var(--tone); }
  [data-tone="neutral"] { --tone: var(--color-ink-muted); }
  [data-tone="good"] { --tone: var(--success); }
  [data-tone="warn"] { --tone: var(--warning); }
  [data-tone="stop"] { --tone: var(--danger); }
  [data-tone="info"] { --tone: var(--info); }
  .status-bar { display: flex; width: 100%; height: 10px; gap: 2px; overflow: hidden; border-radius: var(--radius-sm); background: var(--color-panel-raised); }
  .status-bar > span { min-width: 4px; height: 100%; flex-shrink: 1; background: var(--tone); }
  .legend-count { color: var(--color-ink); font-variant-numeric: tabular-nums; }
</style>
