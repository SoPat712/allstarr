<script lang="ts">
  import { formatLocalDateTime } from "$lib/dates";
  import { newUuid } from "$lib/uuid";
  import { countLabel } from "$lib/count-label";
  import { onMount, tick } from "svelte";
  import { ApiError, home, matchReview, playTraces, type MatchAuthorityScope, type MatchReviewItem, type PlayTrace, type PlayTraceChoice, type ProviderDefinition } from "$lib/api";
  import { expectedAuthorityForScope } from "$lib/matching-api";
  import { dayLabel, playArtwork } from "$lib/activity";
  import { playDuration, playOutcome, playPhase, playSource, playComparison, playActionError } from "$lib/play-traces";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { MoreHorizontal, Headphones } from "@lucide/svelte";
  import { buttonVariants } from "$lib/components/ui/button";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import { groupPlays } from "./activity-rows";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  let matchModule = $state<typeof import("./MatchDialog.svelte")>();
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";

  let { administrator = false, scope = "mine", currentUserId = "" }: { administrator?: boolean; scope?: "mine" | "everyone"; currentUserId?: string } = $props();
  let plays = $state<PlayTrace[]>([]);
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let cursor = $state("");
  let hasMore = $state(false);
  let expanded = $state("");
  let panelOpen = $state(false);
  let filter = $state("all");
  let selectedRuns = $state<PlayTrace[]>([]);
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
  let opener: HTMLElement | null = null;
  let disposed = false;
  let detailRequest = 0;
  let refreshPending = false;
  const days = $derived.by(() => {
    const result: { label: string; plays: PlayTrace[][] }[] = [];
    for (const runs of groupPlays(plays)) {
      if (!runs.some(play => filter === "all" || (filter === "slow" ? (play.timing.firstByteMs ?? 0) > 4000 : play.outcome.toLowerCase() === filter))) continue;
      const label = dayLabel(runs[0].startedAt);
      let day = result.at(-1);
      if (day?.label !== label) result.push(day = { label, plays: [] });
      day.plays.push(runs);
    }
    return result;
  });
  const steps = $derived([...(detail?.timing.phases ?? [])].sort((a, b) => a.startedMs - b.startedMs));
  function time(value: string) {
    return formatLocalDateTime(value, "Time not recorded");
  }
  const clock = (value: string) => Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleTimeString(undefined, { timeStyle: "short" }) : "Not recorded";
  function startRange(runs: PlayTrace[]) {
    const starts = runs.map(run => run.timing.firstByteMs).filter((value): value is number => value != null && Number.isFinite(value) && value >= 0);
    if (!starts.length) return "Start not recorded";

    const low = Math.round(Math.min(...starts) / 100) / 10, high = Math.round(Math.max(...starts) / 100) / 10;
    return (low === high ? String(low) : low + "–" + high) + " s";
  }
  function outcomes(runs: PlayTrace[]) {
    const counts = new Map<string, number>();
    for (const run of runs) { const label = playOutcome(run.outcome).label; counts.set(label, (counts.get(label) ?? 0) + 1); }
    return [...counts].map(([label, count]) => label + (count > 1 ? " ×" + count : "")).join(" · ");
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
    if (!panelOpen) opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    panelOpen = true;
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
      matchModule ??= await import("./MatchDialog.svelte");
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
    reportNote = ""; reportRequestId = newUuid(); reportOpen = true;
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
        const dialog = document.getElementById(`play-${id}`)?.closest('[role="dialog"]');
        (dialog ?? document.body).appendChild(field);
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
  <PageStatus label="Plays status" items={[
    { label: loading ? "Loading plays…" : countLabel(plays.length, "play") + " loaded" },
    { label: countLabel(plays.filter(play => (play.timing.firstByteMs ?? 0) > 4000).length, "slow start"), tone: plays.some(play => (play.timing.firstByteMs ?? 0) > 4000) ? "warn" : "neutral" },
  ]} />
  <div class="plays-toolbar"><FilterSegments label="Play filters" storageKey={"activity.plays." + scope} bind:value={filter} options={[
    { id: "all", label: "All", count: plays.length },
    { id: "completed", label: "Played", count: plays.filter(play => play.outcome.toLowerCase() === "completed").length },
    { id: "cancelled", label: "Stopped", count: plays.filter(play => play.outcome.toLowerCase() === "cancelled").length },
    { id: "failed", label: "Problems", count: plays.filter(play => play.outcome.toLowerCase() === "failed").length },
    { id: "slow", label: "Slow starts", count: plays.filter(play => (play.timing.firstByteMs ?? 0) > 4000).length },
  ]} /><RefreshButton label="Refresh plays" onrefresh={() => load()} /></div>
  {#if loading}
    <RowsSkeleton label="Loading plays" />
  {:else if error && !plays.length}
    <ErrorState title={error} onretry={() => load()} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
    <div class="plays-list">
    {#if days.length}<div class="play-headings table-heading" aria-hidden="true"><span>Song</span><div class="play-columns"><span>Played from</span><span>Start</span><span>Result</span><span>When</span></div></div>{/if}
    {#each days as day (day.label)}
      <section class="event-log-day" aria-label={day.label}>
        <h2>{day.label}</h2>
        <div class="event-log-list">
          {#each day.plays as runs (runs[0].id)}
            {@const play = runs[0]}
            <article class="play-row">
              <ItemRow title={(play.request.presented.title || "Unknown song") + (runs.length > 1 ? " ×" + runs.length : "")} subtitle={play.request.presented.artist || "Unknown artist"} selected={panelOpen && selectedRuns[0]?.id === play.id} onclick={() => { selectedRuns = runs; toggle(play); }}>
                {#snippet leading()}<MediaArtwork url={playArtwork(play)} />{/snippet}
                {#snippet trailing()}
                  <div class="play-columns">
                    <span class="play-source">{#each [...new Set(runs.map(run => playSource(run.resolution.provider, run.resolution.cached, providers)))] as source}<span>{#if source !== "Saved copy"}<ProviderMark decorative id={runs.find(run => playSource(run.resolution.provider, run.resolution.cached, providers) === source)?.resolution.provider || "library"} definition={providers.find(item => item.name === source)} label={source} />{/if}{source}</span>{/each}</span>
                    <span class="play-start-time">{startRange(runs)}{#if runs.some(run => (run.timing.firstByteMs ?? 0) > 4000)}<StatePill tone="warn" text="Slow start" />{/if}</span>
                    <span class="play-result" title={outcomes(runs)}>{outcomes(runs).replace("Could not play", "Couldn't play")}</span>
                    <time datetime={play.startedAt} title={time(play.startedAt)}>{clock(play.startedAt)}</time>
                  </div>
                {/snippet}
              </ItemRow>
            </article>
          {/each}
        </div>
      </section>
    {:else}
      <EmptyState title={plays.length ? "No plays match this filter" : "No plays yet"} description={plays.length ? "Choose All to see every loaded play." : "Play something and it will show up here."}>{#snippet icon()}<Headphones size={24} />{/snippet}</EmptyState>
    {/each}
    </div>
    {#if hasMore}<div class="plays-toolbar"><Button variant="secondary" disabled={busy} onclick={() => void load(true)}>Load earlier plays</Button></div>{/if}
  {/if}
</section>

{#if expanded}
  <SidePanel bind:open={panelOpen} title={detail?.request.presented.title || selectedRuns[0]?.request.presented.title || "Play details"} description={detail?.request.presented.artist || selectedRuns[0]?.request.presented.artist || ""} onclose={() => { expanded = ""; detailRequest++; reportOpen = false; void tick().then(() => { if (opener?.isConnected) opener.focus({ preventScroll: true }); }); }}>
    {#snippet menu()}
      <DropdownMenu.Root>
        <DropdownMenu.Trigger class={buttonVariants({ variant: "text", size: "icon" })} aria-label="More play actions"><MoreHorizontal size={18} /></DropdownMenu.Trigger>
        <DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu" align="end">
          <DropdownMenu.Item class="bits-menu-item play-forget" disabled={!detail?.resolution.externalSnapshotId || actionBusy} onSelect={() => forgetOpen = true}>Look for it again</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" disabled={!detail?.resolution.externalSnapshotId || actionBusy} onSelect={openReport}>Report a problem</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" disabled={copying} onSelect={() => void copy(expanded)}>Copy diagnostics</DropdownMenu.Item>
        </DropdownMenu.Content></DropdownMenu.Portal>
      </DropdownMenu.Root>
    {/snippet}
    {#if selectedRuns.length > 1}
      <nav class="play-runs" aria-label="Plays of this song">{#each selectedRuns as run}<Button variant={run.id === expanded ? "secondary" : "text"} onclick={() => toggle(run)}>{clock(run.startedAt)} · {playOutcome(run.outcome).label}</Button>{/each}</nav>
    {/if}
                <div class="play-detail" id={`play-${expanded}`} aria-busy={detailBusy}>
                  {#if detailBusy && !detail}<p role="status">Loading start steps…</p>{/if}
                  {#if detailError}<ErrorState title={detailError} onretry={() => loadDetail(expanded)} />{/if}
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
                          <div class="seg play-audience" role="group" aria-label="Change it for">
                            <button type="button" aria-pressed={authorityScope === "personal"} onclick={() => authorityScope = "personal"}>Just me</button>
                            <button type="button" aria-pressed={authorityScope === "household"} onclick={() => authorityScope = "household"}>Everyone</button>
                          </div>
                        </div>
                      {/if}
                      <div class="play-actions">
                        <Button variant="outline" size="sm" disabled={actionBusy || matchLoading} onclick={() => void reviewMatch()}>Wrong song?</Button>
                      </div>
                      {#if reportOpen}
                        <form class="play-report" aria-label="Report a problem" onsubmit={(event) => void report(event)}>
                          <label class="field"><span>Note (optional)</span><textarea bind:value={reportNote} maxlength="500" rows="3" disabled={actionBusy}></textarea></label>
                          {#if reportNote.length >= 450}<small>{reportNote.length}/500</small>{/if}
                          <div class="play-actions">
                            <Button variant="secondary" size="sm" type="submit" disabled={actionBusy}>Send report</Button>
                            <Button variant="text" size="sm" disabled={actionBusy} onclick={() => reportOpen = false}>Cancel</Button>
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

                </div>
  </SidePanel>
{/if}
{#if matchModule && matchOpen}
  {@const MatchDialog = matchModule.default}
  <MatchDialog bind:open={matchOpen} match={selectedMatch} {providers} {backend} showReject={false} onChoose={chooseVersion} onSaved={() => {}} />
{/if}
<ConfirmDialog bind:open={forgetOpen} title="Look for this song again?" description="Allstarr will forget this choice and pick the best version next time it plays." confirmLabel="Look for it again" confirmVariant="secondary" disabled={actionBusy} closeOnConfirm={false} onConfirm={forget} />

<style>
  .activity-plays { container-type: inline-size; display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--section-gap); min-width: 0; }
  .plays-toolbar { display: flex; flex-wrap: wrap; gap: var(--space-3); justify-content: space-between; align-items: center; }
  .plays-list { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); }
  .play-row + .play-row, .event-log-day + .event-log-day { border-top: 1px solid var(--color-edge); }
  .event-log-day h2 { margin: 0; padding: var(--space-2) var(--space-4); color: var(--color-ink-muted); font-size: var(--text-xs); background: var(--color-panel-raised); }
  .play-row :global(.item-row), .play-headings { display: grid; grid-template-columns: 36px minmax(0, 1fr) minmax(0, 1.7fr) 18px; gap: var(--space-3); }
  .play-row :global(.row-controls) { min-width: 0; width: 100%; pointer-events: none; }
  .play-columns { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, .9fr) minmax(0, 1.2fr) auto; gap: var(--space-3); align-items: center; width: 100%; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .play-source, .play-start-time { display: grid; gap: var(--space-1); min-width: 0; }
  .play-source > span { display: flex; align-items: center; gap: var(--space-1); }
  .play-result { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .play-headings .play-columns { font: inherit; letter-spacing: inherit; }
  .play-columns time { white-space: nowrap; font-variant-numeric: tabular-nums; }
  .play-headings { padding: var(--space-2) var(--space-4); background: var(--color-panel); color: var(--color-ink-muted); font-size: var(--text-table-heading); text-transform: uppercase; }
  .play-headings > span { grid-column: 1 / 3; }
  .play-detail { display: grid; gap: var(--space-4); }
  :global(.play-forget) { color: var(--danger); }
  .play-runs { display: flex; flex-wrap: wrap; gap: var(--space-2); margin-bottom: var(--space-4); }
  small, time, p, li { color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; overflow-wrap: anywhere; }
  .play-comparison { display: grid; gap: .25rem; }
  .play-comparison strong { color: var(--color-ink); font-weight: 500; }
  .play-difference { color: var(--on-warning-container); }
  .play-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .play-actions :global(button) { min-height: 44px; }
  .play-scope, .play-report { width: 100%; min-width: 0; }
  .play-scope { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); color: var(--color-ink-muted); font-size: .8125rem; }
  .play-audience button { min-height: 44px; }
  .play-report { display: grid; gap: var(--space-3); max-width: 32rem; }
  .play-report textarea { width: 100%; resize: vertical; }
  .play-start { display: grid; gap: var(--space-2); width: 100%; }
  .play-start strong { color: var(--color-ink); font-weight: 500; }
  ol { margin: 0; padding: 0; list-style: none; }
  li + li { margin-top: .5rem; }
  li strong { display: block; }
  p { margin: 0; }
  @container (max-width: 720px) {
    .play-headings { display: none; }
    .play-row :global(.item-row) { display: flex; flex-wrap: wrap; }
    .play-row :global(.row-copy) { flex-basis: calc(100% - 90px); }
    .play-row :global(.row-controls) { flex: 1 1 100%; margin-left: 48px; }
    .play-row :global(.row-chevron) { display: none; }
    .play-columns { grid-template-columns: minmax(0, 1fr) auto; gap: var(--space-2); }
    .play-source { grid-column: 1; } .play-start-time { grid-column: 1; } .play-result { grid-column: 2; grid-row: 1; } .play-columns time { grid-column: 2; grid-row: 2; }
  }
</style>
