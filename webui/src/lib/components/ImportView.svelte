<script lang="ts">
  import { onMount, tick } from "svelte";
  import { Check } from "@lucide/svelte";
  import { ApiError, home, matchReview, migrations, sources, type MigrationChoice, type MigrationReviewItem,
    type MigrationStatus, type MatchReviewItem, type ProviderAccount, type ProviderDefinition } from "$lib/api";
  import { collectionCountsLine, collectionStateLabel, migrationError, migrationProgress, migrationStep, needsReview, reviewReason, songCount, addedCountLine, remainingCountLine, moveCountLine, readyToMove } from "$lib/migration";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Progress } from "$lib/components/ui/progress";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import SelectField from "$lib/components/SelectField.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ActivityOutcome from "$lib/components/ActivityOutcome.svelte";
  import MatchDialog from "$lib/components/MatchDialog.svelte";

  let { currentUserId, initialAccount = "", initialRun = "" }: { currentUserId: string; initialAccount?: string; initialRun?: string } = $props();
  let accounts = $state<ProviderAccount[]>([]);
  let accountId = $state("");
  let runId = $state("");
  let status = $state<MigrationStatus | null>(null);
  let choices = $state<MigrationChoice[]>([]);
  let cursor = $state<string | null>(null);
  let selected = $state<Record<string, boolean>>({});
  let sync = $state<Record<string, boolean>>({});
  let pages = $state<Record<string, MigrationReviewItem[]>>({});
  let more = $state<Record<string, boolean>>({});
  let expanded = $state<Record<string, boolean>>({});
  let pageBusy = $state<Record<string, boolean>>({});
  let receiptCounts = $state<Record<string, number>>({});
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let confirmStop = $state(false);
  let matchOpen = $state(false);
  let matchLoading = $state(false);
  let selectedMatch = $state<MatchReviewItem | null>(null);
  let matchCollection = "";
  let matchTrigger: HTMLElement | undefined;
  let providers = $state<ProviderDefinition[]>([]);
  let backend = $state("Local library");
  let reviewSection = $state<HTMLElement>();
  let requestKey = "";
  let disposed = false;
  let reading = $state(false);
  let reviewInitialized = false;
  let pendingCount = $state(0);
  const step = $derived(migrationStep(status));
  const runState = $derived(status?.run.state ?? "choosing");
  const interrupted = $derived(runState === "waiting" || runState === "cancelled");
  const collections = $derived(status?.collections.filter(item => item.collection.state !== "skipped") ?? []);
  const selectedCount = $derived(choices.filter(item => selected[item.sourceId]).length);
  const remaining = $derived((status?.total.review ?? 0) + (status?.total.unmatched ?? 0));
  const reviewAvailable = $derived(status && !status.provisional && ["review", "completed"].includes(runState));
  const storageKey = () => `allstarr:migration:${accountId}`;

  function remember() {
    try { if (runId) localStorage.setItem(storageKey(), runId); else localStorage.removeItem(storageKey()); } catch { /* Storage is optional. */ }
  }
  async function discover(loadMore = false) {
    const id = runId;
    const next = await migrations.discover(id, loadMore ? cursor ?? undefined : undefined);
    if (disposed || id !== runId) return;
    const previous = loadMore ? choices : [];
    choices = [...previous, ...next.items.filter(item => !previous.some(old => old.sourceId === item.sourceId))]
      .sort((a, b) => Number(b.kind === "liked-songs") - Number(a.kind === "liked-songs"));
    cursor = next.nextCursor;
  }
  async function loadPage(id: string, loadMore = false) {
    if (pageBusy[id]) return;
    pageBusy[id] = true;
    const currentRun = runId;
    try {
      const previous = pages[id] ?? [];
      const next = await migrations.review(currentRun, id, loadMore ? previous.length : 0);
      if (disposed || currentRun !== runId) return;
      pages[id] = loadMore ? [...previous, ...next] : next;
      more[id] = next.length === 50;
    } catch { error = "These songs could not be loaded. Try opening this collection again."; }
    finally { pageBusy[id] = false; }
  }
  async function loadReceipts(next: MigrationStatus) {
    let pending = 0;
    const counts: Record<string, number> = {};
    for (const item of next.collections.filter(item => item.collection.state === "applied")) {
      let added = 0;
      for (let offset = 0; offset < item.collection.read; offset += 50) {
        const rows = await migrations.review(next.id, item.id, offset);
        if (disposed || next.id !== runId) return;
        added += rows.filter(row => row.receipt?.outcome === 2).length;
        pending += rows.filter(readyToMove).length;
        if (rows.length < 50) break;
      }
      counts[item.id] = added;
    }
    receiptCounts = counts; pendingCount = pending;
  }
  async function refresh() {
    if (!runId || reading) return;
    const id = runId;
    reading = true;
    try {
      const next = await migrations.status(id, ["review", "completed"].includes(runState));
      if (disposed || runId !== id) return;
      status = next; accountId = next.run.providerAccountId; remember(); error = "";
      if (next.run.state === "choosing" && !choices.length) await discover();
      if (!reviewInitialized && !next.provisional && ["review", "completed"].includes(next.run.state)) {
        reviewInitialized = true;
        const first = next.collections.find(item => item.counts.review + item.counts.unmatched > 0);
        if (first) { expanded[first.id] = true; await loadPage(first.id); }
      }
      if (next.run.state === "completed") await loadReceipts(next);
    } catch (cause) {
      if (disposed || id !== runId) return;
      if (cause instanceof ApiError && cause.status === 404) {
        runId = ""; status = null; remember();
        error = "The saved move is no longer available. You can find your music again.";
      } else error = "Your progress could not be refreshed. Try refreshing again.";
    } finally { reading = false; }
  }
  async function load() {
    loading = true; error = "";
    try {
      const result = await sources.accounts();
      if (disposed) return;
      accounts = result.accounts.filter(item => item.providerId === "spotify" && item.scope === "Personal" && item.enabled && item.ownerUserId === currentUserId);
      let preferred = accounts.find(item => item.id === initialAccount);
      if (!preferred && !initialAccount && !initialRun) {
        try { preferred = accounts.find(item => localStorage.getItem(`allstarr:migration:${item.id}`)); } catch { /* Storage is optional. */ }
      }
      accountId = preferred?.id ?? accounts[0]?.id ?? "";
      runId = initialRun;
      if (!runId && accountId) try { runId = localStorage.getItem(storageKey()) ?? ""; } catch { /* Storage is optional. */ }
      if (runId) await refresh();
    } catch { error = "Your Spotify accounts could not be loaded. Try again."; }
    finally { loading = false; }
  }
  async function changeAccount(value: string) {
    accountId = value; requestKey = "";
    try { runId = localStorage.getItem(storageKey()) ?? ""; } catch { runId = ""; }
    if (runId) await refresh();
  }
  async function act(action: () => Promise<unknown>, fallback: string) {
    if (busy) return;
    busy = true; error = "";
    try { await action(); }
    catch { error = fallback; }
    finally { busy = false; }
  }
  async function findMusic() {
    await act(async () => {
      if (!runId) {
        requestKey ||= crypto.randomUUID();
        const receipt = await migrations.start(accountId, requestKey);
        if (disposed) return;
        runId = receipt.id; remember();
      }
      await discover(); await refresh();
    }, "Your music could not be found. Try again with this Spotify account.");
  }
  async function command(action: "apply" | "cancel" | "resume") {
    await act(async () => {
      await migrations.change(runId, action);
      confirmStop = false;
      if (action === "apply") { pendingCount = 0; receiptCounts = {}; }
      await refresh();
    }, "This move could not be updated. Refresh its progress and try again.");
  }
  async function fix(item: MigrationReviewItem, trigger: HTMLElement) {
    if (matchLoading) return;
    matchLoading = true; error = ""; matchTrigger = trigger;
    try {
      const match = await matchReview.get(item.externalSnapshotId);
      if (!match || match.externalSnapshotId !== item.externalSnapshotId) throw new Error();
      selectedMatch = match; matchCollection = item.collectionId; matchOpen = true;
    } catch { error = "This song's match could not be opened. Try again."; }
    finally { matchLoading = false; }
  }
  async function matchSaved() {
    await refresh();
    const loaded = pages[matchCollection]?.length ?? 50;
    const rows: MigrationReviewItem[] = [];
    try {
      for (let offset = 0; offset < loaded; offset += 50) rows.push(...await migrations.review(runId, matchCollection, offset));
      pages[matchCollection] = rows;
    } catch { error = "The match was saved. Refresh to see the updated songs."; }
    await tick(); (matchTrigger?.isConnected ? matchTrigger : reviewSection)?.focus({ preventScroll: true });
  }
  function fresh() {
    runId = ""; remember(); status = null; choices = []; selected = {}; sync = {}; pages = {}; more = {}; expanded = {}; pageBusy = {};
    receiptCounts = {}; pendingCount = 0; reviewInitialized = false; cursor = null; requestKey = crypto.randomUUID(); error = "";
  }
  const scheduler = createRefreshScheduler(refresh);
  onMount(() => {
    void load();
    void home.schema().then(schema => { if (!disposed) { providers = schema.providers ?? []; backend = schema.activeBackend ?? "Local library"; } }).catch(() => {});
    const unsubscribe = liveUpdates.subscribe(event => {
      if (runId && (!event.resourceId || event.resourceId === status?.run.jobId || event.reason)) scheduler.schedule();
    });
    return () => { disposed = true; scheduler.cancel(); unsubscribe(); };
  });
</script>

<section class="settings-content import-page" aria-label="Move your Spotify collection">
  <header class="panel-heading"><div><h2>Bring your music</h2><p>Move your Spotify playlists and liked songs into Allstarr. Nothing is changed in Spotify.</p></div>
    {#if runId}<RefreshButton variant="secondary" label="Refresh import progress" onrefresh={refresh} />{/if}
  </header>
  <ol class="import-steps" aria-label="Import progress">
    {#each ["Choose", "Match", "Review", "Done"] as label, index}
      <li class:current={step === index} aria-current={step === index ? "step" : undefined}>{#if index < step}<Check size={16} aria-hidden="true" />{/if}{label}</li>
    {/each}
  </ol>
  {#if loading}<Skeleton class="settings-card skeleton-panel" aria-label="Loading your music" />
  {:else}
    {#if error && !accounts.length && !runId}
      <RouteError eyebrow="Spotify unavailable" title="Your accounts are unavailable." message={error} onRetry={load} />
    {:else if error}<p class="notice-error" role="alert">{error}</p>{/if}
    {#if (!status || runState === "choosing") && (accounts.length || runId || !error)}
      {#if !accounts.length && !runId && !error}
        <article class="settings-card"><div class="import-body"><p>Connect your Spotify account first.</p><Button href="#/integrations/services">Connect Spotify</Button></div></article>
      {:else if !runId}
        <article class="settings-card"><div class="import-body">
          <SelectField value={accountId} options={accounts.map(item => ({ value: item.id, label: item.displayName }))} label="Spotify account" disabled={busy} onchange={changeAccount} />
          <Button disabled={busy || !accountId} onclick={findMusic}>Find my music</Button>
        </div></article>
      {/if}
      {#if runId}
        <article class="settings-card" aria-label="Choose your music">
          <header><h3>Choose your music</h3><div class="import-actions"><Button variant="ghost" disabled={busy} onclick={() => selected = Object.fromEntries(choices.map(item => [item.sourceId, true]))}>Select all</Button><Button variant="ghost" disabled={busy} onclick={() => selected = {}}>Clear</Button></div></header>
          {#each choices as item (item.sourceId)}
            <div class="import-choice">
              <label class="import-selection"><Checkbox aria-label={`Select ${item.name}`} checked={!!selected[item.sourceId]} disabled={busy} onCheckedChange={value => selected[item.sourceId] = value === true} />
                <MediaArtwork url={item.artworkUrl} /><span><strong>{item.kind === "liked-songs" ? "Liked Songs" : item.name}</strong><small>{item.count === null ? "Size unknown" : songCount(item.count)}</small></span></label>
              {#if item.kind !== "liked-songs"}<label class="import-sync"><span>Keep in sync</span><span class="settings-toggle"><Checkbox aria-label={`Keep ${item.name} in sync`} checked={!!sync[item.sourceId]} disabled={busy} onCheckedChange={value => sync[item.sourceId] = value === true} /></span></label>{/if}
            </div>
          {/each}
          <div class="import-body">
            {#if cursor}<Button variant="secondary" disabled={busy} onclick={() => act(() => discover(true), "More playlists could not be loaded. Try again.")}>Show more playlists</Button>{/if}
            {#if !choices.length}<Button variant="secondary" disabled={busy} onclick={findMusic}>Find my music</Button>{/if}
            <small>Keep in sync updates the playlist when it changes in Spotify. Otherwise it's copied once.</small>
            <small>Saved albums and followed artists can't be moved yet.</small>
            <Button disabled={busy || !selectedCount} onclick={() => act(async () => {
              await migrations.choose(runId, choices.filter(item => selected[item.sourceId]).map(item => ({ SourceId: item.sourceId, KeepInSync: item.kind === "playlist" && !!sync[item.sourceId] })));
              await refresh();
            }, "Your selection could not be saved. Try again.")}>Start matching ({selectedCount} selected)</Button>
          </div>
        </article>
      {/if}
    {/if}
    {#if status && (step === 1 || interrupted)}
      <article class="settings-card" aria-label="Matching progress"><div class="import-body">
        <h3>{interrupted ? runState === "waiting" ? "Your move needs attention" : "Your move is stopped" : "Matching your music…"}</h3>
        <Progress value={migrationProgress(status)} max={100} aria-label="Music matched" />
        <p>You can leave this page. It keeps going and shows in <a href="#/activity">Activity</a>.</p>
        {#each collections as item}
          <div><strong>{item.collection.name}</strong><small>{collectionStateLabel(interrupted && item.collection.state !== "applied" ? runState : item.collection.state)} · {item.counts.accepted} of {item.counts.selected} found</small>
            {#if item.collection.error}<p>{migrationError(item.collection.error)}</p>{/if}</div>
        {/each}
        <div class="import-actions">
          {#if interrupted}<Button variant="secondary" disabled={busy} onclick={() => command("resume")}>Try again</Button>{/if}
          {#if runState !== "cancelled"}<Button variant="ghost" disabled={busy} onclick={() => confirmStop = true}>Stop moving</Button>{/if}
        </div>
      </div></article>
    {/if}
    {#if status && runState === "applying"}
      <article class="settings-card"><div class="import-body"><h3>Adding playlists and hearts…</h3><Progress value={null} aria-label="Adding music" /><div class="import-actions"><Button variant="ghost" disabled={busy} onclick={() => confirmStop = true}>Stop moving</Button></div></div></article>
    {/if}
    {#if status && runState === "completed"}
      <article class="settings-card" aria-label="Music moved">
        <header><h3>Your music is in Allstarr</h3><ActivityOutcome state="succeeded" label="Moved" /></header>
        <div class="import-body">
        {#each collections as item}
          <p>{item.collection.name} — {receiptCounts[item.id] === undefined ? "Checking songs added…" : addedCountLine(receiptCounts[item.id], item.collection.kind)}{item.collection.keepInSync ? " · kept in sync" : ""}</p>
        {/each}
        {#if remaining}<p>{remainingCountLine(remaining)}</p><Button variant="secondary" onclick={() => { reviewSection?.scrollIntoView({ behavior: "smooth", block: "start" }); reviewSection?.focus({ preventScroll: true }); }}>Review them</Button>{/if}
        <small>If you edit an imported playlist in your music app, Allstarr keeps your edits in your own copy.</small>
        <div class="import-actions"><Button href="#/library/playlists">Open playlists</Button><Button variant="secondary" disabled={busy || reading} onclick={fresh}>Move more music</Button></div>
      </div></article>
    {/if}
    {#if status && reviewAvailable}
      <section bind:this={reviewSection} tabindex="-1" aria-label="Review your music" class="import-review">
        <div class="import-stats">
          {#each [{ label: "Ready", value: status.total.accepted, detail: `${status.total.local} in your library · ${status.total.streamed} will stream` }, { label: "Needs a look", value: status.total.review }, { label: "Not found", value: status.total.unmatched }, { label: "Can't be moved", value: status.total.unsupported }] as stat}
            <article class="settings-card import-stat"><small>{stat.label}</small><strong>{stat.value}</strong>{#if stat.detail}<small>{stat.detail}</small>{/if}</article>
          {/each}
        </div>
        {#each collections as item (item.id)}
          <details class="settings-card" open={expanded[item.id] ?? false} ontoggle={event => {
            expanded[item.id] = event.currentTarget.open;
            if (event.currentTarget.open && !pages[item.id]) void loadPage(item.id);
          }}>
            <summary><strong>{item.collection.name}</strong><small>{collectionCountsLine(item.counts)}</small></summary>
            {#if !pages[item.id]}<div class="import-body"><Skeleton style="height:44px;width:100%" aria-label="Loading songs" /></div>{/if}
            {#each (pages[item.id] ?? []).filter(needsReview) as song (song.sourceEntryId)}
              <div class="import-song"><MediaArtwork url={song.metadata.artworkUrl} /><div><strong>{song.metadata.title ?? "Untitled song"}</strong><small>{song.metadata.artists?.join(", ") || "Unknown artist"}</small><small>{reviewReason(song)}</small></div>
                <Button variant="secondary" aria-label={`Fix ${song.metadata.title ?? "song"}`} disabled={matchLoading || busy} onclick={event => fix(song, event.currentTarget)}>Fix</Button></div>
            {/each}
            <div class="import-body">
              {#if pages[item.id] && !pages[item.id].some(needsReview)}<p>No songs need a look on this page.</p>{/if}
              {#if more[item.id]}<Button variant="secondary" disabled={pageBusy[item.id]} onclick={() => loadPage(item.id, true)}>Show more</Button>{/if}
              {#if pages[item.id] && pageBusy[item.id]}<small>Loading songs…</small>{/if}
            </div>
          </details>
        {/each}
        <div class="import-body">
          <div class="import-actions">
            {#if runState !== "completed" || pendingCount > 0}<Button disabled={busy || reading || !status.total.accepted} onclick={() => command("apply")}>{moveCountLine(runState === "completed" ? pendingCount : status.total.accepted, runState === "completed")}</Button>{/if}
            {#if runState === "review"}<Button variant="ghost" disabled={busy} onclick={() => confirmStop = true}>Stop moving</Button>{/if}
          </div>
          <small>Songs that need a look stay here. You can fix them and move them later.</small>
        </div>
      </section>
    {/if}
    {#if interrupted}<Button variant="secondary" disabled={busy || reading} onclick={fresh}>Move more music</Button>{/if}
  {/if}
</section>
<ConfirmDialog bind:open={confirmStop} title="Stop moving your music?" description="Everything already moved stays." confirmLabel="Stop moving" closeOnConfirm={false} disabled={busy} error={error} onConfirm={() => command("cancel")} />
<MatchDialog bind:open={matchOpen} match={selectedMatch} {providers} {backend} showReject={false} onSaved={matchSaved} />

<style>
  .import-page { max-width: 64rem; }
  .import-page h3, .import-page p { margin: 0; }
  .import-page small { display: block; color: var(--color-ink-muted); font-size: var(--text-sm); }
  .import-steps { display: flex; flex-wrap: wrap; gap: var(--space-2); margin: 0; padding: 0; list-style: none; }
  .import-steps li { display: flex; align-items: center; gap: var(--space-2); min-height: 44px; color: var(--color-ink-muted); padding: 0 var(--space-3); border-radius: 999px; font-size: var(--text-sm); }
  .import-steps .current { color: var(--md-sys-color-on-secondary-container); background: var(--md-sys-color-secondary-container); font-weight: 600; }
  .import-body { display: grid; justify-items: start; gap: var(--space-4); padding: var(--space-5) var(--space-6); min-width: 0; }
  .import-body > :global(.select-root), .import-body > :global([data-slot="progress"]) { width: 100%; }
  .import-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .import-choice, .import-song { display: flex; min-width: 0; min-height: 44px; align-items: center; gap: var(--space-3); padding: var(--space-2) var(--space-6); border-bottom: 1px solid var(--color-edge); }
  .import-selection { display: flex; flex: 1; min-width: 0; min-height: 44px; align-items: center; gap: var(--space-4); cursor: pointer; }
  .import-selection > span, .import-song > div { min-width: 0; overflow-wrap: anywhere; }
  .import-song > div { flex: 1; }
  .import-sync { display: flex; align-items: center; min-height: 44px; gap: var(--space-2); font-size: var(--text-sm); color: var(--color-ink-muted); }
  .import-page :global(.media-art) { width: 44px; height: 44px; flex: none; }
  .import-page :global([data-slot="checkbox"]) { box-sizing: border-box; width: 44px; height: 44px; padding: 0; margin: 0; border: 0; background: transparent; outline: 0; }
  .import-page :global([data-slot="checkbox"]:focus-visible) { outline: 2px solid var(--md-sys-color-primary); }
  .import-page :global([data-slot="checkbox-indicator"]) { width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px; }
  .import-page :global([data-checked] [data-slot="checkbox-indicator"]) { border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
  .import-review { display: grid; gap: var(--space-4); scroll-margin-top: var(--space-6); }
  .import-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: var(--space-3); }
  .import-stat { display: grid; align-content: start; gap: var(--space-2); padding: var(--space-4); }
  .import-stat strong { font-size: clamp(1.5rem, 3vw, 2rem); font-weight: 600; line-height: 1.2; }
  summary { padding: var(--space-4) var(--space-6); cursor: pointer; min-height: 44px; overflow-wrap: anywhere; }
  @media (max-width: 40rem) {
    .import-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .import-choice { flex-wrap: wrap; }
    .import-selection { flex-basis: 100%; }
    .import-sync { margin-left: auto; }
    .import-body, .import-choice, .import-song, summary { padding-left: var(--space-4); padding-right: var(--space-4); }
  }
</style>
