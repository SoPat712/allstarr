<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { countLabel } from "$lib/count-label";
  import { newUuid } from "$lib/uuid";
  import { onMount, tick } from "svelte";
  import { Check } from "@lucide/svelte";
  import { ApiError, home, matchReview, migrations, sources, type MigrationChoice, type MigrationReviewItem, type MigrationResourceCandidate,
    type MigrationStatus, type MatchReviewItem, type ProviderAccount, type ProviderDefinition } from "$lib/api";
  import { collectionCountsLine, collectionStateLabel, migrationError, migrationProgress, migrationStep, needsReview, reviewReason, addedCountLine, remainingCountLine, moveCountLine, readyToMove, collectionChoiceOrder, collectionCount, collectionName, unavailableCollectionsLine, resourceDetails } from "$lib/migration";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Progress } from "$lib/components/ui/progress";
  import { Switch } from "$lib/components/ui/switch";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ActivityOutcome from "$lib/components/ActivityOutcome.svelte";


  let { currentUserId, initialAccount = "", initialRun = "" }: { currentUserId: string; initialAccount?: string; initialRun?: string } = $props();
  let accounts = $state<ProviderAccount[]>([]);
  let providerId = $state("spotify");
  let accountId = $state("");
  let runId = $state("");
  let status = $state<MigrationStatus | null>(null);
  let choices = $state<MigrationChoice[]>([]);
  let unsupported = $state<{ kind: string }[]>([]);
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
  let error = $state(""), reconnect = $state(false), sourceBusy = $state(false);
  let confirmStop = $state(false);
  let resourceOpen = $state(false);
  let resourceDialog = $state<HTMLDivElement | null>(null);
  let resourceSaving = $state(false);
  let resourceError = $state("");
  let selectedResource = $state<MigrationReviewItem | null>(null);
  let matchOpen = $state(false);
  let LoadedMatchDialog = $state<typeof import("$lib/components/MatchDialog.svelte").default | null>(null);
  let makePlaylists = $state(true);
  let accountVerified = $state(false);
  let previewCursors: (string | undefined)[] = [];
  const kinds = $derived([
    { id: "liked-songs", label: providerId === "apple-musickit" ? "Library songs" : "Liked Songs", count: choices.filter(item => item.kind === "liked-songs").reduce((sum, item) => sum + (item.count ?? 0), 0) },
    { id: "playlist", label: "Playlists", count: choices.filter(item => item.kind === "playlist").length },
    { id: "saved-albums", label: "Albums", count: choices.filter(item => item.kind === "saved-albums").reduce((sum, item) => sum + (item.count ?? 0), 0) },
    { id: "followed-artists", label: "Artists", count: choices.filter(item => item.kind === "followed-artists").reduce((sum, item) => sum + (item.count ?? 0), 0) },
  ]);
  const selectedSongs = $derived(choices.filter(item => selected[item.sourceId] && ["liked-songs", "playlist"].includes(item.kind)).reduce((sum, item) => sum + (item.count ?? 0), 0));
  const selectionSummary = $derived(choices.some(item => selected[item.sourceId] &&
    (item.count == null || !["liked-songs", "playlist"].includes(item.kind)))
    ? "Ready to match your selected music" : `${countLabel(selectedSongs, "song")} to match`);
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
  let selectionVersion = 0;
  const providerOptions = [{ id: "apple-musickit", label: "Apple Music" }, { id: "spotify", label: "Spotify" }];
  const providerAccounts = $derived(accounts.filter(account => account.providerId === providerId));
  const providerName = $derived(providerId === "apple-musickit" ? "Apple Music" : "Spotify");
  const step = $derived(migrationStep(status));
  const runState = $derived(status?.run.state ?? "choosing");
  const interrupted = $derived(runState === "waiting" || runState === "cancelled");
  const collections = $derived(status?.collections.filter(item => item.collection.state !== "skipped") ?? []);
  const hasResources = $derived(collections.some(item => ["saved-albums", "followed-artists"].includes(item.collection.kind)));
  const selectedCount = $derived(choices.filter(item => selected[item.sourceId]).length);
  const remaining = $derived((status?.total.review ?? 0) + (status?.total.unmatched ?? 0));
  const reviewAvailable = $derived(status && !status.provisional && ["review", "completed"].includes(runState));
  const storageKey = () => `allstarr:migration:${accountId}`;
  function rememberProvider() {
    try { localStorage.setItem("allstarr:migration-provider", providerId); } catch { /* Storage is optional. */ }
  }

  function remember() {
    try { if (runId) localStorage.setItem(storageKey(), runId); else localStorage.removeItem(storageKey()); } catch { /* Storage is optional. */ }
  }
  async function discover(loadMore = false) {
    const id = runId;
    const version = selectionVersion;
    const nextCursor = loadMore ? cursor ?? undefined : undefined;
    const next = id ? await migrations.discover(id, nextCursor) : await migrations.preview(accountId, nextCursor);
    if (disposed || id !== runId || version !== selectionVersion) return;
    accountVerified = true;
    previewCursors = loadMore ? [...previewCursors, nextCursor] : [undefined];
    const previous = loadMore ? choices : [];
    choices = [...previous, ...next.items.filter(item => !previous.some(old => old.sourceId === item.sourceId))]
      .sort((a, b) => collectionChoiceOrder(a.kind) - collectionChoiceOrder(b.kind));
    unsupported = next.unsupported ?? [];
    cursor = next.nextCursor;
  }
  async function loadPage(id: string, loadMore = false) {
    if (pageBusy[id]) return;
    pageBusy[id] = true;
    const currentRun = runId;
    const version = selectionVersion;
    try {
      const previous = pages[id] ?? [];
      const next = await migrations.review(currentRun, id, loadMore ? previous.length : 0);
      if (disposed || currentRun !== runId || version !== selectionVersion) return;
      pages[id] = loadMore ? [...previous, ...next] : next;
      more[id] = next.length === 50;
    } catch { if (version === selectionVersion) error = "This music could not be loaded. Try opening this collection again."; }
    finally { if (version === selectionVersion) pageBusy[id] = false; }
  }
  async function loadReceipts(next: MigrationStatus) {
    const version = selectionVersion;
    let pending = 0;
    const counts: Record<string, number> = {};
    for (const item of next.collections.filter(item => item.collection.state === "applied")) {
      if (item.collection.kind === "playlist" && next.run.makePlaylists === false) {
        counts[item.id] = item.counts.accepted;
        continue;
      }
      let added = 0;
      for (let offset = 0; offset < item.collection.read; offset += 50) {
        const rows = await migrations.review(next.id, item.id, offset);
        if (disposed || next.id !== runId || version !== selectionVersion) return;
        added += rows.filter(row => row.resource ? row.resource.applied : row.receipt?.outcome === 2).length;
        pending += rows.filter(readyToMove).length;
        if (rows.length < 50) break;
      }
      counts[item.id] = added;
    }
    if (!disposed && version === selectionVersion) { receiptCounts = counts; pendingCount = pending; }
  }
  function noteSourceError(cause: unknown, fallback: string) {
    const code = cause instanceof ApiError ? (cause.details as { code?: string } | null)?.code : undefined;
    reconnect = code === "source_connection";
    sourceBusy = code === "source_busy";
    return (reconnect || sourceBusy) && cause instanceof Error ? cause.message : fallback;
  }
  async function refresh() {
    if (!runId || reading) return;
    const id = runId;
    const version = selectionVersion;
    reading = true;
    try {
      const next = await migrations.status(id, ["review", "completed"].includes(runState));
      if (disposed || runId !== id || version !== selectionVersion) return;
      status = next; accountId = next.run.providerAccountId; providerId = next.run.providerId ?? "spotify";
      makePlaylists = next.run.makePlaylists ?? true;
      remember(); rememberProvider(); error = ""; reconnect = false; sourceBusy = false;
      if (next.run.state === "choosing" && !choices.length) await discover();
      if (!reviewInitialized && !next.provisional && ["review", "completed"].includes(next.run.state)) {
        reviewInitialized = true;
        const first = next.collections.find(item => item.counts.review + item.counts.unmatched > 0);
        if (first) { expanded[first.id] = true; await loadPage(first.id); }
      }
      if (next.provisional) {
        for (const item of next.collections.filter(item => item.counts.review > 0)) await loadPage(item.id);
      }
      if (next.run.state === "completed") await loadReceipts(next);
    } catch (cause) {
      if (disposed || id !== runId || version !== selectionVersion) return;
      if (cause instanceof ApiError && cause.status === 404) {
        runId = ""; status = null; remember(); reconnect = false; sourceBusy = false;
        error = "The saved move is no longer available. You can find your music again.";
      } else {
        reconnect = false;
        sourceBusy = false;
        error = noteSourceError(cause, "Your progress could not be refreshed. Try refreshing again.");
      }
    } finally { if (version === selectionVersion) reading = false; }
  }
  async function load() {
    loading = true; error = "";
    try {
      const result = await sources.accounts();
      if (disposed) return;
      accounts = result.accounts.filter(item => ["spotify", "apple-musickit"].includes(item.providerId) && item.scope === "Personal" && item.enabled && item.ownerUserId === currentUserId);
      let preferred = accounts.find(item => item.id === initialAccount);
      let rememberedProvider = "";
      try { rememberedProvider = localStorage.getItem("allstarr:migration-provider") ?? ""; } catch { /* Storage is optional. */ }
      providerId = preferred?.providerId ?? accounts.find(item => item.providerId === rememberedProvider)?.providerId ?? accounts[0]?.providerId ?? "spotify";
      const candidates = accounts.filter(item => item.providerId === providerId);
      if (!preferred && !initialAccount && !initialRun) {
        try { preferred = candidates.find(item => localStorage.getItem(`allstarr:migration:${item.id}`)); } catch { /* Storage is optional. */ }
      }
      accountId = preferred?.id ?? candidates[0]?.id ?? "";
      if (accountId) rememberProvider();
      runId = initialRun;
      if (!runId && accountId) try { runId = localStorage.getItem(storageKey()) ?? ""; } catch { /* Storage is optional. */ }
      if (runId) await refresh();
      else if (accountId) await findMusic();
    } catch { error = "Your accounts could not be loaded. Try again."; }
    finally { loading = false; }
  }
  async function changeAccount(value: string) {
    if (busy || value === accountId || !accounts.some(item => item.id === value)) return;
    reset(); accountId = value;
    providerId = accounts.find(item => item.id === value)!.providerId; rememberProvider();
    try { runId = localStorage.getItem(storageKey()) ?? ""; } catch { runId = ""; }
    if (runId) await refresh();
    else await findMusic();
  }
  async function changeProvider(value: string) {
    if (busy || value === providerId) return;
    const candidates = accounts.filter(item => item.providerId === value);
    let preferred = candidates[0];
    try { preferred = candidates.find(item => localStorage.getItem(`allstarr:migration:${item.id}`)) ?? preferred; } catch { /* Storage is optional. */ }
    if (preferred) await changeAccount(preferred.id);
    else { reset(); accountId = ""; providerId = value; rememberProvider(); }
  }
  async function act(action: () => Promise<unknown>, fallback: string) {
    if (busy) return;
    busy = true; error = ""; reconnect = false; sourceBusy = false;
    try { await action(); }
    catch (cause) { error = noteSourceError(cause, fallback); }
    finally { busy = false; }
  }
  async function findMusic() {
    if (busy) return;
    busy = true; error = ""; reconnect = false; sourceBusy = false;
    try { await discover(); }
    catch (cause) {
      error = noteSourceError(cause, "Your music could not be found. Try again with this account.");
    } finally { busy = false; }
  }
  function selectKind(kind: string, checked: boolean) {
    selected = { ...selected, ...Object.fromEntries(choices.filter(item => item.kind === kind).map(item => [item.sourceId, checked])) };
  }
  function kindChecked(kind: string) {
    const items = choices.filter(item => item.kind === kind);
    return items.length > 0 && items.every(item => selected[item.sourceId]);
  }
  function kindPartial(kind: string) {
    return !kindChecked(kind) && choices.some(item => item.kind === kind && selected[item.sourceId]);
  }
  async function startImport() {
    if (!selectedCount || !accountId) return;
    await act(async () => {
      const version = selectionVersion;
      const selection = choices.filter(item => selected[item.sourceId]).map(item => ({ SourceId: item.sourceId, KeepInSync: item.kind === "playlist" && !!sync[item.sourceId] }));
      const destinationPlaylists = makePlaylists;
      if (!runId) {
        requestKey ||= newUuid();
        const receipt = await migrations.start(accountId, requestKey);
        if (disposed || version !== selectionVersion) return;
        runId = receipt.id; remember();
      }
      for (const nextCursor of previewCursors) {
        await migrations.discover(runId, nextCursor);
        if (disposed || version !== selectionVersion) return;
      }
      await migrations.choose(runId, selection, destinationPlaylists);
      await refresh();
    }, "Your import could not be started. Try again; your selection is saved here.");
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
    const version = selectionVersion;
    matchTrigger = trigger;
    if (item.resource) {
      selectedResource = item; matchCollection = item.collectionId; resourceError = ""; resourceOpen = true;
      return;
    }
    matchLoading = true; error = "";
    try {
      const match = await matchReview.get(item.externalSnapshotId);
      if (disposed || version !== selectionVersion) return;
      if (!match || match.externalSnapshotId !== item.externalSnapshotId) throw new Error();
      LoadedMatchDialog ??= (await import("$lib/components/MatchDialog.svelte")).default;
      if (disposed || version !== selectionVersion) return;
      selectedMatch = match; matchCollection = item.collectionId; matchOpen = true;
    } catch { if (version === selectionVersion) error = "This song's match could not be opened. Try again."; }
    finally { if (version === selectionVersion) matchLoading = false; }
  }
  async function saveResource(candidate?: MigrationResourceCandidate) {
    if (!selectedResource || resourceSaving) return;
    const version = selectionVersion;
    resourceSaving = true; resourceError = "";
    try {
      await migrations.chooseResource(runId, selectedResource.collectionId, selectedResource.sourceEntryId,
        candidate ? { providerId: candidate.providerId, resourceId: candidate.resourceId } : { skip: true });
      if (disposed || version !== selectionVersion) return;
      await matchSaved();
      resourceOpen = false;
    } catch { if (version === selectionVersion) resourceError = "Your choice could not be saved. Try again."; }
    finally { if (version === selectionVersion) resourceSaving = false; }
  }
  async function matchSaved() {
    const version = selectionVersion;
    const id = runId;
    const collection = matchCollection;
    await refresh();
    if (disposed || version !== selectionVersion) return;
    const loaded = pages[collection]?.length ?? 50;
    const rows: MigrationReviewItem[] = [];
    try {
      for (let offset = 0; offset < loaded; offset += 50) rows.push(...await migrations.review(id, collection, offset));
      if (disposed || version !== selectionVersion) return;
      pages[collection] = rows;
    } catch { if (version === selectionVersion) error = "The match was saved. Refresh to see your updated music."; }
    await tick(); (matchTrigger?.isConnected ? matchTrigger : reviewSection)?.focus({ preventScroll: true });
  }
  function reset() {
    selectionVersion += 1;
    runId = ""; status = null; choices = []; selected = {}; sync = {}; pages = {}; more = {}; expanded = {}; pageBusy = {};
    unsupported = []; receiptCounts = {}; pendingCount = 0; reviewInitialized = false; cursor = null; requestKey = ""; error = ""; reading = false;
    previewCursors = []; accountVerified = false; makePlaylists = true;
    confirmStop = false; resourceOpen = false; resourceSaving = false; resourceError = ""; selectedResource = null;
    matchOpen = false; matchLoading = false; selectedMatch = null; matchCollection = ""; matchTrigger = undefined;
  }
  function fresh() {
    reset(); remember(); void findMusic();
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

<section class="settings-content import-page" aria-label="Move your music collection">
  <header class="import-heading"><div><h2>Bring your music</h2></div>
    {#if runId}<RefreshButton label="Refresh import progress" onrefresh={refresh} />{/if}
  </header>
  <div class="import-toolbar">
    {#if !loading}
      <SegmentedNav variant="segments" items={providerOptions} active={providerId} label="Music service" onchange={changeProvider} />
    {/if}
    <PageStatus label="Import status" items={[{ label: "Nothing changes in Apple Music or Spotify." }]} />
  </div>
  {#if loading}<RowsSkeleton count={5} />
  {:else}
    {#if error}<ErrorState title={accounts.length ? "Your import needs attention" : "Your accounts could not load"} detail={error} onretry={!accounts.length ? load : runId && runState !== "choosing" ? refresh : findMusic} />
      {#if reconnect}<div class="import-actions"><Button variant="outline" href="#/sources/accounts">Reconnect in Sources</Button></div>{/if}{/if}
    {#if !providerAccounts.length && !runId && !error}
      <EmptyState title={`Connect ${providerName} first.`}>{#snippet icon()}<Check size={24} />{/snippet}{#snippet action()}<Button href="#/integrations/services">Connect an account</Button>{/snippet}</EmptyState>
    {:else if accounts.length || runId}
      <ol class="import-steps" class:running={step > 0} aria-label="Import progress">
        <li class="import-step import-account" class:done={accountVerified || !!runId}>
          <span class="import-step-marker">{#if accountVerified || runId}<Check size={16} aria-hidden="true" />{:else}1{/if}</span>
          <div class="import-step-content">
            <div class="import-account-heading"><div><h3>Account</h3><small>{accountVerified ? "Account verified" : runId ? "Connected account" : reconnect ? "Needs reconnecting" : sourceBusy ? "Busy · try again soon" : error && !busy ? "Not checked" : "Checking your account…"}</small></div>
              {#if providerAccounts.length}<SelectField value={accountId} options={providerAccounts.map(item => ({ value: item.id, label: item.displayName }))} label={`${providerName} account`} disabled={busy} onchange={changeAccount} />{/if}
            </div>
          </div>
        </li>
        <li class="import-step" class:current={step === 0} class:done={step > 0} aria-current={step === 0 ? "step" : undefined}>
          <span class="import-step-marker">{#if step > 0}<Check size={16} aria-hidden="true" />{:else}2{/if}</span>
          <article class="import-step-content import-section" aria-label="Choose your music">
            <header><h3>Choose what to bring</h3>{#if step > 0}<small>{countLabel(collections.length, "collection")} selected</small>{/if}</header>
            {#if !status || runState === "choosing"}
              <section class="import-kind-counts" aria-label="Counts by kind">
                {#each kinds as kind}
                  <label class="import-kind" class:selected={kindChecked(kind.id)}>
                    <Checkbox aria-label={`Bring ${kind.label}`} checked={kindChecked(kind.id)} indeterminate={kindPartial(kind.id)} disabled={busy || !choices.some(item => item.kind === kind.id)} onCheckedChange={value => selectKind(kind.id, value === true)} />
                    <strong>{unsupported.some(item => item.kind === kind.id) ? "—" : choices.some(item => item.kind === kind.id && item.count == null) ? "?" : kind.count.toLocaleString()}</strong><small>{kind.label}</small>
                  </label>
                {/each}
              </section>
              {#if cursor}<p class="import-count-note">Counts so far. Show more playlists to include the rest.</p>{/if}
              <div class="import-settings">
                <div class="import-setting"><div><label for="import-local-only"><strong>Only songs I can play from my library</strong></label><small id="import-local-only-detail">Local-only collection imports are not available yet.</small></div><Switch id="import-local-only" aria-label="Only songs I can play from my library" aria-describedby="import-local-only-detail" checked={false} disabled /></div>
                <div class="import-setting"><div><label for="import-make-playlists"><strong>Make playlists in {backend}</strong></label><small id="import-playlists-detail">{makePlaylists ? "Each imported playlist appears in your apps" : "Imported playlists stay in Allstarr"}</small></div><Switch id="import-make-playlists" aria-label={`Make playlists in ${backend}`} aria-describedby="import-playlists-detail" bind:checked={makePlaylists} disabled={busy} /></div>
              </div>
              <AdvancedFold class="import-collection-details" label="Choose individual collections">
                <div class="import-actions"><Button variant="text" disabled={busy} onclick={() => selected = Object.fromEntries(choices.map(item => [item.sourceId, true]))}>Select all</Button><Button variant="text" disabled={busy} onclick={() => selected = {}}>Clear</Button></div>
                {#each choices as item (item.sourceId)}
                  <div class="import-choice">
                    <label class="import-selection"><Checkbox aria-label={`Select ${collectionName(item.kind, item.name, providerId)}`} checked={!!selected[item.sourceId]} disabled={busy} onCheckedChange={value => selected[item.sourceId] = value === true} />
                      <MediaArtwork url={item.artworkUrl} /><span><strong>{collectionName(item.kind, item.name, providerId)}</strong><small>{item.count === null ? "Size unknown" : collectionCount(item.count, item.kind)}</small>{#if item.kind === "liked-songs"}<small>Become hearts</small>{/if}</span></label>
                    {#if item.kind === "playlist"}<label class="import-sync"><span>Keep in sync</span><Checkbox aria-label={`Keep ${item.name} in sync`} checked={!!sync[item.sourceId]} disabled={busy} onCheckedChange={value => sync[item.sourceId] = value === true} /></label>{/if}
                  </div>
                {/each}
              </AdvancedFold>
              {#if cursor}<Button variant="secondary" disabled={busy} onclick={() => act(() => discover(true), "More playlists could not be loaded. Try again.")}>Show more playlists</Button>{/if}
              {#if !choices.length && !busy && !error}<EmptyState title="No collections found" description="Try refreshing your connected account." />{/if}
              {#if unavailableCollectionsLine(unsupported, providerId)}<small>{unavailableCollectionsLine(unsupported, providerId)}</small>{/if}
            {/if}
          </article>
        </li>
        <li class="import-step" class:current={step === 1 || step === 2} class:done={step === 3} aria-current={step === 1 || step === 2 ? "step" : undefined}>
          <span class="import-step-marker">{#if step === 3}<Check size={16} aria-hidden="true" />{:else}3{/if}</span><div class="import-step-content"><h3>Match</h3><small>Allstarr finds each song; you review only the unsure ones</small></div>
        </li>
        <li class="import-step" class:current={step === 3} aria-current={step === 3 ? "step" : undefined}><span class="import-step-marker">4</span><div class="import-step-content"><h3>Done</h3><small>See what came over and what didn't</small></div></li>
      </ol>
      {#if !status || runState === "choosing"}
        <div class="import-start"><p>{selectionSummary}{cursor ? " · more playlists available" : ""}. This runs in the background; you can leave this page.</p><Button disabled={busy || !selectedCount || !accountVerified} onclick={startImport}>{busy ? "Starting import…" : "Start import"}</Button></div>
      {/if}
    {/if}
    {#if status && (step === 1 || interrupted)}
      <article class="import-section" aria-label="Matching progress"><div class="import-body">
        <h3>{interrupted ? runState === "waiting" ? "Your move needs attention" : "Your move is stopped" : "Matching your music…"}</h3>
        <PageStatus label="Matching status" items={[{ label: `${countLabel(status.total.selected, "song")} checked`, emphasis: true }, { label: `${status.total.accepted} found · ${status.total.review} unsure · ${status.total.unmatched} missing so far` }]} />
        <Progress value={migrationProgress(status)} max={100} aria-label="Music matched" />
        <p>You can leave this page. It keeps going and shows in <a href="#/activity">Activity</a>.</p>
        {#each collections as item}
          <div><strong>{collectionName(item.collection.kind, item.collection.name, providerId)}</strong><small>{collectionStateLabel(interrupted && item.collection.state !== "applied" ? runState : item.collection.state)} · {item.counts.accepted} of {item.counts.selected} found</small>
            {#if item.collection.error}<ErrorState title="This collection needs attention" detail={migrationError(item.collection.error, status.run.providerId)} />{/if}</div>
        {/each}
        {#if status.total.review > 0}<section class="import-unsure" aria-label="Unsure so far"><h3>Unsure so far</h3>
          {#each collections as collection}{#each (pages[collection.id] ?? []).filter(song => Boolean(song.preview) && needsReview(song)) as song (song.sourceEntryId)}
            <ItemRow title={song.metadata.title ?? "Untitled"} subtitle={`${song.metadata.artists?.join(", ") ?? ""} · ${reviewReason(song)}`} actionLabel={`Fix ${song.metadata.title ?? "song"}`} onclick={event => void fix(song, event.currentTarget as HTMLElement)}>{#snippet leading()}<MediaArtwork url={song.metadata.artworkUrl} />{/snippet}</ItemRow>
          {/each}{/each}<small>Matches appear here as each collection is checked.</small></section>{/if}
        <div class="import-actions">
          {#if interrupted}<Button variant="secondary" disabled={busy} onclick={() => command("resume")}>Try again</Button>{/if}
          {#if runState !== "cancelled"}<Button variant="outline" disabled={busy} onclick={() => confirmStop = true}>Stop moving</Button>{/if}
        </div>
      </div></article>
    {/if}
    {#if status && runState === "applying"}
      <article class="import-section"><div class="import-body"><h3>{hasResources ? "Adding your music…" : "Adding playlists and hearts…"}</h3><Progress value={null} aria-label="Adding music" /><div class="import-actions"><Button variant="outline" disabled={busy} onclick={() => confirmStop = true}>Stop moving</Button></div></div></article>
    {/if}
    {#if status && runState === "completed"}
      <article class="import-section" aria-label="Music moved">
        <header><h3>Your music is in Allstarr</h3><ActivityOutcome state="succeeded" label="Moved" /></header>
        <div class="import-body">
        {#each collections as item}
          <p>{collectionName(item.collection.kind, item.collection.name, providerId)} — {receiptCounts[item.id] === undefined ? "Checking music added…" : addedCountLine(receiptCounts[item.id], item.collection.kind)}{item.collection.keepInSync ? " · kept in sync" : ""}</p>
        {/each}
        {#if remaining}<p>{remainingCountLine(remaining, hasResources)}</p><Button variant="secondary" onclick={() => { reviewSection?.scrollIntoView({ behavior: "smooth", block: "start" }); reviewSection?.focus({ preventScroll: true }); }}>Review them</Button>{/if}
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
            <summary><strong>{collectionName(item.collection.kind, item.collection.name, providerId)}</strong><small>{collectionCountsLine(item.counts)}</small></summary>
            {#if !pages[item.id]}<div class="import-body"><RowsSkeleton count={3} /></div>{/if}
            {#each (pages[item.id] ?? []).filter(needsReview) as song (song.sourceEntryId)}
              <div class="import-song"><MediaArtwork url={song.metadata.artworkUrl} /><div><strong>{song.metadata.title ?? "Untitled"}</strong>
                {#if song.metadata.artists?.length}<small>{song.metadata.artists.join(", ")}</small>{/if}
                {#if song.resource && resourceDetails(song.metadata)}<small>{resourceDetails(song.metadata)}</small>{/if}<small>{reviewReason(song)}</small></div>
                <Button variant="secondary" aria-label={`${song.resource ? "Choose" : "Fix"} ${song.metadata.title ?? "song"}`} disabled={matchLoading || busy || resourceSaving} onclick={event => fix(song, event.currentTarget)}>{song.resource ? "Choose" : "Fix"}</Button></div>
            {/each}
            <div class="import-body">
              {#if pages[item.id] && !pages[item.id].some(needsReview)}<p>Nothing needs a look on this page.</p>{/if}
              {#if more[item.id]}<Button variant="secondary" disabled={pageBusy[item.id]} onclick={() => loadPage(item.id, true)}>Show more</Button>{/if}
              {#if pages[item.id] && pageBusy[item.id]}<small>Loading music…</small>{/if}
            </div>
          </details>
        {/each}
        <div class="import-body">
          <div class="import-actions">
            {#if runState !== "completed" || pendingCount > 0}<Button disabled={busy || reading || !status.total.accepted} onclick={() => command("apply")}>{moveCountLine(runState === "completed" ? pendingCount : status.total.accepted, runState === "completed", hasResources)}</Button>{/if}
            {#if runState === "review"}<Button variant="outline" disabled={busy} onclick={() => confirmStop = true}>Stop moving</Button>{/if}
          </div>
          <small>Music that needs a look stays here. You can choose matches and move it later.</small>
        </div>
      </section>
    {/if}
    {#if interrupted}<Button variant="secondary" disabled={busy || reading} onclick={fresh}>Move more music</Button>{/if}
  {/if}
</section>
<ConfirmDialog bind:open={confirmStop} title="Stop moving your music?" description="Everything already moved stays." confirmLabel="Stop moving" closeOnConfirm={false} disabled={busy} error={error} onConfirm={() => command("cancel")} />
<Dialog.Root bind:open={resourceOpen}>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay match-dialog-overlay" />
    <Dialog.Content bind:ref={resourceDialog} class="match-dialog" onOpenAutoFocus={event => {
      event.preventDefault(); resourceDialog?.querySelector<HTMLButtonElement>("button")?.focus({ preventScroll: true });
    }} onCloseAutoFocus={event => {
      event.preventDefault(); (matchTrigger?.isConnected ? matchTrigger : reviewSection)?.focus({ preventScroll: true });
    }}>
      {#if selectedResource?.resource}
        <header><div><Dialog.Title>{selectedResource.resource.kind === "saved-albums" ? "Choose an album" : "Choose an artist"}</Dialog.Title>
          <Dialog.Description>{selectedResource.metadata.title}{selectedResource.metadata.artists?.length ? ` · ${selectedResource.metadata.artists.join(", ")}` : ""}</Dialog.Description></div></header>
        <div class="import-resource-options">
          {#each selectedResource.resource.candidates as candidate (`${candidate.providerId}:${candidate.resourceId}`)}
            <Button variant="text" size="sm" class="import-resource-option" aria-label={`Choose ${candidate.title}${candidate.artist ? ` by ${candidate.artist}` : ""}`} disabled={resourceSaving} onclick={() => saveResource(candidate)}>
              <MediaArtwork url={candidate.artworkUrl} />
              <span><strong>{candidate.title}</strong>{#if candidate.artist}<small>{candidate.artist}</small>{/if}{#if resourceDetails(candidate)}<small>{resourceDetails(candidate)}</small>{/if}</span>
              <span class="import-resource-action">Choose</span>
            </Button>
          {/each}
          {#if !selectedResource.resource.candidates.length}<p>No matches found. You can skip this one and keep moving your other music.</p>{/if}
          {#if resourceError}<ErrorState title="Your choice could not be saved" detail={resourceError} />{/if}
        </div>
        <footer class="dialog-actions"><Dialog.Close class={buttonVariants({ variant: "text" })} disabled={resourceSaving}>Cancel</Dialog.Close><Button variant="outline" disabled={resourceSaving} onclick={() => saveResource()}>Skip this one</Button></footer>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
{#if LoadedMatchDialog}<LoadedMatchDialog bind:open={matchOpen} match={selectedMatch} {providers} {backend} showReject={false} onSaved={matchSaved} />{/if}

<style>
  :global(.import-collection-details) > :not(summary) { margin-inline: var(--space-4); }
  .import-heading, .import-section > header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  .import-heading h2 { margin: 0; font-size: 1.5rem; font-weight: 600; }
  .import-section { min-width: 0; }
  .import-section > header { padding-bottom: var(--space-3); }
  .import-count-note { font-size: var(--text-xs); color: var(--color-ink-muted); }
  .import-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-4); }
  .import-kind-counts { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: var(--space-3); }
  .import-kind { display: grid; gap: var(--space-1); align-content: start; padding: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-panel); cursor: pointer; min-width: 0; }
  .import-kind.selected { border-color: var(--color-signal); }
  .import-kind strong { font-size: 1.5rem; line-height: 1.2; font-variant-numeric: tabular-nums; }
  .import-kind small { overflow-wrap: anywhere; }
  .import-settings { border: 1px solid var(--color-edge); border-radius: var(--radius-md); overflow: hidden; }
  .import-setting { display: flex; align-items: center; justify-content: space-between; gap: var(--space-4); padding: var(--space-4); }
  .import-setting + .import-setting { border-top: 1px solid var(--color-edge); }
  .import-setting > div { min-width: 0; overflow-wrap: anywhere; }
  .import-setting strong { font-size: var(--text-sm); font-weight: 600; }
  .import-setting :global(.ui-switch) { flex: none; }
  .import-start { display: flex; align-items: center; justify-content: space-between; gap: var(--space-5); }
  .import-start p { max-width: 42rem; color: var(--color-ink-muted); font-size: var(--text-sm); }
  .import-start :global(button) { flex: none; }
  .import-unsure { width: 100%; min-width: 0; }
  .import-unsure h3 { margin-bottom: var(--space-3); }
  @media (max-width: 600px) { .import-kind-counts { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-2); } }

  .import-resource-options { display: grid; gap: var(--space-3); padding: var(--space-4); }
  :global(.import-resource-option) { display: flex; width: 100%; height: auto; min-height: 44px; justify-content: start; gap: var(--space-3); padding: var(--space-3); white-space: normal; text-align: left; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); }
  :global(.import-resource-option > span:not(.media-art)) { display: grid; min-width: 0; overflow-wrap: anywhere; }
  :global(.import-resource-option .media-art) { width: 44px; height: 44px; flex: none; }
  :global(.import-resource-option small) { color: var(--color-ink-muted); font-size: var(--text-sm); font-weight: 400; }
  :global(.import-resource-option .import-resource-action) { margin-left: auto; }
  .import-page { max-width: none; padding: var(--space-4) 0; }
  .import-section > .import-body { padding-inline: 0; }
  .import-page h3, .import-page p { margin: 0; }
  .import-page small { display: block; color: var(--color-ink-muted); font-size: var(--text-sm); }
  .import-steps { margin: 0; padding: 0; list-style: none; background: var(--color-panel); border: 1px solid var(--color-edge); border-radius: var(--radius-lg); overflow: hidden; }
  .import-step { display: flex; align-items: start; gap: var(--space-4); min-width: 0; padding: var(--space-5); }
  .import-step + .import-step { border-top: 1px solid var(--color-edge); }
  .import-step-marker { display: grid; place-content: center; flex: none; width: 28px; height: 28px; margin-top: 1px; border: 1px solid var(--color-edge); border-radius: 50%; font-size: var(--text-sm); color: var(--color-ink-muted); font-variant-numeric: tabular-nums; }
  .import-step.current > .import-step-marker { background: var(--color-signal); border-color: var(--color-signal); color: var(--color-on-signal); }
  .import-step.done > .import-step-marker { color: var(--color-signal); border-color: var(--color-signal); }
  .import-step-content { display: grid; gap: var(--space-3); min-width: 0; flex: 1; }
  .import-step-content h3 { font-size: var(--text-md); font-weight: 600; }
  .import-step-content > header { padding-bottom: 0; }
  .import-account-heading { display: flex; align-items: center; justify-content: space-between; gap: var(--space-5); }
  .import-account-heading > div { min-width: 0; }
  .import-account-heading :global(.select-root) { width: min(20rem, 52%); min-width: 0; }
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
    .import-page { padding: var(--space-3) 0; }
    .import-step { gap: var(--space-2); padding: var(--space-3); }
    .import-step-marker { width: 24px; height: 24px; font-size: var(--text-xs); }
    .import-account-heading { flex-wrap: wrap; gap: var(--space-2); }
    .import-account-heading :global(.select-root) { width: 100%; }
    .import-setting { gap: var(--space-3); padding: var(--space-3); }
    .import-start { flex-wrap: wrap; gap: var(--space-3); }
    .import-start :global(button) { width: 100%; }
    .import-steps.running .import-step:not(.current) { display: none; }
    .import-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .import-choice { flex-wrap: wrap; }
    .import-selection { flex-basis: 100%; }
    .import-sync { margin-left: auto; }
    .import-body, .import-choice, .import-song, summary { padding-left: var(--space-4); padding-right: var(--space-4); }
  }
</style>
