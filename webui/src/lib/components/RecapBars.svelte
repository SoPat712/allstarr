<script lang="ts">
  import { formatCount } from "$lib/intelligence";

  type Point = { key: string; label: string; axis?: string; value: number };
  let { points, label, unit = "plays", compact = false }: { points: Point[]; label: string; unit?: string; compact?: boolean } = $props();

  let active = $state<number | null>(null);
  const peak = $derived(Math.max(1, ...points.map(point => point.value)));
  // Round the top of the scale so the two labelled lines land on clean numbers.
  const top = $derived.by(() => {
    if (compact) return peak;
    const step = 10 ** Math.floor(Math.log10(peak));
    return Math.ceil(peak / (step / 2)) * (step / 2);
  });
  const scaleLabel = (value: number) => value >= 1000 ? `${Number((value / 1000).toFixed(2))}K` : String(value);
  const sparse = $derived(points.length > 40);
  const shown = $derived(active === null ? null : points[active]);
  const tipOffset = $derived(active === null ? 50 : (active + .5) / points.length * 100);
</script>

<div class="recap-bars" class:sparse class:compact role="group" aria-label={label}>
  <div class="plot" role="presentation" onmouseleave={() => active = null}>
    {#each compact ? [] : [1, .5] as line}
      <div class="grid-line" style:bottom={`${line * 100}%`}><span>{scaleLabel(top * line)}</span></div>
    {/each}
    <div class="baseline"></div>
    <div class="columns">
      {#each points as point, index (point.key)}
        <button type="button" class="column" class:active={active === index} aria-label={`${point.label}: ${formatCount(point.value)} ${unit}`}
          onmouseenter={() => active = index} onfocus={() => active = index} onblur={() => active = null}>
          <span class="bar" style:height={`${point.value / top * 100}%`} class:empty={!point.value}></span>
        </button>
      {/each}
    </div>
    {#if shown}
      <div class="tip" class:flip={tipOffset > 70} class:start={tipOffset < 30} style:left={`${tipOffset}%`} aria-hidden="true">
        <strong>{formatCount(shown.value)} {unit}</strong><small>{shown.label}</small>
      </div>
    {/if}
  </div>
  <div class="axis" aria-hidden="true">
    {#each points as point (point.key)}<span>{#if point.axis}<em>{point.axis}</em>{/if}</span>{/each}
  </div>
</div>

<style>
  .recap-bars { display: grid; gap: var(--space-2); padding-left: 2.5rem; min-width: 0; }
  .compact { padding-left: 0; }
  .compact .plot { height: 64px; }
  .compact .columns { gap: 10px; }
  .compact .column { padding: 0; }
  .compact .bar { max-width: none; }
  .plot { position: relative; height: 180px; }
  .grid-line { position: absolute; left: 0; right: 0; border-top: 1px solid var(--color-edge); }
  .grid-line span { position: absolute; right: 100%; top: 0; transform: translateY(-50%); padding-right: var(--space-2); color: var(--color-ink-muted); font-size: .75rem; font-variant-numeric: tabular-nums; white-space: nowrap; }
  .baseline { position: absolute; left: 0; right: 0; bottom: 0; border-top: 1px solid var(--color-edge); }
  .columns { position: absolute; inset: 0; display: grid; grid-auto-flow: column; grid-auto-columns: minmax(0, 1fr); align-items: stretch; }
  .column { display: flex; align-items: flex-end; justify-content: center; min-width: 0; height: 100%; padding: 0 1px; margin: 0; border: 0; background: transparent; cursor: default; border-radius: 4px 4px 0 0; }
  .column:hover, .column.active { background: color-mix(in srgb, var(--md-sys-color-primary) 10%, transparent); }
  .column:focus-visible { outline: 2px solid var(--md-sys-color-primary); outline-offset: -2px; }
  .bar { display: block; width: 100%; max-width: 24px; min-height: 2px; background: var(--md-sys-color-primary); border-radius: 4px 4px 0 0; }
  .bar.empty { min-height: 0; }
  .sparse .column { padding: 0; }
  .sparse .bar { border-radius: 1px 1px 0 0; margin-inline: .5px; }
  .tip { position: absolute; top: 0; transform: translateX(-50%); display: grid; gap: 2px; padding: var(--space-2) var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--md-sys-color-surface-container-highest); box-shadow: 0 2px 8px rgb(0 0 0 / 20%); pointer-events: none; white-space: nowrap; z-index: 1; }
  .tip.flip { transform: translateX(-100%); }
  .tip.start { transform: none; }
  .tip strong { font-size: var(--text-sm); font-weight: 600; }
  .tip small { color: var(--color-ink-muted); font-size: .75rem; }
  .axis { display: grid; grid-auto-flow: column; grid-auto-columns: minmax(0, 1fr); height: 1rem; }
  .axis span { position: relative; min-width: 0; }
  .axis em { position: absolute; left: 50%; transform: translateX(-50%); color: var(--color-ink-muted); font-size: .75rem; font-style: normal; white-space: nowrap; }
</style>
