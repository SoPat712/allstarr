<script lang="ts" module>
  import type { ProviderScore } from "$lib/api";

  /** Lyrics are judged on lyrics alone. Every other order decides which source plays, so it uses the overall score. */
  export function capabilityScore(score: ProviderScore | undefined, capability: string) {
    if (!score) return null;
    return capability === "lyrics"
      ? score.capabilities.find((item) => item.capability === capability)?.score ?? score.score
      : score.score;
  }

  export const scoreTier = (value: number | null) =>
    value == null ? "none" : value >= 90 ? "great" : value >= 70 ? "good" : value >= 50 ? "fair" : "poor";
</script>

<script lang="ts">
  import { checkGuide, humanize } from "$lib/sources";

  let { score, capability }: { score: ProviderScore; capability: string } = $props();

  const seconds = (value: number | null) => value == null ? "—" : value < 1000 ? `${value} ms` : `${(value / 1000).toFixed(1)} s`;
  const days = $derived(score.days.slice(-14));
</script>

<div class="score-details">
  <p class="score-summary">
    {#if score.score == null}Not rated yet. A score needs at least 5 checks; this source has {score.checks}.
    {:else if score.quality}Passed {score.uptimePercent}% of {score.checks.toLocaleString()} checks and playback tests in the last 30 days. The score is 50% reliability, with the last week counting double, 30% audio quality and 20% speed. Lossy sources have a ceiling: 60 at low bitrates, up to 80 at 256 kbps or more.
    {:else}Passed {score.uptimePercent}% of {score.checks.toLocaleString()} checks in the last 30 days. This source plays no audio here, so its score is 80% reliability, with the last week counting double, and 20% speed.{/if}
  </p>
  {#if score.quality}
    <p class="score-quality"><strong>Delivers {score.quality.typical}</strong>{#if score.quality.best} · up to {score.quality.best}{/if} <small>from {score.quality.samples} playback tests</small></p>
  {/if}
  {#if days.length}
    <div class="score-days" role="img" aria-label={`Daily check results for the last ${days.length} days`}>
      {#each days as day (day.day)}
        <span title={`${day.day}: ${day.passed} passed, ${day.failed} failed`} style={`--passed:${day.passed + day.failed ? day.passed / (day.passed + day.failed) * 100 : 0}%`}></span>
      {/each}
    </div>
  {/if}
  <div class="score-rows">
    {#each score.capabilities as item (item.capability)}
      <div class="score-row" class:current={item.capability === capability}>
        <strong>{humanize(item.capability)}</strong>
        <span class="score-bar" aria-hidden="true"><span style={`width:${item.uptimePercent ?? 0}%`}></span></span>
        <span>{item.uptimePercent ?? 0}%</span>
        <small>{seconds(item.typicalLatencyMilliseconds)} typical{#if item.failures} · {item.failures} failed{#if item.lastFailureCode}, last: {checkGuide(item.lastFailureCode)?.label ?? humanize(item.lastFailureCode)}{/if}{/if}</small>
      </div>
    {/each}
  </div>
</div>

<style>
  .score-details { grid-column: 1 / -1; display: grid; gap: var(--space-3); padding: 0 var(--space-3) var(--space-4) 5.5rem; animation: list-enter 280ms var(--ease-emphasized) backwards; }
  .score-summary { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .score-quality { font-size: var(--text-sm); }
  .score-quality strong { font-weight: 600; }
  .score-quality small { color: var(--color-ink-muted); }
  .score-days { display: flex; align-items: flex-end; gap: 3px; height: 2rem; }
  .score-days span { position: relative; flex: 1; max-width: 1.25rem; height: 100%; overflow: hidden; border-radius: 3px; background: var(--md-sys-color-error-container); }
  .score-days span::after { position: absolute; right: 0; bottom: 0; left: 0; height: var(--passed); background: var(--success); content: ""; }
  .score-rows { display: grid; gap: var(--space-2); }
  .score-row { display: grid; grid-template-columns: 6rem minmax(3rem, 1fr) 3.25rem minmax(0, 2fr); align-items: center; gap: var(--space-3); font-size: var(--text-sm); }
  .score-row strong { font-weight: 500; }
  .score-row.current strong { font-weight: 700; }
  .score-row small { overflow: hidden; color: var(--color-ink-muted); font-size: 0.8125rem; text-overflow: ellipsis; white-space: nowrap; }
  .score-bar { height: 6px; overflow: hidden; border-radius: 999px; background: color-mix(in srgb, var(--color-ink) 12%, transparent); }
  .score-bar span { display: block; height: 100%; border-radius: inherit; background: var(--color-signal); transition: width 480ms var(--ease-emphasized); }
  @media (max-width: 760px) {
    .score-details { padding-inline: var(--space-3); }
    .score-row { grid-template-columns: 5rem minmax(0, 1fr) 3rem; }
    .score-row small { grid-column: 1 / -1; white-space: normal; }
  }
</style>
