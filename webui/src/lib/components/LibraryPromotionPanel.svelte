<script lang="ts">
  import { formatLocalDateTime } from "$lib/dates";
  import type { Snippet } from "svelte";
  import SidePanel from "./patterns/SidePanel.svelte";
  import ItemRow from "./patterns/ItemRow.svelte";
  import StatePill from "./patterns/StatePill.svelte";
  import RowsSkeleton from "./patterns/RowsSkeleton.svelte";
  import MediaArtwork from "./MediaArtwork.svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";
  import EmptyState from "./patterns/EmptyState.svelte";
  import { promotionSetupHint } from "$lib/promotion-setup";
  import { Button } from "./ui/button";
  import type { LibraryRequest, PromotionPreview, PromotionRun } from "$lib/library-promotion-api";
  import type { ProviderDefinition } from "$lib/api";
  import { canCancelPromotion, promotionRequestStatus, requestLibraryFact, frequency, promotionArtwork, promotionProgress, promotionTimeline, qualityWording, songStatus, sourceLabel, sourceName, sourceQuality } from "$lib/library-promotion-presentation";

  let { open = $bindable(false), preview, run, title: fallbackTitle = "Add to library", description: fallbackDescription = "", cover, loading = false, error = "", busy = false,
    requestReceipt, administrator = false, setupRequired = false, onapprove, ondecline, oncancelrequest, onstart, oncancel, onreview, onPrevious, onNext, onclose, providers = [], copyActions }: {
    open?: boolean; preview?: PromotionPreview | null; run?: PromotionRun | null; title?: string; description?: string; cover?: string | null;
    loading?: boolean; error?: string; busy?: boolean; setupRequired?: boolean;
    requestReceipt?: LibraryRequest | null; administrator?: boolean;
    onapprove?: () => Promise<void>; ondecline?: (note?: string) => Promise<void>; oncancelrequest?: () => Promise<void>;
    onstart?: (unit: "album" | "song") => void;
    oncancel?: () => Promise<void>;
    onreview?: (recordingId: string, action: "try-next-source" | "add-anyway" | "skip", reason?: string) => Promise<void>;
    onPrevious?: () => void; onNext?: () => void; onclose?: () => void;
    providers?: ProviderDefinition[]; copyActions?: Snippet;
  } = $props();
  let selectedTrack = $state<string | null>(null);
  let albumOpen = $state(false);
  let cancelOpen = $state(false);
  let declineOpen = $state(false);
  let declineNote = $state("");
  let reasonOpen = $state(false);
  let reason = $state("");
  let actionError = $state("");
  let actionBusy = $state(false);
  const reviewTrack = $derived(!albumOpen ? run?.tracks.find(track => selectedTrack ? track.recordingId === selectedTrack && track.outcome === "Needs review" : track.outcome === "Needs review") : undefined);
  const album = $derived(preview?.album);
  const simpleRequest = $derived(preview?.canAddDirectly === false && !(administrator && requestReceipt));
  const progress = $derived(run ? promotionProgress(run) : null);
  const title = $derived(reviewTrack?.song.title ?? requestReceipt?.title ?? album?.title ?? (albumOpen ? run?.album?.title ?? run?.title : run?.title) ?? fallbackTitle);
  const description = $derived(reviewTrack ? `${reviewTrack.song.artist} · ${reviewTrack.song.album}${reviewTrack.song.track ? ` · track ${reviewTrack.song.track}` : ""}`
    : requestReceipt ? [requestReceipt.artist, requestReceipt.year, `${requestReceipt.songCount} ${requestReceipt.songCount === 1 ? "song" : "songs"}`].filter(Boolean).join(" · ")
    : album ? [album.artist, album.year, `${album.songCount} ${album.songCount === 1 ? "song" : "songs"}`].filter(Boolean).join(" · ")
    : albumOpen && run?.album ? [run.album.artist, run.album.year, run.unit === "album" ? `${run.counts.total} ${run.counts.total === 1 ? "song" : "songs"}` : ""].filter(Boolean).join(" · ")
    : run ? `${run.artist} · ${run.state === "added" ? "added" : run.outcome === "Cancelled" ? "cancelled" : "adding"} ${progress?.total ?? run.counts.total} ${(progress?.total ?? run.counts.total) === 1 ? "song" : "songs"}${run.startedAt ? ` · started ${time(run.startedAt)}` : ""}` : fallbackDescription);
  const coverUrl = $derived(promotionArtwork(reviewTrack?.song.coverArtUrl ?? requestReceipt?.coverReference ?? album?.coverReference ?? run?.coverReference ?? cover));
  const quality = $derived(reviewTrack?.qualityCheck);
  const wording = $derived(qualityWording(quality, reviewTrack?.reason));
  const cutoff = $derived(quality?.effectiveBandwidthHz != null && quality.effectiveBandwidthHz > 0 ? quality.effectiveBandwidthHz : null);
  const bands = $derived(quality?.energyByFrequency);
  const runIdentity = $derived(run?.id ?? requestReceipt?.id);
  $effect(() => { runIdentity; selectedTrack = null; albumOpen = false; });
  function time(value: string) { return new Date(value).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }); }
  function copyReason(value?: string) {
    const best = preview?.bestCopies.find(source => source.verdict === "meets-your-floor");
    return best && value ? value.replace(`if ${best.provider} fails`, `if ${sourceName(best.provider, providers)} fails`) : value;
  }
  async function act(action: "try-next-source" | "add-anyway" | "skip") {
    if (!reviewTrack || actionBusy || !onreview) return;
    actionError = ""; actionBusy = true;
    try { await onreview(reviewTrack.recordingId, action, action === "add-anyway" ? reason.trim() : undefined); reasonOpen = false; reason = ""; }
    catch (cause) { actionError = cause instanceof Error ? cause.message : "This song could not be updated. Try again."; }
    finally { actionBusy = false; }
  }
  async function requestAction(action: "approve" | "decline" | "cancel") {
    if (actionBusy) return;
    actionError = ""; actionBusy = true;
    try {
      if (action === "approve") await onapprove?.();
      else if (action === "decline") { await ondecline?.(declineNote.trim() || undefined); declineOpen = false; declineNote = ""; }
      else await oncancelrequest?.();
    } catch (cause) { actionError = cause instanceof Error ? cause.message : "This request could not be updated. Try again."; }
    finally { actionBusy = false; }
  }
  async function cancel() {
    if (actionBusy || !oncancel) return;
    actionError = ""; actionBusy = true;
    try { await oncancel(); cancelOpen = false; }
    catch (cause) { actionError = cause instanceof Error ? cause.message : "This run could not be cancelled. Try again."; }
    finally { actionBusy = false; }
  }
</script>

{#snippet albumArtwork()}<MediaArtwork url={coverUrl} loading="eager" />{/snippet}
<SidePanel bind:open {title} {description} {onPrevious} {onNext} {onclose} artwork={simpleRequest ? undefined : albumArtwork} inlineFooter={simpleRequest} width={380} minimumContentWidth={560} wrapTitle navigationLabel={run ? "run" : "song"}>
  <div class="promotion-details" class:request-details={simpleRequest}>
    {#if setupRequired}<EmptyState title={promotionSetupHint} description="Choose where added songs go in library settings. Allstarr needs write access to that folder.">
      {#snippet action()}{#if administrator}<Button variant="outline" href="#/settings/adding-to-library">Open library settings</Button>{:else}<p class="request-context">Ask an administrator to set it up.</p>{/if}{/snippet}
    </EmptyState>{/if}
    {#if loading}<RowsSkeleton count={5} />{/if}
    {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
    {#if actionError && !reasonOpen && !cancelOpen && !declineOpen}<p class="notice-error" role="alert">{actionError}</p>{/if}
    {#if requestReceipt}
      <p class="request-context">{#if administrator}Requested by {requestReceipt.requester?.displayName || "a listener"} · <time datetime={requestReceipt.createdAt}>{formatLocalDateTime(requestReceipt.createdAt)}</time>{:else}{promotionRequestStatus(requestReceipt.state).text}{/if}</p>
      {#if requestReceipt.declineNote}<p class="request-context">{requestReceipt.declineNote}</p>{/if}
    {/if}
    {#if preview && album && (preview.canAddDirectly !== false || administrator && requestReceipt)}
      <div class="promotion-banner info"><span><b>{requestReceipt ? "Adds the album using the requester’s profile." : "Adds this song using your folder settings."}</b> {requestReceipt ? "" : "If you choose the whole album, "}{preview.alreadyInLibrary} {preview.alreadyInLibrary === 1 ? "song is" : "songs are"} already there; {album.songCount - preview.alreadyInLibrary} will be added from the best copy your download sources can get.</span></div>
      {#if preview.bestCopies.length}
        <section><h3 class="sec-label">Best copy</h3><div class="promotion-steps">
          {#each preview.bestCopies as source, index (`${index}:${source.provider}`)}
            <div class="promotion-step"><div><b>{sourceLabel(source, providers, source.verdict === "meets-your-floor")}</b>{#if source.reason}<small>{copyReason(source.reason)}</small>{/if}</div>
              {#if source.verdict === "meets-your-floor"}<StatePill filled compact tone="ok" text="Meets your floor" />{:else if source.verdict === "fallback"}<span class="muted">Fallback</span>{:else}<StatePill filled compact text="Skipped" />{/if}
            </div>
          {/each}
        </div></section>
      {/if}
      {#if preview.destinationPath}<section><h3 class="sec-label">Where it goes</h3><div class="promotion-path">{preview.destinationPath}</div></section>{/if}
      {#if preview.profileSteps.length}<section><h3 class="sec-label">What happens · {requestReceipt ? "requester’s" : "your"} profile</h3><div class="promotion-steps">
        {#each preview.profileSteps as step (step.key)}<div class="promotion-step"><div><b>{step.title}</b><small>{step.description}</small></div><span class:on={step.enabled} class="profile-enabled">{step.enabled ? "✓ On" : "Off"}</span></div>{/each}
      </div></section>{/if}
    {:else if preview && album}
      <div class="promotion-banner request-note">An admin approves requests. You'll see it in your library when it's added.</div>
      <div class="promotion-steps" aria-label="Album request facts">
        <div class="promotion-step"><div><b>Best copy found</b><small>{preview.bestCopies[0] ? sourceLabel(preview.bestCopies[0], providers) : "No source meets your quality floor yet"}</small></div>{#if preview.bestCopies[0]?.verdict === "meets-your-floor"}<StatePill filled compact tone="ok" text={/^(flac|alac|wavpack|ape|pcm_)/i.test(preview.bestCopies[0].codec ?? "") ? "Lossless" : "Meets your floor"} />{/if}</div>
        <div class="promotion-step"><div><b>Already in the library</b><small>{requestLibraryFact(preview.alreadyInLibrary, album.songCount)}</small></div></div>
      </div>
    {:else if reviewTrack && run}
      <div class="promotion-banner warn"><span><b>{wording.title}</b> {wording.detail}</span></div>
      {#if bands?.some(value => value !== null)}
        <div class="quality-bands" role="img" aria-label="Measured energy by frequency, 0 to 22 kHz">
          {#each bands as value}<i class:unmeasured={value === null} style:height={value === null ? "0%" : `${Math.max(0, Math.min(1, value)) * 100}%`}></i>{/each}
        </div><p class="quality-caption">Energy by frequency, 0–22 kHz.{cutoff ? ` Sound stops at about ${frequency(cutoff)} kHz.` : ""}</p>
      {:else if cutoff}
        <div class="quality-cutoff" role="img" aria-label={`Measured sound cut-off: ${frequency(cutoff)} kHz on a 0 to 22 kHz scale`}><i style:width={`${Math.min(100, cutoff / 22000 * 100)}%`}></i></div>
        <p class="quality-caption">Measured sound cut-off: {frequency(cutoff)} kHz · 0–22 kHz</p>
      {/if}
      <dl class="promotion-facts">
        {#if reviewTrack.provider}<dt>From</dt><dd>{sourceName(reviewTrack.provider, providers)}{sourceQuality({ codec: reviewTrack.song.codec, bitDepth: quality?.claimedBitDepth ?? reviewTrack.song.bitDepth, sampleRate: quality?.claimedSampleRate ?? reviewTrack.song.sampleRate }) ? ` · ${sourceQuality({ codec: reviewTrack.song.codec, bitDepth: quality?.claimedBitDepth ?? reviewTrack.song.bitDepth, sampleRate: quality?.claimedSampleRate ?? reviewTrack.song.sampleRate })}${quality ? " (claimed)" : ""}` : ""}</dd>{/if}
        <dt>Rest of album</dt><dd>{run.counts.added} of {run.counts.total} songs passed and were added</dd>
      </dl>
      <section><h3 class="sec-label">What next</h3><div class="promotion-steps">
        {#if reviewTrack.nextSource}<div class="promotion-step"><div><b>Try the next source</b><small>{sourceLabel(reviewTrack.nextSource, providers)}</small></div><Button size="sm" class="rounded-full" variant="secondary" disabled={busy || actionBusy} onclick={() => act("try-next-source")}>Try {sourceName(reviewTrack.nextSource.provider, providers)}</Button></div>{/if}
        {#if quality && reviewTrack.step === "Quality" && !["standard", "true-hi-res"].includes(quality.verdict)}<div class="promotion-step"><div><b>Add it anyway</b><small>Marked as lossy in its tags and in Allstarr</small></div><Button size="sm" class="rounded-full" variant="outline" disabled={busy || actionBusy} onclick={() => { actionError = ""; reasonOpen = true; }}>Add anyway</Button></div>{/if}
        <div class="promotion-step"><div><b>Skip this song</b><small>The album stays without it; you can add it later</small></div><Button size="sm" class="rounded-full" variant="text" disabled={busy || actionBusy} onclick={() => act("skip")}>Skip</Button></div>
      </div></section>
    {:else if run}
      {#if progress && !albumOpen}<section><div class="progress-heading"><h3 class="sec-label">Progress</h3><span>{progress.label}</span></div><div class="promotion-progress" role="progressbar" aria-label="Songs added to the library" aria-valuemin={0} aria-valuemax={progress.total} aria-valuenow={progress.added} aria-valuetext={progress.label}><i style:width={`${progress.percent}%`}></i></div></section>
        <ol class="promotion-timeline">{#each promotionTimeline(run, providers) as step}<li data-tone={step.tone} data-state={step.state}><b>{step.title}</b>{#if step.detail}<small>{step.detail}</small>{/if}</li>{/each}</ol>
      {/if}
      <section><h3 class="sec-label">{albumOpen ? run.unit === "song" ? "Songs in this run" : "Album songs" : "Songs"}</h3><div class="promotion-songs">
        {#each run.tracks as track (track.recordingId)}{@const status = songStatus(track, run.outcome === "Cancelled", run.outcome === "Queued")}
          <ItemRow title={track.song.title} subtitle={`${track.lossy ? "Lossy copy · " : ""}${albumOpen ? track.destinationPath ?? track.song.artist : track.reason}`} onclick={track.outcome === "Needs review" ? () => { albumOpen = false; selectedTrack = track.recordingId; } : undefined}>
            {#snippet leading()}<span class="track-number">{track.song.track ? String(track.song.track).padStart(2, "0") : ""}</span>{/snippet}
            {#snippet pill()}<StatePill filled compact tone={status.tone} text={status.text} />{/snippet}
          </ItemRow>
        {/each}
      </div></section>
    {/if}
    {@render copyActions?.()}
  </div>
  {#snippet footer()}
    <div class="promotion-footer" class:request-footer={simpleRequest}>
      {#if requestReceipt}
        {#if requestReceipt.state === "pending" && administrator}<Button size="sm" class="rounded-full" variant="text" disabled={busy || actionBusy} onclick={() => { actionError = ""; declineOpen = true; }}>Decline…</Button><Button size="sm" class="rounded-full" disabled={busy || actionBusy || loading || !preview || Boolean(error)} onclick={() => requestAction("approve")}>Approve and add</Button>
        {:else if requestReceipt.state === "pending"}<Button size="sm" class="rounded-full" variant="text" disabled={busy || actionBusy} onclick={() => requestAction("cancel")}>Cancel request</Button>{/if}
      {:else if preview?.canAddDirectly === false}<Button size="sm" class="rounded-full" variant="text" disabled={busy} onclick={() => open = false}>Cancel</Button><Button size="sm" class="rounded-full" disabled={busy || loading} onclick={() => onstart?.("album")}>Request album</Button>
      {:else if preview}<Button size="sm" class="rounded-full" variant="text" disabled={busy || loading} onclick={() => onstart?.("album")}>Add album to library</Button><Button size="sm" class="rounded-full" disabled={busy || loading} onclick={() => onstart?.("song")}>Only this song</Button>
      {:else if run}{#if !reviewTrack && !albumOpen && canCancelPromotion(run)}<Button size="sm" variant="text" class="promotion-cancel rounded-full" disabled={busy || actionBusy} onclick={() => { actionError = ""; cancelOpen = true; }}>Cancel…</Button>{:else}<span></span>{/if}<Button size="sm" class="rounded-full" variant="outline" onclick={() => { albumOpen = !albumOpen; selectedTrack = null; }}>{albumOpen ? "Back to progress" : "Open the album"}</Button>{/if}
    </div>
  {/snippet}
</SidePanel>
<ConfirmDialog bind:open={declineOpen} title="Decline this request?" description="You can leave a short note for the listener." confirmLabel="Decline request" closeOnConfirm={false} disabled={actionBusy} error={actionError} onConfirm={() => requestAction("decline")}>
  <label class="reason-field">Note (optional)<textarea bind:value={declineNote} maxlength={500} rows={3}></textarea></label>
</ConfirmDialog>
<ConfirmDialog bind:open={cancelOpen} title="Cancel adding to the library?" description="Songs already in your library stay there. Remaining songs in this run will stop being added." confirmLabel="Cancel adding" cancelLabel="Keep adding" closeOnConfirm={false} disabled={actionBusy} error={actionError} onConfirm={cancel} />
<ConfirmDialog bind:open={reasonOpen} title="Add this copy anyway?" description="Explain why you want to keep this copy. It will be marked as lossy in its tags and in Allstarr." confirmLabel="Add anyway" confirmVariant="default" closeOnConfirm={false} disabled={actionBusy || !reason.trim()} error={actionError} onConfirm={() => act("add-anyway")}>
  <label class="reason-field">Reason<textarea bind:value={reason} maxlength={500} rows={3} placeholder="Why is this copy acceptable?"></textarea></label>
</ConfirmDialog>

<style>
  .promotion-details { display: grid; gap: 12px; }
  .request-details { gap: 20px; }
  .request-details .promotion-step b { font-weight: 600; }
  .promotion-details section { min-width: 0; }
  .promotion-details .sec-label { display: block; margin: 0 0 12px; }
  .promotion-banner { border-radius: 12px; padding: 12px 14px; font-size: .8rem; line-height: 1.5; }
  .promotion-banner.info { background: var(--md-sys-color-primary-container); color: var(--md-sys-color-on-primary-container); }
  .promotion-banner.info b { color: var(--info); }
  .request-context { margin: 0; color: var(--color-ink-muted); font-size: .8rem; line-height: 1.5; }
  .promotion-banner.request-note { background: var(--md-sys-color-primary-container); color: var(--md-sys-color-on-primary-container); }
  .promotion-banner.warn { background: var(--warning-container); color: var(--color-ink); }
  .promotion-banner.warn b { color: var(--warning); }
  .promotion-banner b { font-weight: 600; }
  .promotion-steps, .promotion-songs { overflow: hidden; border: 1px solid var(--color-edge); border-radius: 12px; }
  .promotion-step { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 10px; border-bottom: 1px solid var(--color-edge); padding: 8px 12px; font-size: .8rem; }
  .promotion-step:last-child { border-bottom: 0; }
  .promotion-step b { display: block; font-weight: 500; }
  .promotion-step small { display: block; color: var(--color-ink-muted); font-size: .74rem; line-height: 1.5; }
  .muted, .profile-enabled { color: var(--color-ink-muted); font-size: .74rem; white-space: nowrap; }
  .profile-enabled.on { color: var(--success); }
  .promotion-path { border: 1px solid var(--color-edge); border-radius: 10px; padding: 9px 11px; background: var(--color-panel-raised); overflow-wrap: anywhere; font: 500 .78rem/1.45 ui-monospace, SFMono-Regular, Menlo, monospace; }
  .promotion-footer { display: flex; width: 100%; min-width: 0; align-items: center; justify-content: space-between; gap: 8px; }
  .promotion-footer.request-footer { justify-content: flex-end; }
  .promotion-footer :global(.promotion-cancel) { color: var(--danger); }
  .progress-heading { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 6px; }
  .progress-heading .sec-label { margin: 0; }
  .progress-heading > span { color: var(--color-ink-muted); font-size: .76rem; }
  .promotion-progress, .quality-cutoff { height: 8px; overflow: hidden; border-radius: 999px; background: var(--color-panel-raised); }
  .promotion-progress i, .quality-cutoff i { display: block; height: 100%; border-radius: inherit; background: var(--color-signal); }
  .promotion-timeline { display: grid; gap: 10px; margin: 2px 0 0 5px; border-left: 2px solid var(--color-edge); padding: 2px 0 2px 16px; list-style: none; }
  .promotion-timeline li { position: relative; font-size: .8rem; }
  .promotion-timeline li::before { position: absolute; top: 3px; left: -23px; width: 10px; height: 10px; border-radius: 50%; background: var(--color-edge-strong); box-shadow: 0 0 0 3px var(--color-panel); content: ""; }
  .promotion-timeline [data-state="complete"]::before { background: var(--success); }
  .promotion-timeline [data-state="current"]::before { background: var(--info); }
  .promotion-timeline b { display: block; font-weight: 500; }
  .promotion-timeline small { display: block; color: var(--color-ink-muted); font-size: .74rem; line-height: 1.5; overflow-wrap: anywhere; }
  .track-number { color: var(--color-ink-muted); font-size: .76rem; font-variant-numeric: tabular-nums; }
  .quality-bands { display: grid; grid-auto-flow: column; grid-auto-columns: minmax(0, 1fr); align-items: end; gap: 2px; height: 70px; border-radius: 10px; padding: 8px; background: var(--color-panel-raised); }
  .quality-bands i { display: block; border-radius: 2px 2px 0 0; background: var(--color-signal); }
  .quality-cutoff { height: 20px; margin-top: 4px; }
  .quality-caption { margin: 0; color: var(--color-ink-muted); font-size: .74rem; }
  .promotion-facts { display: grid; grid-template-columns: 85px minmax(0, 1fr); gap: 8px 10px; margin: 12px 0; font-size: .8rem; }
  dt { color: var(--color-ink-muted); } dd { margin: 0; overflow-wrap: anywhere; }
  .reason-field { display: grid; gap: 8px; margin-top: 16px; color: var(--color-ink); font-size: .875rem; }
  textarea { width: 100%; min-height: 88px; border: 1px solid var(--color-edge-strong); border-radius: 8px; padding: 10px; background: var(--color-panel); color: var(--color-ink); }
  @media (max-width: 899px) { .promotion-step { gap: 8px; padding-inline: 10px; } .promotion-step :global([data-slot="button"]) { min-height: 44px; max-width: 125px; height: auto; white-space: normal; } }
</style>
