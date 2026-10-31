<script lang="ts">
  let {
    matched,
    review,
    unmatched,
    compact = false,
  }: {
    matched: number;
    review: number;
    unmatched: number;
    compact?: boolean;
  } = $props();

  const total = $derived(matched + review + unmatched);
  const segments = $derived([
    { label: "Matched", count: matched, color: "var(--color-success)" },
    { label: "Needs review", count: review, color: "var(--color-warning)" },
    { label: "Not matched", count: unmatched, color: "var(--color-danger)" },
  ]);
  const songs = (count: number) => `${count} ${count === 1 ? "song" : "songs"}`;
  let tip = $state<{ label: string; x: number; y: number } | null>(null);
  function point(event: PointerEvent) {
    const label = (event.target as HTMLElement).dataset.label;
    const bar = (event.currentTarget as HTMLElement).getBoundingClientRect();
    tip = label ? { label, x: event.clientX, y: bar.top + bar.height - 4 } : null;
  }
</script>

<span
  class:compact
  class="coverage-bar"
  role="img"
  aria-label={`${matched} matched, ${review} needs review, ${unmatched} not matched`}
  onpointermove={point}
  onpointerleave={() => tip = null}
>
  {#each segments.filter((segment) => segment.count > 0) as segment}
    <span
      style={`width:${segment.count / total * 100}%;--route-color:${segment.color}`}
      data-label={`${segment.label} · ${songs(segment.count)}`}
    ></span>
  {/each}
</span>
{#if tip}<span class="coverage-tip" aria-hidden="true" style={`left:${tip.x}px;top:${tip.y}px`}>{tip.label}</span>{/if}

<style>
  /* A taller invisible hover area keeps the thin strip easy to point at. */
  .coverage-bar { --strip: .28rem; }
  .coverage-bar.compact { --strip: .42rem; }
  :global(.playlist-row) > .coverage-bar {
    z-index: calc(var(--layer-content) + 1);
    height: .9rem;
    background: linear-gradient(color-mix(in srgb, var(--color-ink) 10%, transparent) 0 0) bottom / 100% var(--strip) no-repeat;
  }
  :global(.playlist-row) > .coverage-bar > span { min-width: 0; background: linear-gradient(var(--route-color) 0 0) bottom / 100% var(--strip) no-repeat; }
  .coverage-tip {
    position: fixed; z-index: 130; transform: translate(-50%, calc(-100% - .75rem)); pointer-events: none; white-space: nowrap;
    padding: .375rem .625rem; border-radius: .5rem; background: var(--md-sys-color-inverse-surface);
    color: var(--md-sys-color-inverse-on-surface); font-size: var(--text-xs); font-weight: 600; box-shadow: 0 4px 12px #0003;
  }
</style>
