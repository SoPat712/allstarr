<script lang="ts">
  import { segmentShares, type StatusSegment, type StatusTone } from "./helpers";
  import StatusGauge from "./StatusGauge.svelte";

  let {
    items,
    segments = [],
    label = "Page status",
    separated = false,
  }: {
    items: readonly { label: string; tone?: StatusTone; emphasis?: boolean; href?: string; hideOnPhone?: boolean }[];
    segments?: readonly StatusSegment[];
    label?: string;
    separated?: boolean;
  } = $props();

  const shares = $derived(segmentShares(segments));
  const segmentLabel = $derived(shares.map(segment => `${segment.label}: ${segment.value}`).join(", "));
</script>

<section class="page-status" aria-label={label}>
  <ul class="status-items" class:separated>
    {#each items as item, index}
      <li data-tone={item.tone ?? "neutral"} class:phone-hidden={item.hideOnPhone}>
        {#if item.href}<a href={item.href}>{item.label}</a>{:else if index === 0 || item.emphasis}<strong>{item.label}</strong>{:else}<span>{item.label}</span>{/if}
      </li>
    {/each}
  </ul>
  {#if segments.length}
    <StatusGauge {segments} label={segmentLabel} />
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
  .status-items [data-tone="stop"], .status-items [data-tone="stop"] strong { color: var(--danger); }
  .status-items a { color: inherit; text-decoration: underline; text-underline-offset: 3px; }
  .separated { gap: var(--space-2); }
  .separated li + li::before { content: "·"; color: var(--color-ink-muted); }
  .legend-swatch { width: 10px; height: 10px; flex: 0 0 auto; border-radius: calc(var(--radius-sm) / 4); background: var(--tone); }
  [data-tone="neutral"] { --tone: var(--color-ink-muted); }
  [data-tone="good"] { --tone: var(--success); }
  [data-tone="warn"] { --tone: var(--warning); }
  [data-tone="stop"] { --tone: var(--danger); }
  [data-tone="info"] { --tone: var(--info); }
  [data-tone="accent"] { --tone: var(--md-sys-color-tertiary); }
  [data-tone="empty"] { --tone: transparent; }
  .legend-count { color: var(--color-ink); font-variant-numeric: tabular-nums; }
  @media (max-width: 650px) { .phone-hidden { display: none; } }
</style>
