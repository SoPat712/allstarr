<script lang="ts">
  import { onMount } from "svelte";
  import { home, matchReview, type MatchReviewItem, type ProviderDefinition } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import SelectField from "$lib/components/SelectField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import ProblemContextCard from "$lib/components/ProblemContextCard.svelte";
  import RecordingLyricsPicker from "$lib/components/RecordingLyricsPicker.svelte";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { X } from "@lucide/svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { listenerSupport, reportKindLabel, reportStatusLabel, reportSourceName, reportStatuses, supportDate, supportError,
    supportErrorCode, type ProblemReport, type ProblemReportDetail, type ReportStatus } from "$lib/support";

  let { administrator, initialReportId = "" }: { administrator: boolean; initialReportId?: string } = $props();
  let filter = $state<ReportStatus | "all">("new");
  let rows = $state<ProblemReport[]>([]);
  let cursor = $state<string | null>(null);
  let detail = $state<ProblemReportDetail | null>(null);
  let selectedId = $state("");
  let matchOpen = $state(false);
  let matchLoading = $state(false);
  let selectedMatch = $state<MatchReviewItem | null>(null);
  let matchError = $state("");
  let matchTrigger: HTMLElement | undefined;
  let matchWasOpen = false;
  let backend = $state("Local library");
  const wrongMatch = $derived(detail?.kind === "wrong-match");
  let lyricsOpen = $state(false);
  let lyricsTrigger: HTMLElement | undefined;
  const lyricsRecordingId = $derived(detail?.context.song?.canonicalRecordingId);
  let desired = $state<ReportStatus>("new");
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let detailLoading = $state(false);
  let saving = $state(false);
  let error = $state("");
  let detailError = $state("");
  let conflict = $state(false);
  let savedMessage = $state("");
  let disposed = false;
  let request = 0;
  const provider = (id: string | null) => reportSourceName(providers, id);

  $effect(() => {
    if (matchOpen) { matchWasOpen = true; return; }
    if (!matchWasOpen) return;
    matchWasOpen = false;
    const trigger = matchTrigger;
    matchTrigger = undefined;
    queueMicrotask(() => { if (trigger?.isConnected) trigger.focus(); });
  });

  async function load(more = false) {
    if (!administrator || busy || disposed || more && !cursor) return;
    busy = true; error = "";
    try {
      const next = await listenerSupport.list(filter, more ? cursor! : undefined);
      if (disposed) return;
      rows = more ? [...rows, ...next.items.filter(item => !rows.some(old => old.id === item.id))] : next.items;
      cursor = next.nextCursor;
    } catch (cause) { error = supportError(cause, "Reports could not be loaded. Try again."); }
    finally { busy = false; loading = false; }
  }
  async function choose(next: string) {
    if (busy) return;
    filter = next as ReportStatus | "all"; cursor = null; rows = []; loading = true;
    await load();
  }
  async function open(id: string) {
    if (saving) return;
    lyricsOpen = false;
    matchOpen = false; matchLoading = false; selectedMatch = null; matchError = "";
    const current = ++request;
    selectedId = id; detail = null;
    detailLoading = true; detailError = ""; conflict = false; savedMessage = "";
    try {
      const incoming = await listenerSupport.detail(id);
      if (disposed || current !== request) return;
      detail = incoming; desired = incoming.status;
    } catch (cause) { if (current === request) detailError = supportError(cause, "This report could not be loaded. Try again."); }
    finally { if (current === request) detailLoading = false; }
  }
  async function saveStatus(status: ReportStatus, message = "") {
    if (!detail || saving || status === detail.status || conflict) return;
    const report = detail;
    const current = request;
    saving = true; detailError = ""; savedMessage = message;
    try {
      const result = await listenerSupport.status(report.id, status, report.revision);
      if (disposed || current !== request) return;
      detail = { ...report, ...result }; desired = result.status;
      savedMessage = `${message ? `${message} ` : ""}Report marked ${reportStatusLabel(result.status).toLowerCase()}.`;
      await load();
    } catch (cause) {
      if (disposed || current !== request) return;
      conflict = supportErrorCode(cause) === "report_changed";
      detailError = supportError(cause, "The report status could not be saved. Try again.");
    } finally { saving = false; }
  }
  async function save(event: SubmitEvent) {
    event.preventDefault();
    await saveStatus(desired);
  }
  async function reviewMatch(trigger: HTMLElement) {
    const song = detail?.context.song;
    if (!song?.externalSnapshotId || matchLoading || saving) return;
    const current = request;
    matchTrigger = trigger; matchLoading = true; matchError = "";
    try {
      const match = await matchReview.get(song.externalSnapshotId);
      if (disposed || current !== request) return;
      if (!match || match.externalSnapshotId !== song.externalSnapshotId ||
        song.canonicalRecordingId && match.canonicalRecordingId !== song.canonicalRecordingId)
        throw new Error("The captured recording no longer has a matching review available.");
      selectedMatch = match; matchOpen = true;
    } catch (cause) {
      if (!disposed && current === request)
        matchError = cause instanceof Error ? cause.message : "The match review could not be loaded. Try again.";
    } finally { if (current === request) matchLoading = false; }
  }
  async function matchSaved(message: string) {
    if (disposed || !detail || selectedMatch?.externalSnapshotId !== detail.context.song?.externalSnapshotId) return;
    savedMessage = message;
    if (detail.status === "new") {
      desired = "seen";
      await saveStatus("seen", message);
    }
  }
  const scheduler = createRefreshScheduler(() => load());
  onMount(() => {
    if (!administrator) return;
    void load();
    if (initialReportId) void open(initialReportId);
    void home.schema().then(schema => { providers = schema.providers ?? []; backend = schema.activeBackend ?? "Local library"; }).catch(() => {});
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; request++; scheduler.cancel(); unsubscribe(); };
  });
</script>

{#if administrator}
  <section class="settings-content" aria-label="Problem report inbox">
    <header class="panel-heading"><div><h2>Problem reports</h2><p>Notes and captured diagnostics from your listeners.</p></div><div class="panel-heading-actions"><Button variant="secondary" disabled={busy} onclick={() => void load()}>Refresh reports</Button></div></header>
    <SegmentedNav label="Report status" active={filter} onchange={(next) => void choose(next)} items={[
      { id: "new", label: "New" }, { id: "seen", label: "Seen" }, { id: "fixed", label: "Fixed" }, { id: "all", label: "All" },
    ]} />
    {#if error}<div class="degraded-banner" role="alert"><p>{error}</p><Button variant="secondary" disabled={busy} onclick={() => void load()}>Retry reports</Button></div>{/if}
    {#if loading}<Skeleton class="settings-card skeleton-panel" aria-label="Loading reports" aria-busy="true" />
    {:else if rows.length}
      <article class="settings-card" aria-label="Report list">
        {#each rows as row (row.id)}
          <div class="settings-row"><div><strong>{row.songTitle ?? "General report"}</strong><small>{row.artist ? `${row.artist} · ` : ""}{reportKindLabel(row.kind)} · {row.reporterName} · {supportDate(row.createdAt)}</small><small>{row.notePreview}</small><small>Source: {provider(row.providerId)} · App: {row.clientName ?? "Not captured"}</small></div>
            <div class="panel-heading-actions"><Badge tone={row.status === "fixed" ? "success" : row.status === "new" ? "accent" : "neutral"}>{reportStatusLabel(row.status)}</Badge><Button variant="secondary" disabled={saving} aria-label={`Open report for ${row.songTitle ?? "general problem"}`} onclick={() => void open(row.id)}>Open report</Button></div></div>
        {/each}
        {#if cursor}<footer><Button variant="secondary" disabled={busy} onclick={() => void load(true)}>{busy ? "Loading…" : "Load more reports"}</Button></footer>{/if}
      </article>
    {:else if !error}<article class="settings-card"><header><h3>No {filter === "all" ? "" : `${filter} `}reports</h3></header><p class="settings-impact">Listeners can report a song from its details or send a note from Getting started.</p></article>{/if}
    {#if detailLoading}<Skeleton class="settings-card skeleton-panel" aria-label="Loading report" aria-busy="true" />{/if}
    {#if detailError}<div class="degraded-banner" role="alert"><p>{detailError}</p>{#if selectedId}<Button variant="secondary" disabled={detailLoading || saving} onclick={() => void open(selectedId)}>Reload report</Button>{/if}</div>{/if}
    {#if detail}
      <article class="settings-card" aria-label="Report note"><header><div><h3>{reportKindLabel(detail.kind)}</h3><small>Reported by {detail.reporterName} · {supportDate(detail.createdAt)}</small></div></header>
        <p class="settings-impact whitespace-pre-wrap break-words">{detail.note}</p>
        <form aria-label="Report status change" onsubmit={(event) => void save(event)}>
          <div class="settings-row"><div><strong>Status</strong><small>Opening a report leaves its status unchanged.</small></div><SelectField label="Report status" value={desired} options={reportStatuses} onchange={(value) => desired = value as ReportStatus} disabled={saving || conflict} /></div>
          {#if savedMessage}<p class="settings-impact" role="status">{savedMessage}</p>{/if}
          <footer><Button type="submit" disabled={saving || conflict || desired === detail.status}>{saving ? "Saving…" : "Save status"}</Button></footer>
        </form>
      </article>
      {#if matchError}<div class="degraded-banner" role="alert"><p>{matchError}</p></div>{/if}
      <ProblemContextCard context={detail.context} {providers} review {matchLoading}
        onReviewMatch={wrongMatch ? (trigger) => void reviewMatch(trigger) : undefined}
        matchReviewNote={wrongMatch && detail.context.song && !detail.context.song.externalSnapshotId ? "This song has no provider match to review." : ""}
        onReviewLyrics={(trigger) => {
        lyricsTrigger = trigger;
        lyricsOpen = true;
      }} />
    {/if}
  </section>

  <MatchDialog bind:open={matchOpen} match={selectedMatch} {providers} {backend} showReject={false} onSaved={matchSaved} />

  <Dialog.Root bind:open={lyricsOpen}>
    <Dialog.Portal>
      <Dialog.Overlay class="dialog-overlay" />
      <Dialog.Content class="source-dialog" onCloseAutoFocus={(event) => {
        if (lyricsTrigger?.isConnected) { event.preventDefault(); lyricsTrigger.focus(); }
      }}>
        <header>
          <div><Dialog.Title>Review lyrics</Dialog.Title><Dialog.Description>{detail?.context.song?.title ?? "Reported recording"}{detail?.context.song?.artist ? ` · ${detail.context.song.artist}` : ""}</Dialog.Description></div>
          <Dialog.Close class="icon-button" aria-label="Close lyrics review"><X size={18} aria-hidden="true" /></Dialog.Close>
        </header>
        <p class="settings-impact" style="margin: 0; padding: var(--space-3) var(--surface-gutter);">Personal choices apply to your signed-in account. Household choices set a shared default.</p>
        {#if lyricsOpen && lyricsRecordingId}
          {#key lyricsRecordingId}<RecordingLyricsPicker recordingId={lyricsRecordingId} {providers} allowHousehold />{/key}
        {/if}
        <footer class="dialog-actions" style="position: sticky; bottom: 0; z-index: 1;"><Button onclick={() => lyricsOpen = false}>Done</Button></footer>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>
{/if}
