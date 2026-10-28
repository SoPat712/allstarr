<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import {
    ArrowRight,
    Ban,
    ChevronDown,
    CircleQuestionMark,
    Clock3,
    Hand,
    List,
    MoreHorizontal,
    ScanSearch,
    Sparkles,
  } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    home,
    matchReview,
    type ManualMatchAuthority,
    type MatchAuthorityScope,
    type MatchReviewItem,
    type MatchReviewResponse,
    type ProviderDefinition,
  } from "$lib/api";
  import {
    allowedAuthorityScopes,
    authorityForScope,
    authorityScopeOf,
    canEditAuthority,
    canEditAuthorityScope,
    expectedAuthorityForScope,
    manualMatchAuthority,
  } from "$lib/matching-api";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import { reportHref } from "$lib/support";
  import BulkRematchDialog from "$lib/components/BulkRematchDialog.svelte";
  import ArtworkSimilarity from "$lib/shelved/ArtworkSimilarity.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import {
    candidateResolution,
    currentTarget,
    isAttention,
    playableProviderIds,
    percent,
    reviewStateLabel,
    scoreComponents,
  } from "$lib/mappings";
  import { formatDuration } from "$lib/playlists";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { relativeTime } from "$lib/activity";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  type DestructiveAction =
    | { kind: "reject"; match: MatchReviewItem; authorityScope: MatchAuthorityScope }
    | {
        kind: "manual_delete" | "manual_rematch";
        match: MatchReviewItem;
        authority: ManualMatchAuthority;
      };
  type MappingView =
    | "all"
    | "manual"
    | "rejected"
    | "automatic"
    | "tentative"
    | "review"
    | "unresolved";

  const viewFilters: Record<MappingView, string> = {
    all: "",
    manual: "manual_matches",
    rejected: "manual_rejections",
    automatic: "automatic",
    tentative: "suggested",
    review: "ambiguous",
    unresolved: "unresolved",
  };

  let { initialSearch = "", initialReview = "" }: { initialSearch?: string; initialReview?: string } = $props();

  let data = $state<MatchReviewResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let backend = $state("Local library");
  let stateFilter = $state(viewFilters.review);
  let view = $state<MappingView>("review");
  let searchInput = $state("");
  let search = $state("");
  let backendLibraryId = $state("");
  let sort = $state("");
  let page = $state(1);
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let degraded = $state("");
  const playbackProviders = $derived(playableProviderIds(providers));
  let action = $state("");
  let loadVersion = 0;
  let rowsElement = $state<HTMLElement>();
  // Visited pages show instantly; the next page is fetched ahead so paging feels immediate.
  const pageCache = new Map<string, MatchReviewResponse>();
  let chosen = $state(new Map<string, MatchReviewItem>());
  let bulkConfirm = $state<"reject" | "clear">("reject");
  let bulkConfirmOpen = $state(false);

  let dialogOpen = $state(false);
  let initialReviewOpened = $state(false);
  let selected = $state<MatchReviewItem | null>(null);
  let destructiveOpen = $state(false);
  let destructive = $state<DestructiveAction | null>(null);
  let rematchAllOpen = $state(false);

  const provider = (providerId: string) => findProviderDefinition(providers, providerId);

  function providerName(providerId?: string | null) {
    if (!providerId) return "Unresolved";
    if (providerId === "local") return backend;
    return providerDisplayName(providers, providerId);
  }

  const query = (requested = page) => ({ page: requested, pageSize: 50, search, state: stateFilter, sort, backendLibraryId });
  const cacheKey = (requested = page) => JSON.stringify(query(requested));

  function remember(key: string, response: MatchReviewResponse) {
    pageCache.delete(key);
    pageCache.set(key, response);
    if (pageCache.size > 8) pageCache.delete(pageCache.keys().next().value!);
  }

  async function prefetch(requested: number) {
    const key = cacheKey(requested);
    if (requested < 1 || !data || requested > data.pagination.totalPages || pageCache.has(key)) return;
    try { remember(key, await matchReview.list(query(requested))); } catch { /* the page loads normally when opened */ }
  }

  async function load() {
    const version = ++loadVersion;
    const key = cacheKey();
    const cached = pageCache.get(key);
    if (cached) { data = cached; loading = false; }
    refreshing = !cached;
    error = "";
    try {
      const response = await matchReview.list(query());
      if (version !== loadVersion) return;
      data = response;
      remember(key, response);
      for (const match of response.matches)
        if (chosen.has(match.externalSnapshotId)) chosen.set(match.externalSnapshotId, match);
      void prefetch(page + 1);
    } catch (cause) {
      if (version !== loadVersion) return;
      error = cause instanceof Error ? cause.message : "Match review is unavailable.";
    } finally {
      if (version === loadVersion) {
        loading = false;
        refreshing = false;
      }
    }
  }

  async function loadProviders() {
    try {
      const schema = await home.schema();
      providers = schema.providers;
      backend = schema.activeBackend || backend;
    } catch (cause) {
      degraded =
        cause instanceof Error ? cause.message : "Provider presentation is unavailable.";
    }
  }

  async function openInitialReview() {
    if (!initialReview || initialReviewOpened) return;
    try {
      const requested = data?.matches.find((item) => item.externalSnapshotId === initialReview) ??
        await matchReview.get(initialReview);
      if (!requested) return;
      initialReviewOpened = true;
      openMatch(requested);
    } catch {
      // The review queue remains usable when a stale deep link no longer resolves.
    }
  }

  // Decisions change other pages too, so cached pages are dropped before reloading.
  function reload() {
    pageCache.clear();
    return load();
  }

  function goToPage(next: number) {
    page = next;
    void load();
    if (rowsElement && rowsElement.getBoundingClientRect().top < 0)
      rowsElement.scrollIntoView({ block: "start", behavior: "instant" });
  }

  const refreshScheduler = createRefreshScheduler(reload);
  const scheduleRefresh = refreshScheduler.schedule;

  function setState(value: string) {
    stateFilter = value;
    page = 1;
    chosen = new Map();
    void load();
  }

  function setView(value: string) {
    if (!(value in viewFilters)) return;
    view = value as MappingView;
    setState(viewFilters[view]);
  }

  function removeResolvedFromQueue(match: MatchReviewItem) {
    if (!data || !["tentative", "review", "unresolved"].includes(view)) return;
    const wasVisible = data.matches.some((item) => item.externalSnapshotId === match.externalSnapshotId);
    data = {
      ...data,
      matches: data.matches.filter((item) => item.externalSnapshotId !== match.externalSnapshotId),
      stats: {
        ...data.stats,
        suggested: Math.max(0, data.stats.suggested - (wasVisible && view === "tentative" ? 1 : 0)),
        ambiguous: Math.max(0, (data.stats.ambiguous ?? 0) - (wasVisible && view === "review" ? 1 : 0)),
        attention: Math.max(0, data.stats.attention - (wasVisible && ["tentative", "review"].includes(view) ? 1 : 0)),
        unresolved: Math.max(0, data.stats.unresolved - (wasVisible && view === "unresolved" ? 1 : 0)),
      },
      pagination: {
        ...data.pagination,
        total: Math.max(0, data.pagination.total - (wasVisible ? 1 : 0)),
      },
    };
  }

  function submitFilters() {
    search = searchInput.trim();
    page = 1;
    chosen = new Map();
    void load();
  }

  function openMatch(match: MatchReviewItem) {
    selected = match;
    dialogOpen = true;
  }

  async function matchSaved(message: string) {
    const resolved = selected;
    toast.success(message);
    await reload();
    if (resolved) removeResolvedFromQueue(resolved);
  }

  async function rematchAllQueued(_jobId: string, message: string) {
    toast.success(message);
    await reload();
  }

  async function rematch(match: MatchReviewItem) {
    if (action) return;
    action = `rematch:${match.externalSnapshotId}`;
    try {
      await matchReview.rematch(match.externalSnapshotId);
      toast.success(`${match.title || "Track"} rematched.`);
      await reload();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Rematch failed.");
    } finally {
      action = "";
    }
  }

  async function accept(match: MatchReviewItem) {
    const candidate = match.candidates.find((item) =>
      candidateResolution(item, match.providerId, playbackProviders));
    const resolution = candidateResolution(candidate, match.providerId, playbackProviders);
    if (action || !canEditAuthorityScope(match, "personal")) return;
    if (!resolution) return openMatch(match);
    action = `accept:${match.externalSnapshotId}`;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        ...resolution,
        reason: "Accepted highest-confidence automatic candidate",
        authorityScope: "personal",
        expectedAuthority: expectedAuthorityForScope(match, "personal"),
      });
      toast.success("Highest-confidence candidate accepted.");
      await reload();
      removeResolvedFromQueue(match);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The candidate could not be accepted.");
    } finally {
      action = "";
    }
  }

  function confirm(
    kind: DestructiveAction["kind"],
    match: MatchReviewItem,
    authorityScope: MatchAuthorityScope = "personal",
  ) {
    if (kind !== "reject") return;
    destructive = { kind, match, authorityScope };
    destructiveOpen = true;
  }

  function confirmAuthority(
    kind: "manual_delete" | "manual_rematch",
    match: MatchReviewItem,
    authority: ManualMatchAuthority,
  ) {
    destructive = { kind, match, authority };
    destructiveOpen = true;
  }

  async function applyDestructive() {
    if (!destructive || action) return;
    action = destructive.kind === "reject"
      ? destructive.kind
      : `authority:${destructive.authority.id}`;
    try {
      if (destructive.kind === "reject") {
        await matchReview.resolve(destructive.match.externalSnapshotId, {
          targetType: "reject",
          reason: "Rejected from the match review queue",
          authorityScope: destructive.authorityScope,
          expectedAuthority: expectedAuthorityForScope(
            destructive.match,
            destructive.authorityScope,
          ),
        });
        toast.success("Candidate rejected.");
      } else if (destructive.kind === "manual_rematch") {
        await manualMatchAuthority.rematch(destructive.authority);
        toast.success(`${scopeLabel(authorityScopeOf(destructive.authority))} decision cleared and track rematched.`);
      } else {
        await manualMatchAuthority.delete(destructive.authority);
        toast.success(`${scopeLabel(authorityScopeOf(destructive.authority))} decision cleared.`);
      }
      destructiveOpen = false;
      dialogOpen = false;
      await reload();
      if (destructive.kind === "reject") removeResolvedFromQueue(destructive.match);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The action failed.");
    } finally {
      action = "";
    }
  }

  const chosenItems = $derived([...chosen.values()]);
  const pageIds = $derived(data?.matches.map((match) => match.externalSnapshotId) ?? []);
  const pageChosen = $derived(pageIds.filter((id) => chosen.has(id)).length);
  const bestResolution = (match: MatchReviewItem) => candidateResolution(match.candidates.find((item) =>
    candidateResolution(item, match.providerId, playbackProviders)), match.providerId, playbackProviders);
  const bulkAccept = $derived(chosenItems.filter((match) =>
    !currentTarget(match) && bestResolution(match) && canEditAuthorityScope(match, "personal")));
  const bulkShare = $derived(chosenItems.filter((match) => currentTarget(match) &&
    allowedAuthorityScopes(match).includes("household") && canEditAuthorityScope(match, "household")));
  const bulkReject = $derived(chosenItems.filter((match) => canEditAuthorityScope(match, "personal")));
  const bulkClear = $derived(chosenItems.filter((match) => {
    const authority = authorityForScope(match, "personal");
    return authority != null && canEditAuthority(authority);
  }));

  function toggleChosen(match: MatchReviewItem) {
    const next = new Map(chosen);
    if (next.has(match.externalSnapshotId)) next.delete(match.externalSnapshotId);
    else next.set(match.externalSnapshotId, match);
    chosen = next;
  }

  function togglePage() {
    if (!data) return;
    const next = new Map(chosen);
    const all = pageChosen === pageIds.length;
    for (const match of data.matches) {
      if (all) next.delete(match.externalSnapshotId);
      else next.set(match.externalSnapshotId, match);
    }
    chosen = next;
  }

  function songs(count: number) { return `${count} ${count === 1 ? "song" : "songs"}`; }

  async function runBulk(verbs: [string, string], items: MatchReviewItem[], run: (match: MatchReviewItem) => Promise<unknown>) {
    if (!items.length || action) return;
    action = "bulk";
    const pending = toast.progress(`${verbs[0]} ${songs(items.length)}…`);
    let failed = 0;
    let next = 0;
    const worker = async () => {
      while (next < items.length) {
        const match = items[next++];
        try { await run(match); } catch { failed++; }
      }
    };
    await Promise.all(Array.from({ length: Math.min(4, items.length) }, worker));
    toast.dismiss(pending);
    const done = items.length - failed;
    if (failed) toast.error(`${verbs[1]} ${done} of ${songs(items.length)}. ${failed} could not be changed; try them again.`);
    else toast.success(`${verbs[1]} ${songs(done)}.`);
    chosen = new Map();
    action = "";
    await reload();
  }

  const bulkRematch = () => runBulk(["Rematching", "Rematched"], chosenItems,
    (match) => matchReview.rematch(match.externalSnapshotId));
  const bulkAcceptBest = () => runBulk(["Accepting", "Accepted"], bulkAccept, (match) =>
    matchReview.resolve(match.externalSnapshotId, {
      ...bestResolution(match)!,
      reason: "Accepted highest-confidence automatic candidate",
      authorityScope: "personal",
      expectedAuthority: expectedAuthorityForScope(match, "personal"),
    }));
  const bulkShareHousehold = () => runBulk(["Sharing", "Shared"], bulkShare, (match) => {
    const target = currentTarget(match)!;
    const reason = "Shared with the household from match review";
    return matchReview.resolve(match.externalSnapshotId, {
      ...(target.providerId === "local"
        ? { targetType: "local" as const, libraryTrackId: target.identity, reason }
        : { targetType: "provider" as const, externalProvider: target.providerId, externalId: target.identity, reason }),
      authorityScope: "household",
      expectedAuthority: expectedAuthorityForScope(match, "household"),
    });
  });
  async function applyBulkConfirm() {
    bulkConfirmOpen = false;
    if (bulkConfirm === "reject")
      await runBulk(["Rejecting", "Rejected"], bulkReject, (match) => matchReview.resolve(match.externalSnapshotId, {
        targetType: "reject",
        reason: "Rejected from the match review queue",
        authorityScope: "personal",
        expectedAuthority: expectedAuthorityForScope(match, "personal"),
      }));
    else
      await runBulk(["Clearing", "Cleared"], bulkClear, (match) =>
        manualMatchAuthority.delete(authorityForScope(match, "personal")!));
  }

  function authorityLabel(authority: ManualMatchAuthority) {
    const decision = authority.kind === "provider_match"
      ? `Manual ${providerName(authority.targetProviderId)} match`
      : authority.kind === "local_match"
        ? `Manual ${backend} match`
        : "Manual rejection";
    return `${scopeLabel(authorityScopeOf(authority))} · ${decision}`;
  }

  function scopeLabel(scope: MatchAuthorityScope) {
    return scope === "household" ? "Household" : "Personal";
  }

  function authorityState(match: MatchReviewItem, authority: ManualMatchAuthority) {
    if (authority.effective) return "Effective";
    if (authorityScopeOf(authority) === "household" && authorityForScope(match, "personal"))
      return "Superseded by personal";
    return "Not currently effective";
  }

  function revealedLayer(match: MatchReviewItem, authority: ManualMatchAuthority) {
    const scope = authorityScopeOf(authority);
    if (scope === "personal" && authorityForScope(match, "household"))
      return "The household decision beneath it will become effective.";
    if (scope === "household" && authorityForScope(match, "personal"))
      return "The personal decision remains effective.";
    return "Automatic matching will become effective.";
  }

  function confirmationCopy() {
    if (destructive?.kind === "manual_rematch") return {
      title: `Clear ${authorityScopeOf(destructive.authority)} and rematch?`,
      description: `The ${authorityScopeOf(destructive.authority)} decision will be cleared before the matching algorithm recalculates the track. ${revealedLayer(destructive.match, destructive.authority)} Durable audit history is retained.`,
      label: `Clear ${authorityScopeOf(destructive.authority)} and rematch`,
    };
    if (destructive?.kind === "manual_delete") return {
      title: `Clear ${authorityScopeOf(destructive.authority)} decision?`,
      description: `The ${authorityScopeOf(destructive.authority)} decision will stop being authoritative. ${revealedLayer(destructive.match, destructive.authority)} Durable audit history is retained.`,
      label: `Clear ${authorityScopeOf(destructive.authority)}`,
    };
    return {
      title: "Reject this candidate?",
      description: "The current candidate will be recorded as rejected. You can rematch it later.",
      label: "Reject candidate",
    };
  }

  onMount(() => {
    searchInput = initialSearch;
    search = initialSearch;
    if (initialSearch) {
      stateFilter = "";
      view = "all";
    }
    void loadProviders();
    void (async () => {
      await load();
      await openInitialReview();
    })();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
    };
  });
</script>

{#if loading}
  <section class="mapping-page" aria-label="Loading match review" aria-busy="true">
    <Skeleton class="panel skeleton-panel" />
  </section>
{:else if error && !data}
  <RouteError
    eyebrow="Match review unavailable"
    title="Allstarr could not load canonical match decisions."
    message={error}
    onRetry={load}
  />
{:else if data}
  {#if degraded || error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Some mapping data is unavailable.</strong> {error || degraded}</p>
      <Button variant="secondary" size="sm" onclick={() => { void loadProviders(); void load(); }}>Retry</Button>
    </div>
  {/if}

  <section class="mapping-page" aria-busy={refreshing}>
    <article class="panel mapping-queue">
      <header class="panel-heading playlist-toolbar mapping-heading">
        <div>
          <h2>Match review</h2>
          <p>Check how songs from your playlists were matched to your library and other sources.</p>
        </div>
        <div class="panel-heading-actions playlist-toolbar-actions mapping-heading-actions">
          <Button variant="secondary" disabled={Boolean(action) || refreshing} onclick={() => rematchAllOpen = true}>Rematch all</Button>
          <RefreshButton variant="secondary" disabled={Boolean(action)} onrefresh={reload} />
        </div>
      </header>

      <SegmentedNav
        items={[
          {
            id: "review",
            label: "Needs review",
            count: data.stats.ambiguous ?? Math.max(0, data.stats.review - data.stats.suggested),
            icon: ScanSearch,
          },
          { id: "tentative", label: "Unsure", count: data.stats.suggested, icon: Clock3 },
          { id: "unresolved", label: "No match", count: data.stats.unresolved, icon: CircleQuestionMark },
          {
            id: "automatic",
            label: "Automatic",
            count: data.stats.automatic ?? Math.max(0, data.stats.accepted - (data.stats.manualMatches ?? 0)),
            icon: Sparkles,
          },
          { id: "manual", label: "By hand", count: data.stats.manualMatches ?? 0, icon: Hand },
          { id: "rejected", label: "Rejected", count: data.stats.manualRejections ?? 0, icon: Ban },
          { id: "all", label: "All", count: data.stats.total, icon: List },
        ]}
        active={view}
        label="Mapping views"
        class="mapping-view-tabs"
        onchange={setView}
      />

      <form class="mapping-filters" onsubmit={(event) => { event.preventDefault(); submitFilters(); }}>
        <SearchField bind:value={searchInput} label="Search" placeholder="Search by song, artist, album or source" hiddenLabel />
        <label class="mapping-scope">
          <span class="sr-only">Library scope</span>
          <input bind:value={backendLibraryId} placeholder="All libraries" aria-label="Library scope" />
        </label>
        <SelectField bind:value={sort} label="Confidence" class="mapping-sort" onchange={() => { page = 1; void load(); }} options={[
          { value: "", label: "Default order" }, { value: "confidence_desc", label: "Most confident first" },
          { value: "confidence_asc", label: "Least confident first" },
        ]} />
        <Button type="submit" variant="secondary">Apply</Button>
      </form>



      {#if data.matches.length}
        <div class="mapping-select-bar">
          <label class="mapping-check">
            <input type="checkbox" checked={pageChosen > 0 && pageChosen === pageIds.length}
              indeterminate={pageChosen > 0 && pageChosen < pageIds.length} onchange={togglePage}
              aria-label="Select all songs on this page" />
          </label>
          <span>{chosen.size ? `${chosen.size} selected` : "Select songs to change several at once"}</span>
          {#if chosen.size}<button type="button" class="text-button" onclick={() => chosen = new Map()}>Clear selection</button>{/if}
        </div>
      {/if}

      <div class="mapping-rows" bind:this={rowsElement}>
        {#each data.matches as match (match.externalSnapshotId)}
          {@const target = currentTarget(match)}
          {@const candidate = match.candidates.find((item) =>
            candidateResolution(item, match.providerId, playbackProviders))}
          {@const resolution = candidateResolution(candidate, match.providerId, playbackProviders)}
          {@const candidateProviderId = resolution?.targetType === "provider" ? resolution.externalProvider : "local"}
          {@const candidateExternalId = resolution?.targetType === "provider" ? resolution.externalId : null}
          {@const candidateArtwork = candidateExternalId
            ? `/api/admin/downloads/artwork/${encodeURIComponent(`ext-${candidateProviderId}-song-${candidateExternalId}`)}`
            : candidate?.backendItemId
              ? `/api/admin/downloads/artwork/${encodeURIComponent(candidate.backendItemId)}`
              : ""}
          {@const visibleAuthorities = [...(match.manualAuthorities ?? [])].sort((left, right) =>
            authorityScopeOf(left) === authorityScopeOf(right)
              ? 0
              : authorityScopeOf(left) === "personal" ? -1 : 1)}
          <article class:needs-attention={isAttention(match.state)} class:chosen={chosen.has(match.externalSnapshotId)} class="mapping-row">
            <label class="mapping-check mapping-row-check">
              <input type="checkbox" checked={chosen.has(match.externalSnapshotId)} onchange={() => toggleChosen(match)}
                aria-label={`Select ${match.title || "song"}`} />
            </label>
            <div class="mapping-comparison">
              <div class="mapping-party">
                <MediaArtwork
                  class="mapping-art"
                  url={match.sourceArtworkUrl || match.artworkUrl}
                />
                <span class="mapping-party-copy">
                  <span class="mapping-provider">
                    <ProviderMark id={match.providerId} definition={provider(match.providerId)} />
                    From {providerName(match.providerId)}
                  </span>
                  <strong>{match.title || "Unknown song"}</strong>
                  <small>{match.artist || "Unknown artist"}{match.album ? ` · ${match.album}` : ""}</small>
                  <small>{formatDuration(match.durationMilliseconds)}</small>
                </span>
              </div>

              <ArrowRight class="mapping-arrow" size={20} aria-hidden="true" />

              <div class:unresolved={!target && !candidate} class="mapping-party">
                {#if target || candidate}
                  <MediaArtwork
                    class="mapping-art"
                    url={target?.artworkUrl || match.candidateArtworkUrl || candidateArtwork}
                  />
                {:else}
                  <span class="mapping-route-missing" aria-hidden="true">?</span>
                {/if}
                <span class="mapping-party-copy">
                  <span class="mapping-provider">
                    {#if target || candidate}
                      <ProviderMark
                        id={(target?.providerId ?? candidateProviderId) === "local" ? backend.toLowerCase() : (target?.providerId ?? candidateProviderId)}
                        definition={provider(target?.providerId ?? candidateProviderId)}
                        label={providerName(target?.providerId ?? candidateProviderId)}
                      />
                    {/if}
                    {target
                      ? `Plays from ${providerName(target.providerId)}`
                      : candidate
                        ? `Best guess · ${providerName(candidateProviderId)}`
                        : "No match yet"}
                  </span>
                  <strong>{target?.title || candidate?.title || "Nothing found"}</strong>
                  <small>{target?.artist || candidate?.artist || "Rematch, or search for the right song yourself."}{(target?.album || candidate?.album) ? ` · ${target?.album || candidate?.album}` : ""}</small>
                  {#if target || candidate}<small>{formatDuration(target?.durationMilliseconds ?? candidate?.durationMilliseconds)}</small>{/if}
                </span>
              </div>
            </div>

            <div class="mapping-row-footer">
              <div class="mapping-evidence">
                <Badge state={match.state}>{reviewStateLabel(match.state)}</Badge>
                {#if candidate || target}
                  <span class="mapping-confidence" aria-hidden="true"><span style={`width: ${Math.round((match.confidence ?? 0) * 100)}%`}></span>{#if match.threshold != null}<i style={`left: ${Math.round(match.threshold * 100)}%`}></i>{/if}</span>
                {/if}
                <span class="mapping-evidence-summary">
                  {candidate || target ? `${percent(match.confidence)} confidence` : "Not scored"}
                  {#if match.threshold != null} · {percent(match.threshold)} auto threshold{/if}
                  · {relativeTime(match.decidedAt, "Not decided")}
                  {#if match.reasons.length || match.warnings.length}
                    · {[...match.reasons, ...match.warnings].slice(0, 2).map((reason) => reason.replaceAll("_", " ")).join(" · ")}
                  {/if}
                </span>
                {#if match.sourceArtworkUrl && match.candidateArtworkUrl}
                  <ArtworkSimilarity source={match.sourceArtworkUrl} candidate={match.candidateArtworkUrl} />
                {/if}
              </div>

              <div class="mapping-row-actions">
                {#if !target && resolution}
                  <span class="mapping-action-confidence sr-only">
                    <strong>{percent(candidate?.confidence ?? match.confidence)}</strong>
                    <small>Confidence</small>
                  </span>
                  <Button disabled={Boolean(action) || !canEditAuthorityScope(match, "personal")} onclick={() => void accept(match)}>{action === `accept:${match.externalSnapshotId}` ? "Accepting…" : "Accept"}</Button>
                {/if}
                <Button class="mapping-search-action" variant={!target && resolution ? "secondary" : "default"} onclick={() => openMatch(match)}>
                  {target ? "Review match" : "Interactive search"}
                </Button>
                {#if !target}
                  <Button class="mapping-rematch-action" variant="secondary" disabled={Boolean(action)} onclick={() => void rematch(match)}>{action === `rematch:${match.externalSnapshotId}` ? "Rematching…" : "Rematch"}</Button>
                {/if}
                <DropdownMenu.Root>
                  <DropdownMenu.Trigger class="track-menu-trigger" disabled={Boolean(action)} aria-label={`More actions for ${match.title || "track"}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                  <DropdownMenu.Portal>
                    <DropdownMenu.Content class="bits-menu" sideOffset={4} align="end">
                      <DropdownMenu.Item class="bits-menu-item" onSelect={() => window.location.hash = reportHref({ externalSnapshotId: match.externalSnapshotId }, "/library/mappings")}>Report a problem</DropdownMenu.Item>
                      <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void rematch(match)}>Rematch</DropdownMenu.Item>
                      <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action) || !canEditAuthorityScope(match, "personal")} onSelect={() => confirm("reject", match)}>Reject candidate</DropdownMenu.Item>
                      {#each match.manualAuthorities ?? [] as authority (authority.id)}
                        <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !canEditAuthority(authority)} onSelect={() => confirmAuthority("manual_rematch", match, authority)}>
                          Clear {authorityScopeOf(authority)} and rematch
                        </DropdownMenu.Item>
                        <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action) || !canEditAuthority(authority)} onSelect={() => confirmAuthority("manual_delete", match, authority)}>
                          Clear {authorityScopeOf(authority)}
                        </DropdownMenu.Item>
                      {/each}
                    </DropdownMenu.Content>
                  </DropdownMenu.Portal>
                </DropdownMenu.Root>
              </div>
            </div>

            {#if visibleAuthorities.length}
              <div class="manual-authority-list" aria-label={`Manual decisions for ${match.title || "track"}`}>
                {#each visibleAuthorities as authority (authority.id)}
                  <div class="manual-authority-row">
                    <Hand size={16} aria-hidden="true" />
                    <span>
                      <strong>{authorityLabel(authority)}</strong>
                      <small>
                        {authority.reason}
                        · {authorityState(match, authority)}
                      </small>
                    </span>
                    <Button
                      variant="secondary"
                      size="sm"
                      disabled={Boolean(action) || !canEditAuthority(authority)}
                      onclick={() => confirmAuthority("manual_rematch", match, authority)}
                    >Clear {authorityScopeOf(authority)} and rematch</Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={Boolean(action) || !canEditAuthority(authority)}
                      onclick={() => confirmAuthority("manual_delete", match, authority)}
                    >Clear {authorityScopeOf(authority)}</Button>
                  </div>
                {/each}
              </div>
            {/if}

            <details class="mapping-details">
              <summary>Why this score? <ChevronDown size={16} aria-hidden="true" /></summary>
              <p>
                Allstarr compares the title, artist, album, length, ISRC, artwork and
                verified source IDs when they're available. Missing details are skipped, not
                counted as zero. A match is accepted automatically at {percent(match.threshold)}
                or higher, unless it's ambiguous or has warnings. Your local and extension
                preferences only apply after that.
              </p>
              <dl>
                {#if candidate}
                  {#each scoreComponents(candidate) as [name, value]}
                    <div><dt>{name.replace(/([a-z])([A-Z])/g, "$1 $2").replaceAll("_", " ")}</dt><dd>{percent(value)}</dd></div>
                  {/each}
                {/if}
                <div><dt>Source snapshot</dt><dd>{match.externalSnapshotId}</dd></div>
                {#if target?.identity || candidate?.libraryTrackId || candidateExternalId}<div><dt>Matched item</dt><dd>{target?.identity || candidate?.libraryTrackId || candidateExternalId}</dd></div>{/if}
                {#if match.canonicalRecordingId}<div><dt>Canonical recording</dt><dd>{match.canonicalRecordingId}</dd></div>{/if}
                {#if match.libraryTrackId}<div><dt>Library track</dt><dd>{match.libraryTrackId}</dd></div>{/if}
                {#if match.isrc}<div><dt>ISRC</dt><dd>{match.isrc}</dd></div>{/if}
                {#if match.algorithmVersion}<div><dt>Algorithm</dt><dd>{match.algorithmVersion}</dd></div>{/if}
                {#each match.providerIdentities as identity}
                  <div><dt>{providerName(identity.providerId)}</dt><dd>{identity.externalId} · {identity.verification}</dd></div>
                {/each}
              </dl>
            </details>
          </article>
        {:else}
          <div class="compact-empty">
            <strong>Nothing here</strong>
            <p>{view === "review" ? "No songs need your review right now." : "Try another filter, or wait for the next playlist match."}</p>
          </div>
        {/each}
      </div>

      <nav class="playlist-pagination mapping-pagination" aria-label="Match review pages">
        <span>{data.pagination.total} {data.pagination.total === 1 ? "song" : "songs"}</span>
        <div>
          <Button variant="secondary" size="sm" disabled={data.pagination.page <= 1} onclick={() => goToPage(page - 1)}>Previous</Button>
          <span>Page {data.pagination.page} of {data.pagination.totalPages}</span>
          <Button variant="secondary" size="sm" disabled={data.pagination.page >= data.pagination.totalPages} onclick={() => goToPage(page + 1)}>Next</Button>
        </div>
      </nav>

      {#if chosen.size}
        <div class="mapping-bulk" role="toolbar" aria-label="Actions for selected songs">
          <strong>{chosen.size} selected</strong>
          <div>
            {#if bulkAccept.length}<Button disabled={Boolean(action)} onclick={() => void bulkAcceptBest()}>Accept best ({bulkAccept.length})</Button>{/if}
            <Button variant="secondary" disabled={Boolean(action)} onclick={() => void bulkRematch()}>Rematch ({chosen.size})</Button>
            {#if bulkShare.length}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void bulkShareHousehold()}>Share with household ({bulkShare.length})</Button>{/if}
            {#if bulkClear.length}<Button variant="secondary" disabled={Boolean(action)} onclick={() => { bulkConfirm = "clear"; bulkConfirmOpen = true; }}>Clear my decisions ({bulkClear.length})</Button>{/if}
            {#if bulkReject.length}<Button variant="secondary" disabled={Boolean(action)} onclick={() => { bulkConfirm = "reject"; bulkConfirmOpen = true; }}>Reject ({bulkReject.length})</Button>{/if}
            <Button variant="ghost" disabled={Boolean(action)} onclick={() => chosen = new Map()}>Cancel</Button>
          </div>
        </div>
      {/if}
    </article>
  </section>

  <MatchDialog
    bind:open={dialogOpen}
    match={selected}
    {providers}
    {backend}
    onSaved={matchSaved}
    onReject={(match, authorityScope) => confirm("reject", match, authorityScope)}
  />

  <BulkRematchDialog kind="automatic" bind:open={rematchAllOpen} onQueued={rematchAllQueued} />
  <ConfirmDialog
    bind:open={bulkConfirmOpen}
    title={bulkConfirm === "reject" ? `Reject ${songs(bulkReject.length)}?` : `Clear your decisions for ${songs(bulkClear.length)}?`}
    description={bulkConfirm === "reject"
      ? "Their current candidates are recorded as rejected for you. You can rematch them later."
      : "Your personal decisions stop applying; household decisions or automatic matching take over. Audit history is kept."}
    confirmLabel={bulkConfirm === "reject" ? "Reject songs" : "Clear decisions"}
    onConfirm={applyBulkConfirm}
  />

  <ConfirmDialog
    bind:open={destructiveOpen}
    preventScroll={!dialogOpen}
    title={confirmationCopy().title}
    description={confirmationCopy().description}
    confirmLabel={confirmationCopy().label}
    onConfirm={applyDestructive}
  />
{/if}

<style>
  .mapping-select-bar {
    display: flex; align-items: center; gap: var(--space-2); min-height: 2.75rem;
    padding: 0 var(--space-5) 0 calc(var(--space-5) - .75rem); color: var(--color-ink-muted); font-size: .8125rem;
  }
  .mapping-check {
    display: grid; flex: none; width: 2.75rem; height: 2.75rem; place-items: center; cursor: pointer; border-radius: 50%;
  }
  .mapping-check:hover { background: color-mix(in srgb, var(--color-ink) 6%, transparent); }
  .mapping-check input { width: 1.125rem; height: 1.125rem; margin: 0; accent-color: var(--md-sys-color-primary); cursor: pointer; }
  .mapping-rows .mapping-row { position: relative; padding-left: calc(var(--space-5) + 2.25rem); }
  .mapping-rows .mapping-row.chosen { background: color-mix(in srgb, var(--md-sys-color-primary) 8%, transparent); }
  .mapping-row-check { position: absolute; top: calc(var(--space-4) + 1rem); left: calc(var(--space-5) - .75rem); }
  .mapping-bulk {
    position: sticky; z-index: 5; bottom: var(--space-4); display: flex; flex-wrap: wrap; align-items: center;
    justify-content: space-between; gap: var(--space-3); margin: var(--space-4) var(--space-5) 0; padding: var(--space-3) var(--space-4);
    border-radius: var(--radius-lg); background: var(--md-sys-color-inverse-surface); color: var(--md-sys-color-inverse-on-surface);
    box-shadow: 0 8px 24px #0004;
  }
  .mapping-bulk > div { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .mapping-bulk :global([data-slot="button"]:last-child) { color: inherit; }
  @media (max-width: 767px) {
    .mapping-bulk { bottom: calc(var(--mobile-nav-height, 72px) + var(--space-3)); margin-inline: 0; }
    .mapping-rows .mapping-row { padding-left: calc(var(--space-4) + 2.25rem); }
    .mapping-row-check { left: calc(var(--space-4) - .75rem); }
  }
  .manual-authority-row :global([data-slot="button"]) {
    min-width: 0;
    height: auto;
    min-height: var(--control-sm);
    padding-block: var(--space-2);
    line-height: 1.25;
    white-space: normal;
  }
</style>
