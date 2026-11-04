<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount, tick } from "svelte";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { MoreHorizontal } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    home,
    playlistLinks,
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
  import MatchesPanel from "$lib/components/MatchesPanel.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { ChevronRight, X } from "@lucide/svelte";
  import { currentSong, defaultMatchFilter, differenceChips, filterCounts, filterStates, keyboardCommand, matchesSearch, summarizeMatches, inFilter, sortMatches, type MatchFilter } from "$lib/matches-presentation";
  import { installMatchSearchShortcut, takeMatchSearchRequest } from "$lib/matches-search";
  import { reportHref } from "$lib/support";
  import BulkRematchDialog from "$lib/components/BulkRematchDialog.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import {
    candidateResolution,
    currentTarget,
    isAttention,
    isReviewableMatch,
    playableProviderIds,
    percent,
  } from "$lib/mappings";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  type DestructiveAction =
    | { kind: "reject"; match: MatchReviewItem; authorityScope: MatchAuthorityScope }
    | {
        kind: "manual_delete" | "manual_rematch";
        match: MatchReviewItem;
        authority: ManualMatchAuthority;
      };
  type MappingView = MatchFilter;
  const storageKey = "matches.filter";
  let { initialSearch = "", initialReview = "" }: { initialSearch?: string; initialReview?: string } = $props();

  let data = $state<MatchReviewResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let backend = $state("Local library");
  let stateFilter = $state("");
  let view = $state<MappingView>("all");
  let filterReady = false;
  let bulkMode = $state(false);
  let phoneLayout = $state(false);
  let activeId = $state("");
  let acceptRequest = $state(0);
  let lyricsRequest = $state(0);
  let shortcutsOpen = $state(false);
  let playlistCount = $state<number | null>(null);
  let playlistNames = $state<Record<string, string[]>>({});
  let playlistSortReady = $state(false);
  let libraryOptions = $state<{ value: string; label: string }[]>([]);
  let allFiltered: MatchReviewItem[] | null = null;
  let searchInput = $state("");
  let search = $state("");
  let backendLibraryId = $state("");
  let sort = $state("confidence_asc");
  let page = $state(1);
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let degraded = $state("");
  const playbackProviders = $derived(playableProviderIds(providers));
  const visibleMatches = $derived(data?.matches.filter(isReviewableMatch) ?? []);
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

  const query = (requested = page) => ({ page: requested, pageSize: 50, search, state: stateFilter, sort: sort.startsWith("confidence") ? sort : "confidence_asc", backendLibraryId });
  const counts = $derived(data ? filterCounts(data.stats) : { needs: 0, automatic: 0, manual: 0, rejected: 0, all: 0 });
  let threshold = $state<number | null>(null);
  const collections = new Map<string, { response: MatchReviewResponse; rows: MatchReviewItem[] }>();
  const playlistSearch = () => Boolean(search && Object.values(playlistNames).some(names => names.some(name => name.toLocaleLowerCase().includes(search.toLocaleLowerCase()))));
  const useCollection = () => view === "needs" || !sort.startsWith("confidence") || playlistSearch();
  async function collection() {
    const params = query();
    const ordering = sort;
    const term = search;
    const searchingPlaylists = playlistSearch();
    const selectedFilter = view;
    const states = searchingPlaylists ? [""] : [...filterStates[view]];
    if (searchingPlaylists) params.search = "";
    const key = JSON.stringify({ view, search, backendLibraryId, ordering });
    const cached = collections.get(key);
    if (cached) return cached;
    const responses: MatchReviewResponse[] = [];
    const rows: MatchReviewItem[] = [];
    for (const state of states) {
      let requested = 1, pages = 1;
      do {
        const response = await matchReview.list({ ...params, page: requested, state, pageSize: 200 });
        responses.push(response); rows.push(...response.matches); pages = response.pagination.totalPages;
        requested++;
      } while (requested <= pages);
    }
    const unique = [...new Map(rows.map(item => [item.externalSnapshotId, item])).values()];
    const matching = searchingPlaylists ? unique.filter(item => matchesSearch(item, term, playlistNames[item.externalSnapshotId])) : unique;
    const response = searchingPlaylists ? { ...responses[0], stats: summarizeMatches(matching) } : responses[0];
    const result = { response, rows: sortMatches(searchingPlaylists ? matching.filter(item => inFilter(item, selectedFilter)) : matching, ordering, playlistNames) };
    collections.set(key, result);
    if (collections.size > 4) collections.delete(collections.keys().next().value!);
    return result;
  }
  const cacheKey = (requested = page) => JSON.stringify(query(requested));

  function remember(key: string, response: MatchReviewResponse) {
    pageCache.delete(key);
    pageCache.set(key, response);
    if (pageCache.size > 8) pageCache.delete(pageCache.keys().next().value!);
  }

  async function prefetch(requested: number) {
    if (useCollection()) return;
    const key = `${view}:${sort}:${cacheKey(requested)}`;
    if (requested < 1 || !data || requested > data.pagination.totalPages || pageCache.has(key)) return;
    try { remember(key, await matchReview.list(query(requested))); } catch { /* the page loads normally when opened */ }
  }

  async function load() {
    const version = ++loadVersion;
    const key = `${view}:${sort}:${cacheKey()}`;
    const cached = pageCache.get(key);
    if (cached) { data = cached; loading = false; }
    refreshing = !cached;
    error = "";
    try {
      let response: MatchReviewResponse;
      if (useCollection()) {
        const loaded = await collection();
        if (version !== loadVersion) return;
        allFiltered = loaded.rows;
        response = { ...loaded.response, matches: loaded.rows.slice((page - 1) * 50, page * 50), pagination: { page, pageSize: 50, total: loaded.rows.length, totalPages: Math.max(1, Math.ceil(loaded.rows.length / 50)) } };
      } else { allFiltered = null; response = await matchReview.list(query()); }
      if (version !== loadVersion) return;
      threshold = response.matches.find(item => item.threshold != null)?.threshold ?? threshold;
      const libraries = new Set([...libraryOptions.map(item => item.value), ...response.matches.map(item => item.localTrack?.backendLibraryId).filter((id): id is string => Boolean(id))]);
      libraryOptions = [...libraries].map(value => ({ value, label: value }));
      if (!filterReady) {
        filterReady = true;
        let saved = "";
        try { saved = localStorage.getItem(storageKey) || ""; } catch { /* Browser storage is optional. */ }
        const initial: MappingView = defaultMatchFilter(response.stats, saved, initialSearch);
        if (initial !== view) { view = initial; stateFilter = filterStates[view][0]; await load(); return; }
      }
      if (version !== loadVersion) return;
      data = response;
      if (!activeId && response.matches.length) activeId = response.matches[0].externalSnapshotId;
      remember(key, response);
      for (const match of response.matches) {
        if (!chosen.has(match.externalSnapshotId)) continue;
        if (isReviewableMatch(match)) chosen.set(match.externalSnapshotId, match);
        else chosen.delete(match.externalSnapshotId);
      }
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
      const links = await playlistLinks.list();
      playlistCount = links.playlistLinks?.length ?? null;
      const names: Record<string, string[]> = {};
      let failed = false;
      for (const link of links.playlistLinks ?? []) {
        try {
          const details = await playlistLinks.details(link.id);
          for (const track of details.tracks ?? []) {
            if (!track.externalSnapshotId) continue;
            const previous = names[track.externalSnapshotId] ?? [];
            if (!previous.includes(link.name)) names[track.externalSnapshotId] = [...previous, link.name];
          }
        } catch { failed = true; }
      }
      playlistNames = names;
      playlistSortReady = !failed;
      if (failed) degraded = "Some playlist names could not load.";
      else degraded = "";
      if (sort === "playlist" || playlistSearch()) { collections.clear(); await load(); }
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
      if (!requested || !isReviewableMatch(requested)) return;
      initialReviewOpened = true;
      openMatch(requested);
    } catch {
      // The review queue remains usable when a stale deep link no longer resolves.
    }
  }

  // Decisions change other pages too, so cached pages are dropped before reloading.
  function reload() {
    pageCache.clear();
    collections.clear();
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
    if (!Object.hasOwn(filterStates, value)) return;
    view = value as MappingView;
    allFiltered = null;
    setState(filterStates[view][0]);
  }

  function removeResolvedFromQueue(match: MatchReviewItem) {
    if (!data || view !== "needs") return;
    const wasVisible = data.matches.some((item) => item.externalSnapshotId === match.externalSnapshotId);
    data = {
      ...data,
      matches: data.matches.filter((item) => item.externalSnapshotId !== match.externalSnapshotId),
      stats: {
        ...data.stats,
        suggested: Math.max(0, data.stats.suggested - (wasVisible && match.state === "suggested" ? 1 : 0)),
        ambiguous: Math.max(0, (data.stats.ambiguous ?? 0) - (wasVisible && match.state === "ambiguous" ? 1 : 0)),
        attention: Math.max(0, data.stats.attention - (wasVisible && ["suggested", "ambiguous"].includes(match.state) ? 1 : 0)),
        unresolved: Math.max(0, data.stats.unresolved - (wasVisible && match.state === "unresolved" ? 1 : 0)),
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

  // Searching updates as you type, once typing pauses.
  $effect(() => {
    const value = searchInput.trim();
    if (value === search) return;
    const timer = setTimeout(submitFilters, 350);
    return () => clearTimeout(timer);
  });

  function openMatch(match: MatchReviewItem) {
    selected = match;
    activeId = match.externalSnapshotId;
    dialogOpen = true;
  }

  async function matchSaved(message: string) {
    const resolved = selected;
    toast.success(message);
    await reload();
    if (resolved) removeResolvedFromQueue(resolved);
    await tick();
    focusMatch(activeId);
  }

  async function rematchAllQueued(_jobId: string, message: string) {
    toast.success(message);
    await reload();
  }

  async function rematch(match: MatchReviewItem) {
    if (action) return;
    action = `rematch:${match.externalSnapshotId}`;
    const pending = toast.progress(`Rematching ${match.title || "song"}…`);
    try {
      await matchReview.rematch(match.externalSnapshotId);
      toast.success(`${match.title || "Track"} rematched.`);
      await reload();
      if (dialogOpen && selected?.externalSnapshotId === match.externalSnapshotId)
        selected = data?.matches.find(item => item.externalSnapshotId === match.externalSnapshotId) ?? await matchReview.get(match.externalSnapshotId);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Rematch failed.");
    } finally {
      toast.dismiss(pending);
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
      await tick();
      focusMatch(activeId);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The action failed.");
    } finally {
      action = "";
    }
  }

  const chosenItems = $derived([...chosen.values()]);
  const pageIds = $derived(visibleMatches.map((match) => match.externalSnapshotId));
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
    for (const match of visibleMatches) {
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
      title: "Nothing fits?",
      description: "The current candidate will be recorded as rejected. You can rematch it later.",
      label: "Reject match",
    };
  }

  onMount(() => {
    const breakpoint = matchMedia("(max-width: 899px)");
    const syncLayout = () => phoneLayout = breakpoint.matches;
    syncLayout();
    breakpoint.addEventListener("change", syncLayout);
    window.addEventListener("matches-search", focusRequestedSearch);
    const removeSearchShortcut = installMatchSearchShortcut();
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
      breakpoint.removeEventListener("change", syncLayout);
      window.removeEventListener("matches-search", focusRequestedSearch);
      removeSearchShortcut();
      unsubscribe();
      refreshScheduler.cancel();
    };
  });

  function focusRequestedSearch() {
    const input = document.querySelector<HTMLInputElement>(".matches-search input");
    if (input && takeMatchSearchRequest()) input.focus();
  }
  $effect(() => { if (!loading && data && !dialogOpen) focusRequestedSearch(); });

  function focusMatch(id: string) {
    const match = visibleMatches.find(item => item.externalSnapshotId === id) ?? visibleMatches[0];
    if (!match) { document.querySelector<HTMLInputElement>(".matches-search input")?.focus(); return; }
    activeId = match.externalSnapshotId;
    const selector = `[data-snapshot="${CSS.escape(activeId)}"] .open-match, [data-snapshot="${CSS.escape(activeId)}"] .row-action`;
    [...document.querySelectorAll<HTMLButtonElement>(selector)].find(element => element.getClientRects().length > 0)?.focus();
  }
  function moveSelected(delta: number) {
    const index = visibleMatches.findIndex(item => item.externalSnapshotId === activeId);
    const next = visibleMatches[Math.min(visibleMatches.length - 1, Math.max(0, (index < 0 ? (delta > 0 ? -1 : 1) : index) + delta))];
    if (!next) return;
    activeId = next.externalSnapshotId;
    if (dialogOpen) selected = next;
    else focusMatch(activeId);
  }
  function keys(event: KeyboardEvent) {
    if (destructiveOpen || bulkConfirmOpen || shortcutsOpen || event.target instanceof HTMLElement && event.target.closest('[role="alertdialog"]')) return;
    const command = keyboardCommand(event as unknown as Parameters<typeof keyboardCommand>[0]);
    if (!command || command === "search") return;
    const match = visibleMatches.find(item => item.externalSnapshotId === activeId) || visibleMatches[0];
    if (dialogOpen && ["previous", "next", "open", "close"].includes(command)) return;
    if (!dialogOpen && event.target instanceof HTMLElement && event.target.closest("button, a") && command === "open") return;
    event.preventDefault();
    if (command === "previous") moveSelected(-1);
    if (command === "next") moveSelected(1);
    if (command === "open" && match) openMatch(match);
    if (command === "accept" && match) { if (!dialogOpen) openMatch(match); acceptRequest++; }
    if (command === "rematch" && match) void rematch(selected && dialogOpen ? selected : match);
    if (command === "help") shortcutsOpen = true;
  }
  function rowSong(match: MatchReviewItem) {
    const target = currentSong(match);
    if (target) return target;
    const candidate = match.candidates.find(item => candidateResolution(item, match.providerId, playbackProviders));
    const resolution = candidateResolution(candidate, match.providerId, playbackProviders);
    return candidate && resolution ? { ...candidate, isrc: candidate.candidateIsrc, providerId: resolution.targetType === "provider" ? resolution.externalProvider : "local", identity: resolution.targetType === "provider" ? resolution.externalId : resolution.libraryTrackId } : null;
  }
  function pill(match: MatchReviewItem): { text: string; tone: "ok" | "warn" | "stop" | "neutral" } {
    if (match.state === "rejected") return { text: "Rejected", tone: "stop" };
    if (match.manualAuthorities?.some(item => item.effective && item.kind !== "rejection") || match.state === "pinned") return { text: "By you", tone: "neutral" };
    if (match.confidence == null) return { text: "No match", tone: "stop" };
    return { text: percent(match.confidence), tone: match.threshold != null && match.confidence >= match.threshold ? "ok" : "warn" };
  }
  async function selectAll() {
    if (!data || action) return;
    action = "select";
    try {
      const loaded = allFiltered ?? (await collection()).rows;
      chosen = new Map(loaded.filter(isReviewableMatch).map(item => [item.externalSnapshotId, item]));
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "Songs could not be selected."); }
    finally { action = ""; }
  }
</script>

<svelte:window onkeydown={keys} />

{#snippet rowMenu(match: MatchReviewItem)}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger class={`${buttonVariants({ variant: "ghost", size: "icon" })} row-menu`} disabled={Boolean(action)} aria-label={`More actions for ${match.title || "song"}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
    <DropdownMenu.Portal><DropdownMenu.Content class="bits-menu" sideOffset={4} align="end">
      <DropdownMenu.Item class="bits-menu-item" onSelect={() => { openMatch(match); lyricsRequest++; }} disabled={!match.canonicalRecordingId}>Wrong lyrics?</DropdownMenu.Item>
      <DropdownMenu.Item class="bits-menu-item" onSelect={() => window.location.hash = reportHref({ externalSnapshotId: match.externalSnapshotId }, "/library/matches")}>Report a problem</DropdownMenu.Item>
      <DropdownMenu.Item class="bits-menu-item" onSelect={() => void rematch(match)}>Rematch</DropdownMenu.Item>
      <DropdownMenu.Item class="bits-menu-item danger-item" disabled={!canEditAuthorityScope(match, "personal")} onSelect={() => confirm("reject", match)}>Nothing fits</DropdownMenu.Item>
      {#each match.manualAuthorities ?? [] as authority (authority.id)}
        <DropdownMenu.Item class="bits-menu-item" disabled={!canEditAuthority(authority)} onSelect={() => confirmAuthority("manual_rematch", match, authority)}>Clear {authorityScopeOf(authority)} and rematch</DropdownMenu.Item>
        <DropdownMenu.Item class="bits-menu-item danger-item" disabled={!canEditAuthority(authority)} onSelect={() => confirmAuthority("manual_delete", match, authority)}>Clear {authorityScopeOf(authority)}</DropdownMenu.Item>
      {/each}
    </DropdownMenu.Content></DropdownMenu.Portal>
  </DropdownMenu.Root>
{/snippet}

{#if loading}
  <section class="mapping-page" aria-label="Loading match review"><RowsSkeleton count={8} /></section>
{:else if error && !data}
  <ErrorState title="Matches could not load" detail={error} onretry={load} />
{:else if data}
  <section class="mapping-page" class:panel-open={dialogOpen} aria-busy={refreshing}>
    <article class="panel mapping-queue">
      <header class="mapping-heading">
        <h2>Matches</h2>
        <PageStatus items={[
          { label: `${data.stats.total.toLocaleString()} songs${playlistCount == null ? "" : ` from ${playlistCount.toLocaleString()} playlists`}`, emphasis: true },
          ...(threshold == null ? [] : [{ label: `Matches above ${percent(threshold)} are accepted on their own` }]),
        ]} segments={[
          { label: "Matched by Allstarr", value: counts.automatic, tone: "info" },
          { label: "Chosen by you", value: counts.manual, tone: "neutral" },
          { label: "Unsure", value: data.stats.review, tone: "warn" },
          { label: "Without a match", value: data.stats.unresolved, tone: "stop" },
        ]} />
      </header>
      {#if degraded || error}<div class="degraded-banner" role="status"><p>{error || degraded}</p><Button variant="text" onclick={() => { void loadProviders(); void load(); }}>Try again</Button></div>{/if}
      <div class="matches-controls">
        <FilterSegments options={[
          { id: "needs", label: "Needs a look", count: counts.needs },
          { id: "automatic", label: "Matched by Allstarr", count: counts.automatic },
          { id: "manual", label: "Chosen by you", count: counts.manual },
          { id: "rejected", label: "Rejected", count: counts.rejected },
          { id: "all", label: "All", count: counts.all },
        ]} value={view} {storageKey} onchange={setView} label="Match filters" />
        <div class="matches-toolbar">
          <SearchField class="matches-search" bind:value={searchInput} label="Search" placeholder="Search song, artist, album or playlist" hiddenLabel />
          <SelectField value={backendLibraryId} label="Library scope" options={[{ value: "", label: "All libraries" }, ...libraryOptions]} onchange={(value) => { backendLibraryId = value; page = 1; void load(); }} />
          <SelectField bind:value={sort} label="Sort matches" options={[
            { value: "confidence_asc", label: "Least sure first" }, { value: "confidence_desc", label: "Most sure first" },
            { value: "newest", label: "Newest" }, { value: "title", label: "Title" }, { value: "playlist", label: "Playlist", disabled: !playlistSortReady },
          ]} onchange={() => { page = 1; void load(); }} />
          <Button variant="text" size="sm" aria-pressed={bulkMode} onclick={() => { bulkMode = !bulkMode; if (!bulkMode) chosen = new Map(); }}>{bulkMode ? "Done selecting" : "Select"}</Button>
          <Button variant="outline" size="sm" disabled={Boolean(action) || refreshing} onclick={() => rematchAllOpen = true}>Rematch all</Button>
          <RefreshButton variant="ghost" size="icon" class="matches-refresh" disabled={Boolean(action)} onrefresh={reload} />
        </div>
      </div>
      {#if counts.needs === 0}<div class="quiet-success"><EmptyState title="Nothing needs you right now" /></div>{/if}
      {#if bulkMode && visibleMatches.length}<div class="mapping-select-bar">
        <label class="select-target"><input type="checkbox" checked={pageChosen > 0 && pageChosen === pageIds.length} indeterminate={pageChosen > 0 && pageChosen < pageIds.length} onchange={togglePage} aria-label="Select all songs on this page" /> Select page</label>
        <Button variant="text" disabled={Boolean(action) || refreshing} onclick={() => void selectAll()}>Select all {data.pagination.total} in this filter</Button>
      </div>{/if}
      <div class="mapping-rows" bind:this={rowsElement}>
        {#if visibleMatches.length}
          <table class="matches-table">{#if !phoneLayout}<thead><tr>{#if bulkMode}<th class="check-cell"><span class="sr-only">Select</span></th>{/if}<th>From your playlist</th><th>Plays as</th><th>Sure</th><th>What differs</th><th><span class="sr-only">Actions</span></th></tr></thead>{/if}
            <tbody>{#each visibleMatches as match (match.externalSnapshotId)}
              {@const target = rowSong(match)}{@const differences = differenceChips(match, target)}{@const state = pill(match)}
              <tr class="mapping-row" data-snapshot={match.externalSnapshotId} aria-label={`Open match for ${match.title || "song"}`} onfocusin={() => activeId = match.externalSnapshotId} class:needs-review={isAttention(match.state)} class:chosen={chosen.has(match.externalSnapshotId) || activeId === match.externalSnapshotId}>
                {#if phoneLayout}
                  <td class="phone-cell" colspan={bulkMode ? 6 : 5}>
                    <ItemRow title={match.title || "Song"} actionLabel={`Open match for ${match.title || "song"}`} subtitle={target ? `${providerName(target.providerId)} · ${differences[0] || "Same song"}` : "No match yet"} attention={isAttention(match.state)} selected={activeId === match.externalSnapshotId} selection={bulkMode} checked={chosen.has(match.externalSnapshotId)} oncheck={() => toggleChosen(match)} onclick={() => openMatch(match)}>
                      {#snippet leading()}<MediaArtwork url={match.sourceArtworkUrl || match.artworkUrl} />{/snippet}
                      {#snippet pill()}<StatePill tone={state.tone} text={state.text} />{/snippet}
                      {#snippet menu()}{@render rowMenu(match)}{/snippet}
                    </ItemRow>
                  </td>
                {:else}
                  {#if bulkMode}<td class="desktop-cell check-cell"><label class="select-target"><input type="checkbox" checked={chosen.has(match.externalSnapshotId)} onchange={() => toggleChosen(match)} aria-label={`Select ${match.title || "song"}`} /></label></td>{/if}
                  <td class="desktop-cell source-cell">
                    <button type="button" class="open-match" aria-label={`Open match for ${match.title || "song"}`} onclick={() => openMatch(match)}></button>
                    <div class="source-copy"><MediaArtwork url={match.sourceArtworkUrl || match.artworkUrl} /><div><strong>{match.title}</strong><small>{match.artist}{match.album ? ` · ${match.album}` : ""}</small>{#if playlistNames[match.externalSnapshotId]?.length}<small class="playlist-names">{playlistNames[match.externalSnapshotId].join(", ")}</small>{/if}</div></div>
                  </td>
                  <td class="desktop-cell"><div class="plays-as">{#if target}<span><ProviderMark id={target.providerId === "local" ? backend.toLowerCase() : target.providerId} definition={provider(target.providerId)} />{providerName(target.providerId)}</span>{#if target.title !== match.title}<small>{target.title || "Metadata unavailable"}</small>{/if}{:else}<span class="no-match">No match yet</span>{/if}</div></td>
                  <td class="desktop-cell"><StatePill tone={state.tone} text={state.text} /></td>
                  <td class="desktop-cell"><div class="difference-chips" title={differences.join(" · ") || undefined}>{#if target}{#each differences as difference}<span>{difference}</span>{:else}<span class="same-song">✓ Same song</span>{/each}{/if}</div></td>
                  <td class="desktop-cell actions-cell"><div class="row-actions">{@render rowMenu(match)}<Button variant="ghost" size="icon" class="row-chevron" aria-label={`Show match for ${match.title || "song"}`} onclick={() => openMatch(match)}><ChevronRight size={16} aria-hidden="true" /></Button></div></td>
                {/if}
              </tr>
            {/each}</tbody>
          </table>
        {:else}<EmptyState title="No songs in this filter" description="Try another filter or search." />{/if}
      </div>
      <nav class="playlist-pagination mapping-pagination" aria-label="Match review pages"><span>{data.pagination.total} {data.pagination.total === 1 ? "song" : "songs"}</span><div>
        <Button variant="text" disabled={page <= 1} onclick={() => goToPage(page - 1)}>Previous</Button><span>Page {page} of {data.pagination.totalPages}</span><Button variant="text" disabled={page >= data.pagination.totalPages} onclick={() => goToPage(page + 1)}>Next</Button>
      </div></nav>
      {#if chosen.size}<div class="mapping-bulk" role="toolbar" aria-label="Actions for selected songs"><strong>{chosen.size} selected</strong><div>
        <Button disabled={Boolean(action) || !bulkAccept.length} onclick={() => void bulkAcceptBest()}>Accept</Button>
        <Button variant="outline" disabled={Boolean(action)} onclick={() => void bulkRematch()}>Rematch</Button>
        <Button variant="text" disabled={Boolean(action) || !bulkReject.length} onclick={() => { bulkConfirm = "reject"; bulkConfirmOpen = true; }}>Reject</Button>
        {#if bulkShare.length}<Button variant="text" disabled={Boolean(action)} onclick={() => void bulkShareHousehold()}>Share with household ({bulkShare.length})</Button>{/if}
        {#if bulkClear.length}<Button variant="text" disabled={Boolean(action)} onclick={() => { bulkConfirm = "clear"; bulkConfirmOpen = true; }}>Clear my decisions ({bulkClear.length})</Button>{/if}
        <Button variant="text" disabled={Boolean(action)} onclick={() => chosen = new Map()}>Clear selection</Button>
      </div></div>{/if}
    </article>
  </section>
  <MatchesPanel bind:open={dialogOpen} match={selected} {providers} {backend} {acceptRequest} {lyricsRequest} playlists={selected ? playlistNames[selected.externalSnapshotId] ?? [] : []} onSaved={matchSaved} onReject={(match, scope) => confirm("reject", match, scope)} onRematch={rematch}
    onPrevious={visibleMatches.findIndex(item => item.externalSnapshotId === activeId) > 0 ? () => moveSelected(-1) : undefined}
    onNext={visibleMatches.findIndex(item => item.externalSnapshotId === activeId) < visibleMatches.length - 1 ? () => moveSelected(1) : undefined}>
    {#snippet authorities()}{#if selected?.manualAuthorities?.length}<section aria-label="Saved decisions"><h3 class="sec-label">Saved decisions</h3>
      {#each selected.manualAuthorities as authority}<div class="manual-authority-row"><strong>{authorityLabel(authority)}</strong><small>{authority.reason} · {authorityState(selected, authority)}</small><div>
        <Button variant="text" disabled={Boolean(action) || !canEditAuthority(authority)} onclick={() => confirmAuthority("manual_rematch", selected!, authority)}>Clear {authorityScopeOf(authority)} and rematch</Button>
        <Button variant="text" disabled={Boolean(action) || !canEditAuthority(authority)} onclick={() => confirmAuthority("manual_delete", selected!, authority)}>Clear {authorityScopeOf(authority)}</Button>
      </div></div>{/each}
    </section>{/if}{/snippet}
  </MatchesPanel>
  <BulkRematchDialog kind="automatic" bind:open={rematchAllOpen} onQueued={rematchAllQueued} />
  <ConfirmDialog bind:open={bulkConfirmOpen} title={bulkConfirm === "reject" ? `Reject ${songs(bulkReject.length)}?` : `Clear your decisions for ${songs(bulkClear.length)}?`} description={bulkConfirm === "reject" ? "Their current candidates are recorded as rejected for you. You can rematch them later." : "Your personal decisions stop applying; household decisions or automatic matching take over. Audit history is kept."} confirmLabel={bulkConfirm === "reject" ? "Reject songs" : "Clear decisions"} onConfirm={applyBulkConfirm} />
  <ConfirmDialog bind:open={destructiveOpen} preventScroll={!dialogOpen} title={confirmationCopy().title} description={confirmationCopy().description} confirmLabel={confirmationCopy().label} onConfirm={applyDestructive} />
{/if}
<Dialog.Root bind:open={shortcutsOpen}><Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="shortcuts-card"><Dialog.Title>Matches shortcuts</Dialog.Title><Dialog.Description>Use these keys while you are outside a text field.</Dialog.Description>
  <dl>{#each [["↑ / ↓", "Previous / next song"], ["Enter", "Open song"], ["A", "Use selected version and choose scope"], ["R", "Rematch selected song"], ["/", "Focus search"], ["Esc", "Close panel"], ["?", "Show shortcuts"]] as [key, label]}<div><dt><kbd>{key}</kbd></dt><dd>{label}</dd></div>{/each}</dl>
  <Dialog.Close class="icon-button" aria-label="Close shortcuts"><X size={18} /></Dialog.Close>
</Dialog.Content></Dialog.Portal></Dialog.Root>

<style>
  .mapping-queue { padding: var(--space-5); overflow: visible; }
  .mapping-heading { display: grid; gap: var(--space-3); margin-bottom: var(--space-5); }
  .mapping-heading h2 { margin: 0; font-size: 1.5rem; }
  .matches-controls { display: grid; gap: var(--space-3); margin-bottom: var(--space-4); min-width: 0; }
  .matches-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); min-width: 0; }
  .matches-toolbar :global(.matches-search) { min-width: 280px; flex: 1 1 280px; }
  .matches-toolbar :global(.matches-search input) { width: 100%; min-width: 0; height: var(--control-md); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-panel); color: var(--color-ink); font-size: .8125rem; }
  .matches-toolbar :global(.select-root) { flex: 0 0 auto; }
  .matches-toolbar :global(.select-trigger) { width: auto; gap: var(--space-2); padding-inline: var(--space-2); background: var(--color-panel); color: var(--color-ink); font-size: .8125rem; white-space: nowrap; }
  .matches-toolbar :global([data-slot="button"]) { padding-inline: var(--space-2); }
  .matches-toolbar :global(.matches-refresh) { width: 32px; height: 32px; padding: 0; color: var(--color-ink-muted); }
  .matches-toolbar :global(.matches-refresh span) { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  .mapping-rows { overflow: auto; min-width: 0; }
  .matches-table { width: 100%; table-layout: fixed; border-collapse: collapse; font-size: .8125rem; }
  .matches-table th { text-align: left; font-weight: 500; color: var(--color-ink-muted); padding: var(--space-2); font-size: .75rem; border-bottom: 1px solid var(--color-edge); }
  .matches-table th:nth-last-child(5) { width: 32%; }.matches-table th:nth-last-child(4) { width: 20%; }.matches-table th:nth-last-child(3) { width: 84px; }.matches-table th:last-child { width: 80px; }
  .mapping-row { display: table-row; position: relative; height: 64px; padding: 0; border: 0; color: var(--color-ink); }
  .mapping-row td { padding: var(--space-2); border-bottom: 1px solid var(--color-edge); vertical-align: middle; }
  .mapping-row:hover { background: color-mix(in srgb, var(--color-ink) 5%, transparent); }
  .mapping-row.chosen { background: color-mix(in srgb, var(--color-signal) 9%, transparent); }
  .mapping-row.needs-review .source-cell { box-shadow: inset 3px 0 var(--warning); }
  .open-match { position: absolute; inset: 0; width: 100%; border: 0; background: transparent; cursor: pointer; z-index: 1; border-radius: var(--radius-sm); }
  .open-match:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: -2px; }
  .source-copy, .plays-as, .difference-chips { pointer-events: none; position: relative; }
  .source-copy { display: flex; gap: var(--space-3); align-items: center; min-width: 0; }
  .source-copy :global(.media-art) { width: 36px; height: 36px; flex: none; }.source-copy > div { min-width: 0; }
  .source-copy strong, .source-copy small, .plays-as small { display: block; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
  .source-copy strong, .plays-as { color: var(--color-ink); }
  small { color: var(--color-ink-muted); font-size: .75rem; }
  .plays-as > span { display: flex; gap: var(--space-2); align-items: center; }
  .plays-as .no-match { color: var(--color-ink-muted); }
  .difference-chips { display: flex; flex-wrap: wrap; align-content: start; gap: var(--space-1); max-height: calc(2.8em + 12px + var(--space-1)); overflow: hidden; font-size: .6875rem; line-height: 1.4; }
  .difference-chips span { max-width: 100%; overflow: hidden; border-radius: var(--radius-sm); background: var(--color-panel-raised); color: var(--color-ink-muted); padding: 3px 6px; text-overflow: ellipsis; white-space: nowrap; }
  .difference-chips .same-song { color: var(--success); background: transparent; }
  .actions-cell { position: relative; }
  .row-actions { display: flex; min-height: 44px; align-items: center; justify-content: flex-end; }
  .row-actions :global(button), :global(.row-menu) { position: relative; z-index: 2; width: 32px; height: 32px; min-height: var(--touch-target-min, 32px); padding: 0; color: var(--color-ink-muted); }
  .check-cell { width: 44px; position: relative; }.check-cell .select-target { position: relative; z-index: 2; }
  .select-target { display: inline-flex; gap: var(--space-2); min-height: 44px; min-width: 44px; align-items: center; justify-content: center; cursor: pointer; }
  input[type="checkbox"] { accent-color: var(--color-signal); width: 18px; height: 18px; }
  .mapping-select-bar { display: flex; flex-wrap: wrap; gap: var(--space-2); align-items: center; }
  .mapping-bulk { position: sticky; z-index: var(--layer-control); bottom: var(--space-3); background: var(--md-sys-color-secondary-container); color: var(--md-sys-color-on-secondary-container); border-radius: var(--radius-lg); padding: var(--space-3); display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: var(--space-3); box-shadow: var(--shadow-md); }
  .mapping-bulk > div { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .quiet-success :global(.empty-state) { min-height: 0; align-items: flex-start; padding: var(--space-2); text-align: start; }
  .quiet-success :global(h3) { color: var(--success); font-size: .8125rem; font-weight: 400; }
  .phone-cell { display: none; }
  .manual-authority-row { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--space-2); padding-block: var(--space-3); border-bottom: 1px solid var(--color-edge); }
  .manual-authority-row > div { display: flex; flex-wrap: wrap; }
  :global(.shortcuts-card) { position: fixed; z-index: var(--layer-dialog); top: 50%; left: 50%; transform: translate(-50%, -50%); width: min(420px, calc(100vw - 32px)); background: var(--color-panel); padding: var(--space-6); border-radius: var(--radius-xl); }
  :global(.shortcuts-card dl > div) { display: flex; gap: var(--space-4); margin-block: var(--space-2); }:global(.shortcuts-card dd) { margin: 0; }:global(.shortcuts-card > .icon-button) { position: absolute; top: var(--space-2); right: var(--space-2); }
  @media (min-width: 900px) { .panel-open { margin-right: min(480px, 40vw); } }
  @media (min-width: 1100px) { .matches-toolbar { flex-wrap: nowrap; }.panel-open .matches-toolbar { flex-wrap: wrap; } }
  @media (max-width: 899px) {
    .mapping-queue { padding: var(--space-3); }.matches-toolbar { flex-wrap: wrap; }.matches-table, .matches-table tbody { display: block; }.matches-table thead, .desktop-cell { display: none; }.mapping-row { display: block; height: auto; }.mapping-row .phone-cell { display: block; padding: 0; border: 0; }.matches-table { table-layout: auto; }
    .mapping-bulk { bottom: calc(var(--mobile-nav-height) + var(--space-2)); }.mapping-pagination { flex-wrap: wrap; gap: var(--space-2); }.mapping-pagination > div { display: flex; align-items: center; }
  }
</style>
