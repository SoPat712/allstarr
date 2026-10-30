<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, home, matchReview, playTraces, type MatchAuthorityScope, type MatchReviewItem, type PlayTrace, type PlayTraceChoice, type ProviderDefinition } from "$lib/api";
  import { expectedAuthorityForScope } from "$lib/matching-api";
  import { dayLabel } from "$lib/activity";
  import { playDuration, playOutcome, playPhase, playSource, playStart, playComparison, playActionError } from "$lib/play-traces";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import ActivityOutcome from "$lib/components/ActivityOutcome.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import RouteError from "$lib/components/RouteError.svelte";

  let { administrator = false, scope = "mine", currentUserId = "" }: { administrator?: boolean; scope?: "mine" | "everyone"; currentUserId?: string } = $props();
  let plays = $state<PlayTrace[]>([]);
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let cursor = $state("");
  let hasMore = $state(false);
  let expanded = $state("");
  let detail = $state<PlayTrace | null>(null);
  let detailBusy = $state(false);
  let detailError = $state("");
  let copying = $state(false);
  let backend = $state("Local library");
  let authorityScope = $state<MatchAuthorityScope>("personal");
  let actionBusy = $state(false);
  let matchLoading = $state(false);
  let matchOpen = $state(false);
  let selectedMatch = $state<MatchReviewItem | null>(null);
  let forgetOpen = $state(false);
  let reportOpen = $state(false);
  let reportNote = $state("");
  let reportRequestId = "";
  let disposed = false;
  let detailRequest = 0;
  let refreshPending = false;
  const days = $derived.by(() => {
    const result: { label: string; plays: PlayTrace[] }[] = [];
    for (const play of plays) {
      const label = dayLabel(play.startedAt);
      let day = result.at(-1);
      if (day?.label !== label) result.push(day = { label, plays: [] });
      day.plays.push(play);
    }
    return result;
  });
  const steps = $derived([...(detail?.timing.phases ?? [])].sort((a, b) => a.startedMs - b.startedMs));
  function time(value: string) {
    return Number.isNaN(Date.parse(value)) ? "Time not recorded" : new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }
  async function load(older = false) {
    if (busy) { if (!older) refreshPending = true; return; }
    busy = true; error = "";
    try {
      const visible: PlayTrace[] = [];
      let before = older ? cursor : "", more = false;
      do {
        const page = await playTraces.list(before);
        if (disposed) return;
        visible.push(...page.filter(play => administrator && scope === "everyone" || play.listenerId === currentUserId));
        const next = page.at(-1)?.id ?? "";
        more = page.length === 50 && next !== before;
        before = next;
      } while (more && visible.length < 50);
      const merged = new Map(plays.map(play => [play.id, play]));
      for (const play of visible) merged.set(play.id, play);
      plays = [...merged.values()].sort((a, b) => b.startedAt.localeCompare(a.startedAt) || b.id.localeCompare(a.id));
      if (older || !cursor) { cursor = before; hasMore = more; }
      if (expanded) await loadDetail(expanded, true);
    } catch { if (!disposed) error = "Plays could not be loaded. Try again."; }
    finally {
      if (!disposed) {
        loading = false; busy = false;
        if (refreshPending) { refreshPending = false; scheduler.schedule(); }
      }
    }
  }
  const scheduler = createRefreshScheduler(() => load());
  async function loadDetail(id: string, passive = false) {
    const request = ++detailRequest;
    if (!passive) { detailBusy = true; detailError = ""; }
    try {
      const next = await playTraces.detail(id);
      if (!disposed && request === detailRequest && expanded === id) { detail = next; detailError = ""; }
    } catch { if (!disposed && request === detailRequest && expanded === id) detailError = "Start steps could not be loaded. Try again."; }
    finally { if (!disposed && request === detailRequest) detailBusy = false; }
  }
  function toggle(play: PlayTrace) {
    detailRequest++;
    reportOpen = false; forgetOpen = false; matchOpen = false; authorityScope = "personal";
    if (expanded === play.id) { expanded = ""; return; }
    expanded = play.id; detail = null; detailError = "";
    void loadDetail(play.id);
  }
  function authority(match: MatchReviewItem) {
    const scope = administrator ? authorityScope : "personal";
    return { authorityScope: scope, expectedAuthority: expectedAuthorityForScope(match, scope) };
  }
  async function latestMatch(play: PlayTrace) {
    const snapshot = play.resolution.externalSnapshotId;
    if (!snapshot) throw new Error();
    const match = await matchReview.get(snapshot);
    if (!match || match.externalSnapshotId !== snapshot) throw new Error();
    return match;
  }
  async function actionFailed(cause: unknown, play: PlayTrace) {
    if (disposed) return;
    const code = cause instanceof ApiError ? (cause.details as { code?: string } | null)?.code : null;
    if (cause instanceof ApiError && cause.status === 409 && (code === "match_changed" || code === "match_unavailable")) {
      matchOpen = false;
      toast.error("This song's match changed. Showing the latest.");
      await Promise.all([loadDetail(play.id, true), latestMatch(play).then(match => {
        if (!disposed && expanded === play.id) selectedMatch = match;
      }).catch(() => {})]);
    } else toast.error(playActionError(cause));
  }
  async function chooseVersion(choice: PlayTraceChoice) {
    if (!detail || !selectedMatch || selectedMatch.externalSnapshotId !== detail.resolution.externalSnapshotId || actionBusy) throw new Error("That didn't work. Try again.");
    const play = detail;
    actionBusy = true;
    try {
      await playTraces.version(play.id, { ...choice, ...authority(selectedMatch) });
      toast.success("Saved. It will play this version next time.");
      scheduler.schedule();
    } catch (cause) {
      await actionFailed(cause, play);
      throw new Error(playActionError(cause));
    } finally { actionBusy = false; }
  }
  async function reviewMatch() {
    if (!detail?.resolution.externalSnapshotId || matchLoading) return;
    const play = detail, id = play.id;
    matchLoading = true;
    try {
      const match = await latestMatch(play);
      if (disposed || expanded !== id) return;
      selectedMatch = match; matchOpen = true;
    } catch (cause) { if (!disposed && expanded === id) toast.error(playActionError(cause)); }
    finally { matchLoading = false; }
  }
  async function forget() {
    if (!detail || actionBusy) return;
    const play = detail;
    actionBusy = true;
    try {
      const match = await latestMatch(play);
      await playTraces.forget(play.id, authority(match));
      toast.success("Done. Allstarr will look again next time.");
      scheduler.schedule();
    } catch (cause) { await actionFailed(cause, play); }
    finally { forgetOpen = false; actionBusy = false; }
  }
  function openReport() {
    if (reportOpen) return;
    reportNote = ""; reportRequestId = crypto.randomUUID(); reportOpen = true;
  }
  async function report(event: SubmitEvent) {
    event.preventDefault();
    if (!detail || actionBusy) return;
    const id = detail.id;
    actionBusy = true;
    try {
      await playTraces.report(id, { requestId: reportRequestId, note: reportNote });
      if (!disposed) {
        if (expanded === id) reportOpen = false;
        toast.success(administrator ? "Reported. It's in Problems." : "Reported. Your admin will see it in Problems.");
      }
    } catch (cause) { if (!disposed) toast.error(playActionError(cause)); }
    finally { actionBusy = false; }
  }
  async function copy(id: string) {
    if (copying) return;
    copying = true;
    try {
      const text = await playTraces.diagnostics(id);
      try {
        if (!navigator.clipboard?.writeText) throw new Error();
        await navigator.clipboard.writeText(text);
      } catch {
        const focus = document.activeElement;
        const field = document.createElement("textarea");
        field.value = text;
        field.setAttribute("aria-hidden", "true");
        field.style.cssText = "position:fixed;left:-9999px;opacity:0";
        document.body.appendChild(field);
        try {
          field.select();
          if (!document.execCommand("copy")) throw new Error();
        } finally {
          field.remove();
          if (focus instanceof HTMLElement) focus.focus({ preventScroll: true });
        }
      }
      toast.success("Copied — paste it to your admin.");
    } catch { toast.error("Diagnostics could not be copied. Try again."); }
    finally { copying = false; }
  }
  onMount(() => {
    void home.schema().then(next => { if (!disposed) { providers = next.providers; backend = next.activeBackend ?? "Local library"; } }).catch(() => {});
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; detailRequest++; unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="activity-plays" aria-label="Plays" aria-busy={busy}>
  <div class="plays-toolbar"><RefreshButton variant="secondary" label="Refresh plays" onrefresh={() => load()} /></div>
  {#if loading}
    <Skeleton class="skeleton-panel" aria-label="Loading plays" />
  {:else if error && !plays.length}
    <RouteError eyebrow="Plays unavailable" title="Play history is unavailable." message={error} onRetry={() => load()} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
    {#each days as day (day.label)}
      <section class="event-log-day" aria-label={day.label}>
        <h2>{day.label}</h2>
        <div class="event-log-list">
          {#each day.plays as play (play.id)}
            {@const outcome = playOutcome(play.outcome)}
            <article class="play-row">
              <button type="button" class="play-toggle" aria-expanded={expanded === play.id} aria-controls={`play-${play.id}`} onclick={() => toggle(play)}>
                <MediaArtwork url={play.request.presented.artwork === "available" ? null : play.request.presented.artwork} />
                <span class="play-copy"><strong>{play.request.presented.title || "Unknown song"}</strong><small>{play.request.presented.artist || "Unknown artist"}</small><small>{playStart(play.timing.firstByteMs)} · {playSource(play.resolution.provider, play.resolution.cached, providers)}</small></span>
                <span class="play-end"><ActivityOutcome state={outcome.state} label={outcome.label} /><time datetime={play.startedAt}>{time(play.startedAt)}</time></span>
              </button>
              {#if expanded === play.id}
                <div class="play-detail" id={`play-${play.id}`} aria-busy={detailBusy}>
                  {#if detailBusy && !detail}<p role="status">Loading start steps…</p>{/if}
                  {#if detailError}<p class="notice-error" role="alert">{detailError}</p><Button variant="secondary" onclick={() => void loadDetail(play.id)}>Retry start steps</Button>{/if}
                  {#if detail}
                    <div class="play-comparison" role="region" aria-label="What played">
                      {#each playComparison(detail, providers) as row}
                        <p><strong>{row.label}:</strong> {#each row.fields as field, index}{#if index}{" · "}{/if}<span class:play-difference={field.different}>{field.text}</span>{/each}</p>
                      {/each}
                    </div>
                    {#if detail.resolution.externalSnapshotId}
                      {#if administrator}
                        <div class="play-scope" inert={actionBusy || matchLoading}>
                          <span>Change it for</span>
                          <div class="play-audience" role="group" aria-label="Change it for">
                            <button type="button" class="filter-chip" aria-pressed={authorityScope === "personal"} onclick={() => authorityScope = "personal"}>Just me</button>
                            <button type="button" class="filter-chip" aria-pressed={authorityScope === "household"} onclick={() => authorityScope = "household"}>Everyone</button>
                          </div>
                        </div>
                      {/if}
                      <div class="play-actions">
                        <Button variant="secondary" size="sm" disabled={actionBusy || matchLoading} onclick={() => void reviewMatch()}>Wrong song?</Button>
                        <Button variant="secondary" size="sm" disabled={actionBusy} onclick={() => forgetOpen = true}>Look for it again</Button>
                        <Button variant="secondary" size="sm" disabled={actionBusy} aria-expanded={reportOpen} onclick={openReport}>Report a problem</Button>
                      </div>
                      {#if reportOpen}
                        <form class="play-report" aria-label="Report a problem" onsubmit={(event) => void report(event)}>
                          <label class="field"><span>Note (optional)</span><textarea bind:value={reportNote} maxlength="500" rows="3" disabled={actionBusy}></textarea></label>
                          {#if reportNote.length >= 450}<small>{reportNote.length}/500</small>{/if}
                          <div class="play-actions">
                            <Button variant="secondary" size="sm" type="submit" disabled={actionBusy}>Send report</Button>
                            <Button variant="secondary" size="sm" disabled={actionBusy} onclick={() => reportOpen = false}>Cancel</Button>
                          </div>
                        </form>
                      {/if}
                    {:else}
                      <p>This played from your library, so there is nothing to change here.</p>
                    {/if}
                    <div class="play-start">
                      <p><strong>How it started</strong></p>
                      <ol aria-label="Start steps" role="list">
                        {#each steps as step}<li><strong>{playPhase(step.phase)}{step.provider ? ` · ${playSource(step.provider, false, providers)}` : ""}</strong><span>{playDuration(step.elapsedMs)}</span></li>{:else}<li>Start steps were not recorded.</li>{/each}
                      </ol>
                    </div>
                    {#if detail.timing.omittedPhases}<p>Some start steps were not retained.</p>{/if}
                  {/if}
                  <Button variant="secondary" disabled={copying} onclick={() => void copy(play.id)}>{copying ? "Copying…" : "Copy diagnostics"}</Button>
                </div>
              {/if}
            </article>
          {/each}
        </div>
      </section>
    {:else}
      <div class="compact-empty"><p>Play something and it will show up here.</p></div>
    {/each}
    {#if hasMore}<div class="plays-toolbar"><Button variant="secondary" disabled={busy} onclick={() => void load(true)}>Load earlier plays</Button></div>{/if}
  {/if}
</section>

<MatchDialog bind:open={matchOpen} match={selectedMatch} {providers} {backend} showReject={false} onChoose={chooseVersion} onSaved={() => {}} />
<ConfirmDialog bind:open={forgetOpen} title="Look for this song again?" description="Allstarr will forget this choice and pick the best version next time it plays." confirmLabel="Look for it again" confirmVariant="secondary" disabled={actionBusy} closeOnConfirm={false} onConfirm={forget} />

<style>
  .activity-plays { display: grid; gap: var(--section-gap); min-width: 0; }
  .plays-toolbar { display: flex; justify-content: flex-end; }
  .play-row + .play-row { border-top: 1px solid var(--color-edge); }
  .play-toggle { display: grid; width: 100%; min-height: 4rem; grid-template-columns: 2.5rem minmax(0, 1fr) auto; gap: var(--space-4); align-items: center; padding: var(--space-3) var(--space-5); border: 0; background: transparent; color: inherit; font: inherit; text-align: left; cursor: pointer; }
  .play-toggle:hover, .play-toggle[aria-expanded="true"] { background: color-mix(in srgb, var(--color-ink) 4%, transparent); }
  .play-toggle:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: -2px; }
  .play-toggle :global(.media-art) { width: 2.5rem; height: 2.5rem; }
  .play-copy { display: grid; gap: .125rem; min-width: 0; }
  .play-copy strong { color: var(--color-ink); font-size: var(--text-sm); font-weight: 500; }
  small, time, p, li { color: var(--color-ink-muted); font-size: .8125rem; line-height: 1.5; overflow-wrap: anywhere; }
  .play-end { display: grid; justify-items: end; gap: .25rem; }
  .play-detail { display: grid; justify-items: start; gap: .75rem; padding: var(--space-3) var(--space-5) var(--space-4) calc(2.5rem + var(--space-5) + var(--space-4)); }
  .play-comparison { display: grid; gap: .25rem; }
  .play-comparison strong { color: var(--color-ink); font-weight: 500; }
  .play-difference { color: var(--on-warning-container); }
  .play-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .play-actions :global(button) { min-height: 44px; }
  .play-scope, .play-report { width: 100%; min-width: 0; }
  .play-scope { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); color: var(--color-ink-muted); font-size: .8125rem; }
  .play-audience { display: flex; gap: var(--space-2); }
  .play-audience button { min-height: 44px; }
  .play-report { display: grid; gap: var(--space-3); max-width: 32rem; }
  .play-report textarea { width: 100%; resize: vertical; }
  .play-start { display: grid; gap: var(--space-2); width: 100%; }
  .play-start strong { color: var(--color-ink); font-weight: 500; }
  ol { margin: 0; padding: 0; list-style: none; }
  li + li { margin-top: .5rem; }
  li strong { display: block; }
  p { margin: 0; }
  @media (max-width: 650px) {
    .play-toggle { grid-template-columns: 2.5rem minmax(0, 1fr); gap: var(--space-3); padding-inline: var(--space-4); }
    .play-end { grid-column: 2; justify-items: start; }
    .play-detail { padding-inline: var(--space-4); }
  }
</style>
