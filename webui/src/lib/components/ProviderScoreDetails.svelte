<script lang="ts">
  import { scoreParts } from "$lib/provider-score";
  import { checkGuide, humanize } from "$lib/sources";

  import type { ProviderScore } from "$lib/api";

  let { score, capability, ranked = true }: { score: ProviderScore; capability: string; ranked?: boolean } = $props();

  const seconds = (value: number | null) => value == null ? "—" : value < 1000 ? `${value} ms` : `${(value / 1000).toFixed(1)} s`;
  const days = $derived(score.days.slice(-14));
  const parts = $derived(ranked ? scoreParts(score, capability) : null);
  const scope = $derived(capability === "lyrics" ? score.capabilities.find(item => item.capability === capability) ?? score : score);
  const playsAudio = $derived(Boolean(parts?.qualityOutOf));
  const timing = $derived(parts?.typicalMilliseconds ?? null);
  const shares = $derived(parts ? [
    { label: "Reliability", points: parts.reliability, outOf: parts.reliabilityOutOf, detail: `Passed ${scope.uptimePercent}% of ${scope.checks.toLocaleString()} checks` },
    { label: "Speed", points: parts.speed, outOf: parts.speedOutOf, detail: timing == null ? "Not timed yet" : `${playsAudio ? "Songs start" : "Answers"} in about ${seconds(timing)}` },
    ...(playsAudio ? [{ label: "Quality", points: parts.quality, outOf: parts.qualityOutOf, detail: score.quality?.lossless ? "Lossless" : "Lossy" }] : []),
  ] : []);
</script>

<div class="score-details">
  <p class="score-summary">
    {#if !ranked}You choose this source yourself and Allstarr never picks it automatically, so it isn't scored or ranked. It passed {score.uptimePercent}% of {score.checks.toLocaleString()} checks in the last 30 days.
    {:else if score.score == null}Not rated yet. A score needs at least 5 checks; this source has {score.checks}.
    {:else if playsAudio}Measured over the last 30 days, with the last week counting double. Speed earns full points at 0.5 s and none at 8 s. Lossy sources are held to a ceiling: 60 at low bitrates, up to 80 at 256 kbps or more.
    {:else}Measured over the last 30 days, with the last week counting double. Speed earns full points at 0.5 s and none at 8 s.{/if}
  </p>
  {#if shares.length}
    <div class="score-parts">
      {#each shares as share (share.label)}
        <div class="score-part">
          <span class="score-part-head"><strong>{share.label}</strong><span>{share.points} of {share.outOf}</span></span>
          <span class="score-bar" aria-hidden="true"><span style={`width:${share.points / share.outOf * 100}%`}></span></span>
          <small>{share.detail}</small>
        </div>
      {/each}
    </div>
  {/if}
  {#if parts?.cappedAt != null}
    <p class="score-cap">Earned {parts.earned}, held at {parts.cappedAt} because this source delivers {score.quality?.typical ?? "lossy audio"}.</p>
  {/if}
  {#if score.quality}
    <p class="score-quality"><strong>Delivers {score.quality.typical}</strong>{#if score.quality.best}{" · "}up to {score.quality.best}{/if} <small>from {score.quality.samples} playback tests</small></p>
  {/if}
  {#if days.length}
    <div class="score-days" role="img" aria-label={`Daily check results for the last ${days.length} days`}>
      {#each days as day (day.day)}
        <span title={`${day.day}: ${day.passed} passed, ${day.failed} failed`} style={`--passed:${day.passed + day.failed ? day.passed / (day.passed + day.failed) * 100 : 0}%`}></span>
      {/each}
    </div>
  {/if}
  <div class="score-rows">
    <span class="score-rows-title">Checks passed by feature</span>
    {#each score.capabilities as item (item.capability)}
      <div class="score-row" class:current={item.capability === capability}>
        <strong>{humanize(item.capability)}</strong>
        <span class="score-bar" aria-hidden="true"><span style={`width:${item.uptimePercent ?? 0}%`}></span></span>
        <span>{item.uptimePercent ?? 0}%</span>
        <small>{seconds(item.typicalLatencyMilliseconds)} typical{#if item.failures}{" · "}{item.failures} failed{#if item.lastFailureCode}, last: {checkGuide(item.lastFailureCode)?.label ?? humanize(item.lastFailureCode)}{/if}{/if}</small>
      </div>
    {/each}
  </div>
</div>

<style>
  .score-details { grid-column: 1 / -1; display: grid; gap: var(--space-3); padding: 0 var(--space-3) var(--space-4) 5.5rem; animation: list-enter 280ms var(--ease-emphasized) backwards; }
  .score-summary { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .score-parts { display: grid; grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr)); gap: var(--space-4); }
  .score-part { display: grid; gap: var(--space-1); font-size: var(--text-sm); }
  .score-part-head { display: flex; justify-content: space-between; gap: var(--space-2); }
  .score-part-head strong { font-weight: 600; }
  .score-part small { color: var(--color-ink-muted); font-size: 0.8125rem; }
  .score-cap { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .score-rows-title { color: var(--color-ink-muted); font-size: 0.8125rem; font-weight: 600; }
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
