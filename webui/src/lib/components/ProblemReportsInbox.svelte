<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount, tick } from "svelte";
  import { home, matchReview, type MatchReviewItem, type ProviderDefinition } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import { MessageSquareWarning } from "@lucide/svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { X } from "@lucide/svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { listenerSupport, reportKindLabel, reportStatusLabel, reportSourceName, reportStatuses, supportDate, supportError,
    supportErrorCode, type ProblemReport, type ProblemReportDetail, type ReportStatus } from "$lib/support";

  let { administrator, initialReportId = "" }: { administrator: boolean; initialReportId?: string } = $props();
  let filter = $state<ReportStatus | "all">("new");
  let rows = $state<ProblemReport[]>([]);
  let known = $state<ProblemReport[]>([]);
  let panelOpen = $state(false);
  const count = (status: string) => known.filter(row => status === "all" || row.status === status).length;
  let cursor = $state<string | null>(null);
  let detail = $state<ProblemReportDetail | null>(null);
  let selectedId = $state("");
  let matchOpen = $state(false);
  let matchLoading = $state(false);
  let selectedMatch = $state<MatchReviewItem | null>(null);
  let matchError = $state("");
  let matchTrigger = $state<HTMLElement>();
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
  let opener: HTMLElement | null = null;
  let disposed = false;
  let request = 0;
  let listRequest = 0;
  const provider = (id: string | null) => reportSourceName(providers, id);

  $effect(() => {
    if (matchOpen) { matchWasOpen = true; return; }
    if (!matchWasOpen) return;
    matchWasOpen = false;
    const trigger = matchTrigger;
    matchTrigger = undefined;
    queueMicrotask(() => { if (trigger?.isConnected) trigger.focus(); });
  });

  async function load(more = false, replace = false) {
    if (!administrator || busy && !replace || disposed || more && !cursor) return;
    const version = ++listRequest;
    busy = true; error = "";
    try {
      const [next, overview] = await Promise.all([
        listenerSupport.list(filter, more ? cursor! : undefined),
        !more && filter !== "all" ? listenerSupport.list("all").catch(() => null) : null,
      ]);
      if (disposed || version !== listRequest) return;
      rows = more ? [...rows, ...next.items.filter(item => !rows.some(old => old.id === item.id))] : next.items;
      cursor = next.nextCursor;
      const merged = new Map(known.map(row => [row.id, row]));
      for (const row of [...(overview?.items ?? []), ...next.items]) merged.set(row.id, row);
      known = [...merged.values()];
    } catch (cause) { if (!disposed && version === listRequest) error = supportError(cause, "Reports could not be loaded. Try again."); }
    finally { if (!disposed && version === listRequest) { busy = false; loading = false; } }
  }
  async function choose(next: string) {
    filter = next as ReportStatus | "all"; cursor = null; rows = []; loading = true;
    await load(false, true);
  }
  async function open(id: string) {
    if (saving) return;
    lyricsOpen = false;
    matchOpen = false; matchLoading = false; selectedMatch = null; matchError = "";
    const current = ++request;
    if (!panelOpen) opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    selectedId = id; detail = null; panelOpen = true;
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
  <section class="settings-content report-inbox" aria-label="Problem report inbox">
    <PageStatus label="Reports status" items={[{ label: loading ? "Loading reports…" : countLabel(count("new"), "new report") }, { label: count("seen") + " seen · " + count("fixed") + " fixed · " + countLabel(known.length, "report") + " loaded" }]} />
    <div class="report-toolbar"><FilterSegments label="Report status" value={filter} onchange={(next) => void choose(next)} storageKey="activity.reports" options={[
      { id: "new", label: "New", count: count("new") }, { id: "seen", label: "Seen", count: count("seen") },
      { id: "fixed", label: "Fixed", count: count("fixed") }, { id: "all", label: "All", count: count("all") },
    ]} /><RefreshButton onrefresh={() => load()} label="Refresh reports" /></div>
    {#if error}<ErrorState title={error} onretry={() => load()} />{/if}
    {#if loading}<RowsSkeleton label="Loading reports" />
    {:else if rows.length}
      <article class="report-list" aria-label="Report list">
        {#each rows as row (row.id)}
          <ItemRow title={reportKindLabel(row.kind) + (row.songTitle ? " · " + row.songTitle : "")} subtitle={[row.artist, row.reporterName, provider(row.providerId), row.clientName, supportDate(row.createdAt), row.notePreview].filter(Boolean).join(" · ")} actionLabel={"Open report for " + (row.songTitle ?? "general problem")} selected={selectedId === row.id && panelOpen} onclick={() => void open(row.id)}>
            {#snippet leading()}<MessageSquareWarning size={20} aria-hidden="true" />{/snippet}
            {#snippet pill()}<Badge tone={row.status === "fixed" ? "success" : row.status === "new" ? "accent" : "neutral"}>{reportStatusLabel(row.status)}</Badge>{/snippet}
          </ItemRow>
        {/each}
        {#if cursor}<footer><Button variant="outline" disabled={busy} onclick={() => void load(true)}>{busy ? "Loading…" : "Load more reports"}</Button></footer>{/if}
      </article>
    {:else if !error}<EmptyState title={filter === "all" ? "No reports" : "No " + filter + " reports"} description="Listeners can report a song from its details in any app, or send a note from Report a problem.">{#snippet icon()}<MessageSquareWarning size={24} />{/snippet}</EmptyState>{/if}
    {#if selectedId}
      <SidePanel bind:open={panelOpen} title={detail ? reportKindLabel(detail.kind) : "Report details"} description={detail ? detail.reporterName + " · " + supportDate(detail.createdAt) : "Captured with the report"} onclose={() => { selectedId = ""; detail = null; request++; lyricsOpen = false; matchOpen = false; void tick().then(() => { if (opener?.isConnected) opener.focus({ preventScroll: true }); }); }}>
        <div class="report-panel-content">
        {#if detailLoading}<RowsSkeleton count={3} label="Loading report" />{/if}
    {#if detailError}<ErrorState title={detailError} onretry={() => open(selectedId)} />{/if}
    {#if detail}
      <article class="settings-card" aria-label="Report note"><header><div><h3>{reportKindLabel(detail.kind)}</h3><small>Reported by {detail.reporterName} · {supportDate(detail.createdAt)}</small></div></header>
        <p class="settings-impact whitespace-pre-wrap break-words">{detail.note}</p>
        <form aria-label="Report status change" onsubmit={(event) => void save(event)}>
          <div class="settings-row"><div><strong>Status</strong><small>Opening a report leaves its status unchanged.</small></div><SelectField label="Report status" value={desired} options={reportStatuses} onchange={(value) => desired = value as ReportStatus} disabled={saving || conflict} /></div>
          {#if savedMessage}<p class="settings-impact" role="status">{savedMessage}</p>{/if}
          <footer><Button type="submit" variant="outline" disabled={saving || conflict || desired === detail.status}>{saving ? "Saving…" : "Save status"}</Button></footer>
        </form>
      </article>
      {#if matchError}<ErrorState title={matchError} onretry={matchTrigger ? () => reviewMatch(matchTrigger!) : undefined} />{/if}
      {#await import("./ProblemContextCard.svelte") then module}
      {@const ProblemContextCard = module.default}
      <ProblemContextCard context={detail.context} {providers} review {matchLoading}
        onReviewMatch={wrongMatch ? (trigger) => void reviewMatch(trigger) : undefined}
        matchReviewNote={wrongMatch && detail.context.song && !detail.context.song.externalSnapshotId ? "This song has no provider match to review." : ""}
        onReviewLyrics={(trigger) => {
        lyricsTrigger = trigger;
        lyricsOpen = true;
      }} />
      {/await}
    {/if}
        </div>
        {#snippet footer()}<Button disabled={!detail || saving || conflict || detail.status === "fixed"} onclick={() => void saveStatus("fixed")}>{detail?.status === "fixed" ? "Fixed" : saving ? "Saving…" : "Mark fixed"}</Button>{/snippet}
      </SidePanel>
    {/if}
  </section>

  {#if matchOpen}{#await import("./MatchDialog.svelte") then module}
    {@const MatchDialog = module.default}
    <MatchDialog bind:open={matchOpen} match={selectedMatch} {providers} {backend} showReject={false} onSaved={matchSaved} />
  {/await}{/if}

  <Dialog.Root bind:open={lyricsOpen}>
    <Dialog.Portal>
      <Dialog.Overlay class="dialog-overlay report-lyrics-overlay" />
      <Dialog.Content class="source-dialog report-lyrics-dialog" onCloseAutoFocus={(event) => {
        if (lyricsTrigger?.isConnected) { event.preventDefault(); lyricsTrigger.focus(); }
      }}>
        <header>
          <div><Dialog.Title>Review lyrics</Dialog.Title><Dialog.Description>{detail?.context.song?.title ?? "Reported recording"}{detail?.context.song?.artist ? ` · ${detail.context.song.artist}` : ""}</Dialog.Description></div>
          <Dialog.Close class="icon-button" aria-label="Close lyrics review"><X size={18} aria-hidden="true" /></Dialog.Close>
        </header>
        <p class="settings-impact" style="margin: 0; padding: var(--space-3) var(--surface-gutter);">Personal choices apply to your signed-in account. Household choices set a shared default.</p>
        {#if lyricsOpen && lyricsRecordingId}
          {#await import("./RecordingLyricsPicker.svelte") then module}
            {@const RecordingLyricsPicker = module.default}
            {#key lyricsRecordingId}<RecordingLyricsPicker recordingId={lyricsRecordingId} {providers} allowHousehold />{/key}
          {/await}
        {/if}
        <footer class="dialog-actions" style="position: sticky; bottom: 0; z-index: 1;"><Button onclick={() => lyricsOpen = false}>Done</Button></footer>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>
{/if}

<style>
  .report-inbox { max-width: none; padding-top: var(--space-4); }
  .report-inbox > :global(.empty-state) { border: 1px dashed var(--color-edge-strong); border-radius: var(--radius-lg); }
  .report-toolbar { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: var(--space-3); margin-block: var(--space-4); }
  .report-list { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); }
  .report-list footer { padding: var(--space-3); text-align: center; }
  .report-panel-content :global(.settings-card) { box-shadow: none; margin-block: var(--space-4); padding: var(--space-3); }
  .report-panel-content :global(.settings-row) { flex-wrap: wrap; }
  :global(.report-lyrics-overlay) { z-index: var(--layer-nested-overlay); }
  :global(.report-lyrics-dialog) { z-index: var(--layer-nested-dialog); }
</style>
