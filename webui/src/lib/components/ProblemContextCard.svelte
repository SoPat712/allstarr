<script lang="ts">
  import type { ProviderDefinition } from "$lib/api";
  import { humanize } from "$lib/sources";
  import { reportSourceName, supportDate, type ProblemContext } from "$lib/support";
  import { Button } from "$lib/components/ui/button";
  let { context, providers = [], review = false, onReviewLyrics, onReviewMatch, matchLoading = false, matchReviewNote = "" }: {
    context: ProblemContext;
    providers?: ProviderDefinition[];
    review?: boolean;
    onReviewLyrics?: (trigger: HTMLElement) => void;
    onReviewMatch?: (trigger: HTMLElement) => void;
    matchLoading?: boolean;
    matchReviewNote?: string;
  } = $props();
  const provider = (id?: string | null) => reportSourceName(providers, id);
</script>

<article class="settings-card" aria-label="Included report details">
  <header><div><h3>{context.song ? "Song details" : "General report"}</h3><small>Captured {supportDate(context.capturedAt)}</small></div></header>
  {#if context.song}
    <div class="settings-row"><div><strong>Song</strong><small>{context.song.artist ?? "Artist not captured"}{context.song.album ? ` · ${context.song.album}` : ""}</small></div><output>{context.song.title ?? "Title not captured"}</output></div>
    <div class="settings-row"><div><strong>{context.playback?.sourceConfirmed ? "Playing from" : "Catalog source"}</strong><small>{context.playback?.sourceConfirmed ? "Confirmed by the observed stream." : "The playing source has not been confirmed."}</small></div>
      <output>{provider(context.playback?.sourceConfirmed ? context.playback.providerId : context.song.catalogProviderId)}</output></div>
  {:else}<p class="settings-impact">No song is attached. Describe the app, action and problem in your note.</p>{/if}
  <div class="settings-row"><div><strong>Music app</strong><small>{context.subjectName ? `Observed listener: ${context.subjectName}` : "Only an observed music client is included."}</small></div><output>{context.playback?.clientName ?? "Not captured"}</output></div>
  {#if context.match}<div class="settings-row"><div><strong>Saved match</strong><small>{humanize(context.match.authorityScope)} choice{context.match.authorityScope === "automatic" && context.match.decisionVersion != null ? ` · version ${context.match.decisionVersion}` : ""}</small></div>
    <output>{context.match.state ? humanize(context.match.state) : "Not captured"}{context.match.authorityScope === "automatic" && context.match.confidence != null && Number.isFinite(context.match.confidence) ? ` · ${Math.round(context.match.confidence * 100)}%` : ""}</output></div>{/if}
  <details class="settings-disclosure">
    <summary>Included diagnostics</summary>
    {#if context.playback}
      <div class="settings-row"><div><strong>Playback route</strong><small>{context.playback.selectionReason ? humanize(context.playback.selectionReason) : "Route reason not captured"}</small></div><output>{context.playback.seekRung ? humanize(context.playback.seekRung) : "Seeking details not captured"}</output></div>
    {/if}
    {#if context.match?.reasonCodes.length}<p class="settings-impact">Match reasons: {context.match.reasonCodes.map(humanize).join(" · ")}</p>{/if}
    {#each context.checks as check}
      <div class="settings-row"><div><strong>{provider(check.providerId)} · {humanize(check.capability)}</strong><small>{supportDate(check.checkedAt)}</small></div><output>{humanize(check.state)}{check.reasonCode ? ` · ${humanize(check.reasonCode)}` : ""}</output></div>
    {:else}<p class="settings-impact">No recent source checks were captured.</p>{/each}
  </details>
  {#if review && (context.song?.externalSnapshotId || context.song?.canonicalRecordingId && onReviewLyrics)}
    <footer class="panel-heading-actions">
      {#if context.song?.externalSnapshotId}
        {#if onReviewMatch}<Button variant="secondary" disabled={matchLoading} onclick={(event) => onReviewMatch?.(event.currentTarget)}>{matchLoading ? "Loading match…" : "Review match"}</Button>
        {:else}<Button variant="secondary" href={`#/library/mappings?review=${encodeURIComponent(context.song.externalSnapshotId)}`}>Review match</Button>{/if}
      {/if}
      {#if context.song?.canonicalRecordingId && onReviewLyrics}<Button variant="secondary" onclick={(event) => onReviewLyrics?.(event.currentTarget)}>Review lyrics</Button>{/if}
    </footer>
  {/if}
  {#if review && matchReviewNote}<p class="settings-impact">{matchReviewNote}</p>{/if}
</article>
