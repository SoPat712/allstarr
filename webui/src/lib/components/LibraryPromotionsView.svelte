<script lang="ts">
  import { onMount, untrack } from "svelte";
  import { home, type ProviderDefinition } from "$lib/api";
  import { libraryPromotions, type LibraryRequest, type PromotionList, type PromotionPreview, type PromotionRun } from "$lib/library-promotion-api";
  import { activePromotion, promotionArtwork, promotionGroup, promotionProgress, requestSummary } from "$lib/library-promotion-presentation";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import PageStatus from "./patterns/PageStatus.svelte";
  import FilterSegments from "./patterns/FilterSegments.svelte";
  import ItemRow from "./patterns/ItemRow.svelte";
  import GroupedRow from "./patterns/GroupedRow.svelte";
  import StatePill from "./patterns/StatePill.svelte";
  import RowsSkeleton from "./patterns/RowsSkeleton.svelte";
  import ErrorState from "./patterns/ErrorState.svelte";
  import EmptyState from "./patterns/EmptyState.svelte";
  import MediaArtwork from "./MediaArtwork.svelte";
  import LibraryPromotionAddPanel from "./LibraryPromotionAddPanel.svelte";
  import LibraryPromotionPanel from "./LibraryPromotionPanel.svelte";
  import { Button } from "./ui/button";

  let { initialRun = "", initialRequest = "", initialRecording = "", administrator = false }: { initialRun?: string; initialRequest?: string; initialRecording?: string; administrator?: boolean } = $props();
  let data = $state<PromotionList | null>(null), loading = $state(true), error = $state("");
  let filter = $state("active"), selected = $state<PromotionRun | null>(null), open = $state(false), panelError = $state(""), panelLoading = $state(false);
  let requests = $state<LibraryRequest[]>([]), requestError = $state("");
  let selectedRequest = $state<LibraryRequest | null>(null), requestPreview = $state<PromotionPreview | null>(null);
  let addOpen = $state(false);
  let busy = $state(false), visible = $state(true);
  let providers = $state<ProviderDefinition[]>([]);
  let listRevision = 0, panelRevision = 0;
  let initialOpened = "", initialRequestOpened = "", initialRecordingOpened = "";
  const pendingCount = $derived(requestError ? undefined : requests.length);
  const pendingVisible = $derived((filter === "active" || filter === "all") && requests.length > 0);
  let panelController: AbortController | undefined;
  let pollController: AbortController | undefined;
  let polling = false;
  const rows = $derived(data?.items ?? []);
  const selectedIndex = $derived(rows.findIndex(run => run.id === selected?.id));
  const groups = $derived.by(() => {
    const result = new Map<string, { label: string; items: PromotionRun[] }>();
    for (const id of ["active", "needs-look", "added", "older", "cancelled", "finished"]) {
      const items = rows.filter(run => {
        const group = promotionGroup(run);
        if (group.id !== "added") return group.id === id;
        return (run.finishedAt && new Date(run.finishedAt).toISOString().slice(0, 10) === new Date().toISOString().slice(0, 10) ? "added" : "older") === id;
      });
      if (items.length) result.set(id, { label: id === "older" ? "Added earlier" : promotionGroup(items[0]).label, items });
    }
    return [...result.entries()];
  });
  async function refresh(append = false) {
    const revision = ++listRevision;
    try {
      const state = filter;
      const responses = await Promise.allSettled([libraryPromotions.list(state, append ? data?.nextCursor ?? undefined : undefined), state === "active" ? libraryPromotions.list("added") : Promise.resolve(null), libraryPromotions.requests()]);
      if (revision !== listRevision) return;
      if (responses[0].status === "rejected") throw responses[0].reason;
      const result = responses[0].value;
      if (responses[2].status === "fulfilled") { requests = responses[2].value; requestError = ""; }
      else { requests = []; requestError = "Pending requests could not be loaded. Try again."; }
      const recent = responses[1].status === "fulfilled" ? responses[1].value : null;
      if (recent) result.items = [...result.items, ...recent.items.filter(run => run.finishedAt && new Date(run.finishedAt).toISOString().slice(0, 10) === new Date().toISOString().slice(0, 10))];
      data = append && data ? { ...result, items: [...data.items, ...result.items.filter(item => !data!.items.some(old => old.id === item.id))] } : result;
      error = responses[1].status === "rejected" ? "Recent additions could not be loaded. Try again." : "";
    } catch (cause) { if (revision === listRevision) error = cause instanceof Error ? cause.message : "Library additions could not be loaded."; }
    finally { if (revision === listRevision) loading = false; }
  }
  async function openRun(id: string, initial?: PromotionRun) {
    panelController?.abort(); pollController?.abort(); const controller = new AbortController(); panelController = controller;
    const revision = ++panelRevision;
    selectedRequest = null; requestPreview = null; selected = initial ?? null; open = true; panelError = ""; panelLoading = true;
    try { const run = await libraryPromotions.get(id, controller.signal); if (revision === panelRevision) selected = run; }
    catch (cause) { if (!controller.signal.aborted) panelError = cause instanceof Error ? cause.message : "This run could not be loaded."; }
    finally { if (revision === panelRevision) panelLoading = false; }
  }
  async function openRequest(id: string, initial?: LibraryRequest) {
    panelController?.abort(); pollController?.abort(); const controller = new AbortController(); panelController = controller;
    const revision = ++panelRevision;
    selected = null; selectedRequest = initial ?? null; requestPreview = null; open = true; panelError = ""; panelLoading = true;
    try {
      const receipt = await libraryPromotions.getRequest(id, controller.signal);
      if (revision !== panelRevision) return;
      selectedRequest = receipt;
      if (administrator && receipt.state === "pending") {
        const plan = await libraryPromotions.requestPreview(id, controller.signal);
        if (revision === panelRevision) requestPreview = plan;
      }
    } catch (cause) { if (!controller.signal.aborted) panelError = cause instanceof Error ? cause.message : "This request could not be loaded."; }
    finally { if (revision === panelRevision) panelLoading = false; }
  }
  async function approveRequest() {
    if (!selectedRequest || busy) return;
    busy = true;
    try {
      selectedRequest = await libraryPromotions.approveRequest(selectedRequest.id);
      await refresh();
      if (selectedRequest.runId) { const id = selectedRequest.runId; await openRun(id); toast.success("Approved and adding to the library."); }
      else { open = false; toast.success("This album is already in the library."); }
    } finally { busy = false; }
  }
  async function declineRequest(note?: string) {
    if (!selectedRequest || busy) return;
    busy = true;
    try { selectedRequest = await libraryPromotions.declineRequest(selectedRequest.id, note); await refresh(); open = false; toast.success("Request declined."); }
    finally { busy = false; }
  }
  async function cancelRequest(request = selectedRequest) {
    if (!request || busy) return;
    busy = true;
    try {
      const receipt = await libraryPromotions.cancelRequest(request.id);
      if (selectedRequest?.id === request.id) selectedRequest = receipt;
      await refresh(); toast.success("Request cancelled.");
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "This request could not be cancelled. Try again."); throw cause; }
    finally { busy = false; }
  }
  async function pollRun() {
    if (open && selectedRequest?.state === "pending" && !busy && !panelLoading && visible && !polling) {
      const id = selectedRequest.id, revision = panelRevision;
      const controller = new AbortController(); pollController = controller; polling = true;
      try {
        const receipt = await libraryPromotions.getRequest(id, controller.signal);
        if (revision === panelRevision && !controller.signal.aborted) {
          selectedRequest = receipt;
          if (receipt.state !== "pending") { await refresh(); if (receipt.runId && administrator) await openRun(receipt.runId); }
        }
      } catch (cause) { if (!controller.signal.aborted) panelError = cause instanceof Error ? cause.message : "Request updates are unavailable."; }
      finally { polling = false; }
      return;
    }
    if (!open || !selected || !activePromotion(selected) || !visible || panelLoading || busy || polling) return;
    const id = selected.id, revision = panelRevision;
    const controller = new AbortController(); pollController = controller; polling = true;
    try { const run = await libraryPromotions.get(id, controller.signal); if (revision === panelRevision && selected?.id === id && !controller.signal.aborted) { selected = run; panelError = ""; } }
    catch (cause) { if (revision === panelRevision && !controller.signal.aborted) panelError = cause instanceof Error ? cause.message : "Run updates are unavailable."; }
    finally { polling = false; }
  }
  async function review(recordingId: string, action: "try-next-source" | "add-anyway" | "skip", reason?: string) {
    if (!selected || busy) return;
    busy = true;
    try { selected = await libraryPromotions.review(selected.id, recordingId, action, reason); toast.success(action === "skip" ? "Song skipped." : action === "try-next-source" ? "Trying the next source." : "Adding this copy with your reason."); await refresh(); }
    finally { busy = false; }
  }
  async function cancel() {
    if (!selected || busy) return;
    busy = true;
    try { selected = await libraryPromotions.cancel(selected.id); toast.success("Adding cancelled."); await refresh(); }
    finally { busy = false; }
  }
  const scheduler = createRefreshScheduler(refresh, 250, 1000);
  $effect(() => { filter; untrack(() => { data = null; loading = true; void refresh(); }); });
  $effect(() => { if (initialRun && initialRun !== initialOpened) { initialOpened = initialRun; untrack(() => void openRun(initialRun)); } });
  $effect(() => { if (initialRequest && initialRequest !== initialRequestOpened) { initialRequestOpened = initialRequest; untrack(() => void openRequest(initialRequest)); } });
  $effect(() => { if (initialRecording && initialRecording !== initialRecordingOpened) { initialRecordingOpened = initialRecording; addOpen = true; } });
  onMount(() => {
    void home.schema().then(schema => providers = schema.providers).catch(() => {});
    const visibility = () => { visible = document.visibilityState !== "hidden"; if (visible) void pollRun(); else pollController?.abort(); };
    visibility(); document.addEventListener("visibilitychange", visibility);
    const timer = setInterval(() => void pollRun(), 2500);
    const unsubscribe = liveUpdates.subscribe(() => { if (document.visibilityState !== "hidden") scheduler.schedule(); }, 10000);
    return () => { ++listRevision; ++panelRevision; panelController?.abort(); pollController?.abort(); clearInterval(timer); unsubscribe(); scheduler.cancel(); document.removeEventListener("visibilitychange", visibility); };
  });
</script>

<section class="promotion-page" class:panel-open={open} aria-label="To library">
  <h2>Adding to your library</h2>
  {#if data}<PageStatus label="Library additions" items={[{ label: `${data.counts.active - data.counts.needsLook} adding`, emphasis: true }, ...(pendingCount === undefined ? [] : [{ label: `${pendingCount} waiting for approval` }]), { label: `${data.counts.needsLook} needs a look`, tone: data.counts.needsLook ? "warn" : "neutral" }, { label: `${data.counts.addedToday} added today` }]} />{/if}
  <FilterSegments bind:value={filter} label="Library addition filters" options={[{ id: "active", label: "Active", count: data && pendingCount !== undefined ? data.counts.active + pendingCount : undefined }, { id: "needs-look", label: "Needs a look", count: data?.counts.needsLook }, { id: "added", label: "Added", count: data?.counts.added }, { id: "all", label: "All", count: data && pendingCount !== undefined ? data.counts.all + pendingCount : undefined }]} />
  {#if error}<ErrorState title="Library additions are unavailable" detail={error} onretry={() => refresh()} />{/if}
  {#if requestError}<ErrorState title="Requests are unavailable" detail={requestError} onretry={() => refresh()} />{/if}
  {#if loading && !data}<RowsSkeleton count={5} />{:else if rows.length || pendingVisible}<div class="promotion-list">
    {#if pendingVisible}<GroupedRow summary="Waiting for approval" count={requests.length} collapsible={false}>
      {#snippet members()}{#each requests as request (request.id)}
        <ItemRow title={request.title} subtitle={requestSummary(request)} selected={open && selectedRequest?.id === request.id} onclick={() => openRequest(request.id, request)} actionLabel={`${administrator ? "Review" : "Open request for"} ${request.title}`}>
          {#snippet leading()}<MediaArtwork url={promotionArtwork(request.coverReference)} />{/snippet}
          {#snippet pill()}<StatePill filled compact text="Request" />{/snippet}
          {#snippet trailing()}{#if administrator}<Button size="sm" class="rounded-full" variant="secondary" disabled={busy} onclick={() => openRequest(request.id, request)}>Review</Button>{:else}<Button size="sm" class="rounded-full" variant="text" disabled={busy} onclick={() => void cancelRequest(request).catch(() => {})}>Cancel request</Button>{/if}{/snippet}
        </ItemRow>
      {/each}{/snippet}
    </GroupedRow>{/if}
    {#each groups as [id, group] (id)}<GroupedRow summary={group.label} count={group.items.length} collapsible={false}>
      {#snippet members()}
      {#each group.items as run (run.id)}{@const state = promotionGroup(run)}{@const progress = promotionProgress(run)}
        <ItemRow title={run.title} subtitle={`${run.artist} · ${state.id === "needs-look" ? `${run.counts.needsLook} ${run.counts.needsLook === 1 ? "song needs" : "songs need"} a look` : state.id === "active" ? `${progress.added} of ${progress.total} songs added` : `${run.counts.total} ${run.counts.total === 1 ? "song" : "songs"}${run.finishedAt ? ` · ${new Date(run.finishedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}` : ""}`}`} selected={open && selected?.id === run.id} attention={state.id === "needs-look"} onclick={() => openRun(run.id, run)} actionLabel={`Open ${run.title}`}>
          {#snippet leading()}<MediaArtwork url={promotionArtwork(run.coverReference)} />{/snippet}{#snippet pill()}<StatePill filled compact tone={state.tone} text={state.chip} />{/snippet}
        </ItemRow>
      {/each}
      {/snippet}
    </GroupedRow>{/each}
  </div>{:else if !error}<EmptyState title={filter === "needs-look" ? "Nothing needs a look" : filter === "added" ? "No albums added yet" : "No library additions here"} description="Add an album from Kept to follow its progress here." />{/if}
  {#if data?.nextCursor}<Button variant="text" onclick={() => refresh(true)}>Load more</Button>{/if}
</section>
<LibraryPromotionPanel bind:open run={selected} requestReceipt={selectedRequest} preview={requestPreview} {administrator} onapprove={approveRequest} ondecline={declineRequest} oncancelrequest={() => cancelRequest()} {providers} title="Library addition" loading={panelLoading} error={panelError} {busy} onreview={review} oncancel={cancel} onclose={() => { ++panelRevision; panelController?.abort(); pollController?.abort(); }} onPrevious={selectedIndex > 0 ? () => openRun(rows[selectedIndex - 1].id, rows[selectedIndex - 1]) : undefined} onNext={selectedIndex >= 0 && selectedIndex < rows.length - 1 ? () => openRun(rows[selectedIndex + 1].id, rows[selectedIndex + 1]) : undefined} />

{#if initialRecording}<LibraryPromotionAddPanel bind:open={addOpen} recordingId={initialRecording} {providers} onrequestchange={() => refresh()} />{/if}

<style>
  .promotion-page { display: grid; min-width: 0; align-content: start; justify-items: start; gap: 18px; }
  .promotion-page > :global(*) { max-width: 100%; }
  h2 { margin: 0; font-size: 1.35rem; font-weight: 600; }
  .promotion-list { width: 100%; min-width: 0; overflow: hidden; border: 1px solid var(--color-edge); border-radius: 14px; }
</style>
