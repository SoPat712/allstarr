<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { formatLocalDateTime } from "$lib/dates";
  import { countLabel } from "$lib/count-label";
  import { writeFilter } from "$lib/components/patterns/helpers";
  import { onMount, tick, untrack } from "svelte";
  import { flip } from "svelte/animate";
  import { Switch } from "$lib/components/ui/switch";
  import ActivityStatusIcon from "$lib/components/ActivityStatusIcon.svelte";
  import { ArrowRight } from "@lucide/svelte";
  import { adminUrl } from "$lib/admin-url";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import ActivityOutcome from "$lib/components/ActivityOutcome.svelte";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import {
    eventLog,
    home,
    type ActivityItem,
    type ProviderDefinition,
  } from "$lib/api";
  import {
    activityAction,
    activityHeadline,
    activityRecovery,
    jobState,
    activityText,
    activityDuration,
    activityCategory,
    isRoutineActivity,
    activityIcon,
    dayLabel,
    filterActivity,
    groupActivity,
    groupOutcome,
    groupSeverity,
    humanize,
    mergeActivity,
    outcomeClass,
  } from "$lib/activity";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ActivityIcon from "$lib/components/ActivityIcon.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { findProviderDefinition } from "$lib/sources";

  let { administrator = false, scope = "mine" }: { administrator?: boolean; scope?: "mine" | "everyone" } = $props();

  let opener: HTMLElement | null = null;
  let disposed = false;
  let earlierButton = $state<HTMLButtonElement | null>(null);
  let filterTopUpPending = false;
  let previousFilters = "";
  let items = $state<ActivityItem[]>([]);
  let providers = $state<ProviderDefinition[]>([]);
  let backend = $state("Local library");
  let loading = $state(true);
  let refreshing = $state(false);
  let loadingEarlier = $state(false);
  let error = $state("");
  let hasMore = $state(false);
  let cursor = $state("");
  let cursorId = $state("");
  let query = $state("");
  let category = $state("");
  let providerFilter = $state("");
  let problemsOnly = $state(false);
  let selectedKey = $state("");
  let panelOpen = $state(false);
  let segment = $state("all");
  let streamedIds = $state(new Set<string>());
  let streamAnnouncement = $state("");
  let preferencesReady = $state(false);
  let reducedMotion = $state(false);
  let streamTimer: ReturnType<typeof setTimeout> | null = null;

  // Routine background work stays out of the way unless someone asks for it or it went wrong.
  let showBackground = $state(false);
  const listenReceipts = $derived(new Set(items.filter(item => item.kind === "scrobble" && item.correlationId).map(item => `${item.actorUserId ?? "household"}|${item.correlationId}`)));
  const visibleItems = $derived(showBackground || scope === "everyone" || category === "system" || query || problemsOnly
    ? items
    : items.filter((item) => !isRoutineActivity(item) || item.correlationId && listenReceipts.has(`${item.actorUserId ?? "household"}|${item.correlationId}`)));
  const filtered = $derived(filterActivity(visibleItems, {
    query,
    category: category === "library" ? "" : category,
    provider: providerFilter,
    problemsOnly,
  }));
  const shown = $derived(filtered.filter(item => category !== "library" || item.kind === "library").filter(item => category !== "matching" || item.kind !== "library"));
  const groups = $derived(groupActivity(shown));
  const countFor = (id: string) => filterActivity(visibleItems, { query, category: id === "all" || id === "problems" || id === "library" ? "" : id, provider: providerFilter, problemsOnly: id === "problems" }).filter(item => id !== "library" || item.kind === "library").filter(item => id !== "matching" || item.kind !== "library").length;
  function selectFilter(next: string) { segment = next; problemsOnly = next === "problems"; category = ["all", "problems"].includes(next) ? "" : next; }

  const selectedGroup = $derived(groups.find(group => group.key === selectedKey));
  const days = $derived.by(() => {
    const result: Array<{ label: string; groups: typeof groups }> = [];
    for (const group of groups) {
      const label = dayLabel(group.entries[0].occurredAt, new Date(), { weekday: false });
      const current = result.at(-1);
      if (current?.label === label) current.groups.push(group);
      else result.push({ label, groups: [group] });
    }
    return result;
  });
  const eventProviders = $derived(unique(items.map((item) => item.providerId)));
  const filtering = $derived(Boolean(query || category || providerFilter || problemsOnly));

  function unique(values: Array<string | null | undefined>) {
    return [...new Set(values.filter((value): value is string => Boolean(value)))].toSorted();
  }

  const provider = (providerId?: string | null) => findProviderDefinition(providers, providerId);

  function providerName(providerId?: string | null) {
    if (!providerId) return "System";
    if (providerId === "library") return backend;
    return findProviderDefinition(providers, providerId)?.name ?? humanize(providerId);
  }

  async function load(mode: "initial" | "refresh" | "older" = "refresh") {
    if (disposed || refreshing || loadingEarlier) return;
    if (mode === "older") loadingEarlier = true;
    else refreshing = true;
    if (mode === "initial" && !items.length) loading = true;
    error = "";
    try {
      const knownIds = new Set(items.map((item) => item.id));
      const knownGroups = new Set(groups.map(group => group.key));
      for (let page = 0; page < (mode === "refresh" ? 1 : 8); page++) {
        if (disposed) return;
        const response = await eventLog.list(mode === "older" || page > 0
          ? { limit: 100, before: cursor, beforeId: cursorId, scope }
          : { limit: 100, scope });
        if (disposed) return;
        error = "";
        // The first page shows at once; earlier pages fill in below it.
        if (mode === "initial" && page === 0) { items = response.items; loading = false; }
        else if (mode !== "refresh") items = mergeActivity(items, response.items);
        else {
          const added = response.items.filter((item) => !knownIds.has(item.id));
          items = mergeActivity(items, response.items);
          if (added.length) {
            streamedIds = new Set(added.map((item) => item.id));
            const announcement = `${countLabel(added.length, "new event")} streamed in.`;
            if (streamAnnouncement === announcement) {
              streamAnnouncement = "";
              await tick();
            }
            streamAnnouncement = announcement;
            if (streamTimer) clearTimeout(streamTimer);
            streamTimer = setTimeout(() => {
              streamedIds = new Set();
              streamAnnouncement = "";
              streamTimer = null;
            }, 600);
          }
        }
        if (mode !== "refresh" || !cursor) {
          cursor = response.nextCursor || "";
          cursorId = response.nextCursorId || "";
          hasMore = response.hasMore;
        }
        if (mode === "refresh" || !hasMore || !cursor ||
          groups.filter(group => mode === "initial" || !knownGroups.has(group.key)).length >= 25) break;
      }
    } catch {
      if (disposed) return;
      error = "History could not be loaded.";
    } finally {
      loading = false;
      refreshing = false;
      loadingEarlier = false;
      topUpFilters();
    }
  }

  async function loadProviders() {
    try {
      const schema = await home.schema();
      providers = schema.providers;
      backend = schema.activeBackend || backend;
    } catch {
      // Event records remain readable with provider IDs when presentation metadata is unavailable.
    }
  }

  const refreshScheduler = createRefreshScheduler(() => load("refresh"));
  const scheduleRefresh = refreshScheduler.schedule;

  function resetFilters() {
    query = "";
    category = "";
    providerFilter = "";
    problemsOnly = false;
    segment = "all";
    writeFilter("activity.history." + scope, "all");
  }

  function clockTime(value: string) {
    if (Number.isNaN(Date.parse(value))) return "Time unavailable";
    return new Intl.DateTimeFormat(undefined, { timeStyle: "short" }).format(new Date(value));
  }

  function topUpFilters() {
    if (!filterTopUpPending || disposed || loading || refreshing || loadingEarlier) return;
    filterTopUpPending = false;
    if (hasMore && !error && groups.length < 25) void load("older");
  }

  $effect(() => {
    const filters = JSON.stringify([showBackground, problemsOnly, query, category, providerFilter]);
    if (previousFilters && filters !== previousFilters) filterTopUpPending = true;
    previousFilters = filters;
    untrack(topUpFilters);
  });

  $effect(() => {
    const button = earlierButton;
    if (!button || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting) && !disposed && hasMore &&
        !loading && !refreshing && !loadingEarlier && !error) void load("older");
    }, { rootMargin: "400px" });
    observer.observe(button);
    return () => observer.disconnect();
  });

  function sourceName(item: ActivityItem) {
    if (item.providerId === "library" && item.source !== "library") return item.source;
    return item.providerId ? providerName(item.providerId) : humanize(item.source || "system");
  }

  function groupSubtitle(entries: ActivityItem[]) {
    const first = entries[0];
    if (first.kind === "matching" && entries.length > 1) {
      const matched = entries.filter((item) => outcomeClass(item.state) === "accepted").length;
      const playlists = [...new Set(entries.map((item) => item.playlistName).filter(Boolean))];
      return [first.actorDisplayName || "Household", playlists.join(", "), `${matched} matched`, entries.length - matched ? `${entries.length - matched} need review` : ""]
        .filter(Boolean).join(" · ");
    }
    const title = first.sourceTitle || first.targetTitle || first.playlistName;
    const target = title && !first.detail.includes(title)
      ? [title, first.sourceArtist || first.targetArtist].filter(Boolean).join(" · ") : "";
    return [first.actorDisplayName || "Household", sourceName(first), target, activityText(first.detail)].filter(Boolean).join(" · ");
  }

  function groupBadge(entries: ActivityItem[], state: string) {
    if (state !== "mixed") return jobState(state);
    if (entries.every(item => item.kind !== "matching" && !item.jobId)) return `${humanize(groupSeverity(entries))} · ${countLabel(entries.length, "event")}`;
    const succeeded = entries.filter((item) => outcomeClass(item.state) === "accepted").length;
    return `${succeeded} of ${entries.length} done`;
  }

  function fullTime(value: string) {
    return formatLocalDateTime(value, "Time unavailable");
  }

  function technical(item: ActivityItem) {
    const fields: Array<[string, string | null | undefined]> = [
      ["Job reference", item.jobId],
      ["Source reference", item.providerId],
      ["Original details", activityText(item.detail) !== item.detail ? item.detail : null],
      ["ISRC", item.isrc],
      ["Source provider ID", item.sourceProviderTrackId],
      ["Target provider ID", item.targetProviderTrackId],
      ["Media server item ID", item.backendItemId],
      ["Route decision", item.routeDecisionId],
      ["Correlation ID", item.correlationId],
      ["Actor user ID", item.actorUserId],
      ...Object.entries(item.technicalDetails ?? {}),
    ];
    return fields.filter((entry, index) =>
      entry[1] && fields.findIndex((candidate) =>
        candidate[0].toLowerCase() === entry[0].toLowerCase()) === index);
  }

  $effect(() => {
    if (!preferencesReady) return;
    try { localStorage.setItem("activity.history.options." + scope, JSON.stringify({ query, provider: providerFilter, background: showBackground })); } catch {}
  });

  onMount(() => {
    try {
      const saved = JSON.parse(localStorage.getItem("activity.history.options." + scope) || "{}");
      if (typeof saved.query === "string") query = saved.query;
      if (typeof saved.provider === "string") providerFilter = saved.provider;
      if (typeof saved.background === "boolean") showBackground = saved.background;
    } catch {}
    preferencesReady = true;
    const motionPreference = window.matchMedia("(prefers-reduced-motion: reduce)");
    const syncMotion = (event: MediaQueryListEvent) => reducedMotion = event.matches;
    reducedMotion = motionPreference.matches;
    motionPreference.addEventListener("change", syncMotion);
    void loadProviders();
    void load("initial");
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      disposed = true;
      motionPreference.removeEventListener("change", syncMotion);
      unsubscribe();
      refreshScheduler.cancel();
      if (streamTimer) clearTimeout(streamTimer);
    };
  });
</script>

<PageStatus label="History status" items={[
  { label: loading ? "Loading history…" : countLabel(items.filter(item => dayLabel(item.occurredAt) === "Today").length, "event") + " today" },
  { label: countFor("problems") ? countFor("problems") + " need attention" : "Nothing failed", tone: countFor("problems") ? "warn" : "neutral" },
]} />

{#if loading}
  <div class="event-log-state"><RowsSkeleton label={administrator ? "Loading Event log" : "Loading Your activity"} /></div>
{:else if error && !items.length}
  <div class="event-log-state"><ErrorState title={error} label={administrator ? "Event log unavailable" : "Your activity unavailable"} onretry={() => load("initial")} /></div>
{:else}
  {#if error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>New events could not be loaded.</strong> {error}</p>
      <Button variant="secondary" size="sm" disabled={refreshing || loadingEarlier} onclick={() => void load("refresh")}>{refreshing ? "Trying again…" : "Retry"}</Button>
    </div>
  {/if}

  <section class="event-log-panel" aria-busy={refreshing} aria-label={administrator ? "Event log" : "Your activity"}>
    <p class="sr-only" aria-live="polite" aria-atomic="true">{streamAnnouncement}</p>
    <div class="event-log-toolbar">
      <form class="event-log-search" onsubmit={(event) => event.preventDefault()}>
        <SearchField bind:value={query} label="Search" placeholder="Search activity" hiddenLabel />
      </form>
      <div class="event-log-chip-row">
              <FilterSegments label="History filters" storageKey={"activity.history." + scope} value={segment} onchange={selectFilter} options={[
          { id: "all", label: "All", count: countFor("all") },
          { id: "problems", label: "Problems", count: countFor("problems") },
          { id: "playlists", label: "Playlists", count: countFor("playlists") },
          { id: "matching", label: "Matches", count: countFor("matching") },
          { id: "listening", label: "Listening", count: countFor("listening") },
          { id: "library", label: "Library", count: countFor("library") },
        ]} />
      </div>
      <RefreshButton class="event-log-refresh" disabled={loadingEarlier} onrefresh={() => load("refresh")} />
    </div>
    <AdvancedFold placement="standalone" class="event-more-filters" label={`More filters${providerFilter || showBackground || ["sources", "system"].includes(category) ? " · active" : ""}`}>
      <div class="more-filter-fields">
        {#if administrator}<label class="background-option"><span>Background work</span><Switch checked={showBackground} aria-label="Background work" onchange={(event) => showBackground = event.currentTarget.checked} /></label>{/if}
        <SelectField bind:value={providerFilter} label="Source" options={[
          { value: "", label: "All sources" }, ...eventProviders.map((value) => ({ value, label: providerName(value) })),
        ]} />
        <SelectField label="Activity type" value={category} onchange={(value) => selectFilter(value || "all")} options={[
          { value: "", label: "All types" }, { value: "playlists", label: "Playlists" }, { value: "matching", label: "Matches" },
          { value: "listening", label: "Listening" }, { value: "library", label: "Library" }, { value: "sources", label: "Sources" }, { value: "system", label: "System" },
        ]} />
      </div>
    </AdvancedFold>

    <div class="history-days">

    {#each days as day (day.label)}
      <section class="event-log-day" aria-label={day.label}>
        <h2>{day.label}</h2>
        <div class="event-log-list">
          {#each day.groups as group (group.key)}
            {@const first = group.entries[0]}
            {@const groupState = groupOutcome(group.entries)}
            {@const severityState = groupSeverity(group.entries)}
            <div class="event-log-group" class:streaming={group.entries.some(item => streamedIds.has(item.id))} animate:flip={{ duration: reducedMotion ? 0 : 260 }}>
              <ItemRow title={group.entries.length > 1 ? group.title : activityHeadline(first)} subtitle={groupSubtitle(group.entries)} selected={panelOpen && selectedKey === group.key} onclick={(event) => { opener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null; selectedKey = group.key; panelOpen = true; }}>
                {#snippet leading()}<ActivityStatusIcon state={groupState} severity={severityState} kind={first.kind} label={groupBadge(group.entries, groupState)} />{/snippet}
                {#snippet trailing()}<time datetime={first.occurredAt} title={fullTime(first.occurredAt)}>{clockTime(first.occurredAt)}</time>{/snippet}
              </ItemRow>
            </div>
          {/each}
        </div>
      </section>
    {:else}
      <EmptyState title={items.length ? "Nothing matches these filters" : "No activity yet"} description={items.length ? "Clear the filters to see everything that's loaded." : "Your listening, playlists, matches and background work will show up here."}>
        {#snippet icon()}<ActivityIcon kind="job" />{/snippet}
        {#snippet action()}{#if items.length}<Button variant="outline" onclick={resetFilters}>Clear filters</Button>{/if}{/snippet}
      </EmptyState>
    {/each}

    </div>
    {#if filtering && groups.length}
      <p class="event-log-count">{countLabel(groups.length, "result")} · <Button variant="link" size="sm" onclick={resetFilters}>Clear filters</Button></p>
    {/if}

    {#if hasMore}
      <nav class="event-log-pagination" aria-label="Event log pages">
        <Button bind:ref={earlierButton} variant="secondary" disabled={loadingEarlier || refreshing} onclick={() => void load("older")}>
          {loadingEarlier ? "Loading…" : "Load earlier events"}
        </Button>
      </nav>
    {/if}
  </section>
{/if}

{#if selectedGroup}
  <SidePanel bind:open={panelOpen} title={selectedGroup.entries.length > 1 ? selectedGroup.title : activityHeadline(selectedGroup.entries[0])} description={fullTime(selectedGroup.entries[0].occurredAt)} onclose={() => { selectedKey = ""; void tick().then(() => { if (opener?.isConnected) opener.focus({ preventScroll: true }); }); }}>
              <div class="event-children">
                {#each selectedGroup.entries as item}
                  {@const action = activityAction(item, providerName)}
                  {@const details = technical(item)}
                  <article class="event-child">
                    <MediaArtwork class="event-art" url={item.artworkUrl} fallback={activityIcon(item.kind)} />
                    <div class="event-child-copy">
                      <header>
                        <strong>{item.kind === "matching" ? (item.sourceTitle || activityText(item.detail)) : activityHeadline(item)}</strong>
                        <time datetime={item.occurredAt}>{fullTime(item.occurredAt)}</time>
                      </header>
                      {#if item.kind === "matching"}
                        <p>{item.sourceArtist ? `${item.sourceArtist} · ` : ""}{providerName(item.providerId)}
                          <ArrowRight size={13} aria-hidden="true" />
                          {item.targetTitle || "No playable match"}{item.confidenceLabel ? ` · ${item.confidenceLabel} confidence` : ""}</p>
                      {:else}
                        {#if (item.sourceTitle || item.targetTitle) && !item.detail.includes(item.sourceTitle || item.targetTitle || "")}<p><strong>{item.sourceTitle || item.targetTitle}</strong>{item.sourceArtist || item.targetArtist ? ` · ${item.sourceArtist || item.targetArtist}` : ""}</p>{/if}
                        <p>{activityText(item.detail)}</p>
                        {#if item.recovery}<p class="notice-copy"><strong>Next step:</strong> {activityRecovery(item.recovery)}</p>{/if}
                      {/if}
                      <div class="event-child-meta">
                        <span>{item.actorDisplayName || "Household"} · {sourceName(item)}</span>
                        <ActivityOutcome state={item.state} severity={item.severity} label={jobState(item.state)} />
                        {#if activityDuration(item.durationMilliseconds)}<span>Took {activityDuration(item.durationMilliseconds)}</span>{/if}
                        {#if item.playlistName}<span>{item.playlistName}</span>{/if}
                      </div>
                      {#if action || details.length}
                        <div class="event-child-actions">
                          {#if action}<Button variant="secondary" size="sm" href={action.href}>{action.label}</Button>{/if}
                          {#if details.length}
                            <AdvancedFold placement="flush" class="event-technical" label="Technical details">
                              <dl>
                                {#each details as [name, value]}
                                  <div><dt>{humanize(name)}</dt><dd>{value}</dd></div>
                                {/each}
                              </dl>
                            </AdvancedFold>
                          {/if}
                        </div>
                      {/if}
                    </div>
                  </article>
                {/each}
              </div>
  </SidePanel>
{/if}


<style>
  .event-log-state { margin-block-start: var(--space-4); }
  .event-log-panel { min-width: 0; grid-template-columns: minmax(0, 1fr); gap: 0; container-type: inline-size; }
  .event-log-toolbar { display: grid; grid-template-columns: minmax(0, 1fr) minmax(160px, 220px) auto; align-items: center; gap: var(--space-2); margin-block: var(--space-4) var(--space-1); }
  .event-log-search { min-width: 0; order: 2; }
  .event-log-chip-row { min-width: 0; order: 1; }
  .event-log-chip-row :global(.filter-segments) { width: 100%; flex-wrap: nowrap; overflow-x: auto; }
  .event-log-toolbar :global(.event-log-refresh) { order: 3; }
  :global(.event-more-filters) { margin-bottom: var(--space-2); }
  .more-filter-fields { display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-4); padding: var(--space-3) var(--space-4); }
  .background-option { display: flex; align-items: center; gap: var(--space-3); min-height: 44px; }
  .history-days { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); }
  .history-days:has(> :global(.empty-state)) { border-style: dashed; }
  .event-log-group { border: 0; border-bottom: 1px solid var(--color-edge); }
  .event-log-group + .event-log-group { border-top: 0; }
  .event-log-group:last-child { border-bottom: 0; }
  .event-log-group.streaming :global(.item-row) { animation: event-stream-arrive 600ms ease-out; }
  @media (prefers-reduced-motion: reduce) { .event-log-group.streaming :global(.item-row) { animation: none; } }
  .event-log-group time { white-space: nowrap; color: var(--color-ink-muted); font-size: var(--text-xs); font-variant-numeric: tabular-nums; }
  .event-children { display: grid; }
  .event-child { display: flex; gap: var(--space-3); padding: var(--space-3) 0; }
  .event-child:first-child { padding-top: 0; }
  .event-child + .event-child { border-top: 1px solid var(--color-edge); }
  .event-child-copy { min-width: 0; flex: 1; overflow-wrap: anywhere; }
  .event-child-copy header { display: grid; gap: var(--space-1); }
  .event-child-copy strong { font-size: var(--text-sm); }
  .event-child-copy p, .event-child-copy time, .event-child-meta { color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; }
  .event-child-meta, .event-child-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); align-items: center; margin-top: var(--space-2); }
  :global(.event-technical) { width: 100%; } :global(.event-technical) dd { margin: 0; overflow-wrap: anywhere; font-size: var(--text-xs); }
  .event-child :global(.event-art) { width: 36px; height: 36px; flex: none; }
  .event-log-pagination { display: flex; justify-content: center; padding-block: var(--space-4); }

  @container (max-width: 800px) {
    .event-log-toolbar { grid-template-columns: minmax(0, 1fr) auto; }
    .event-log-search { grid-column: 1; order: 1; }
    .event-log-chip-row { grid-column: 1 / -1; grid-row: 2; order: 3; }
    .event-log-toolbar :global(.event-log-refresh) { order: 2; }
    .more-filter-fields { align-items: stretch; }
    .more-filter-fields :global(.field) { flex: 1 1 180px; min-width: 0; }
  }
</style>
