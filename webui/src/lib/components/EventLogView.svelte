<script lang="ts">
  import { onMount, tick } from "svelte";
  import { flip } from "svelte/animate";
  import { ArrowRight, ChevronDown, CircleAlert, RefreshCw } from "@lucide/svelte";
  import { adminUrl } from "$lib/admin-url";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import {
    eventLog,
    home,
    type ActivityItem,
    type ProviderDefinition,
  } from "$lib/api";
  import {
    activityAction,
    activityCategories,
    activityCategory,
    activityIcon,
    dayLabel,
    filterActivity,
    groupActivity,
    groupOutcome,
    groupSeverity,
    humanize,
    mergeActivity,
    outcomeClass,
    relativeTime,
  } from "$lib/activity";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ActivityIcon from "$lib/components/ActivityIcon.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { formatDuration } from "$lib/playlists";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  let { administrator = false }: { administrator?: boolean } = $props();

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
  let expanded = $state(new Set<string>());
  let streamedIds = $state(new Set<string>());
  let streamAnnouncement = $state("");
  let reducedMotion = $state(false);
  let streamTimer: ReturnType<typeof setTimeout> | null = null;

  const filtered = $derived(filterActivity(items, {
    query,
    category,
    provider: providerFilter,
    problemsOnly,
  }));
  const groups = $derived(groupActivity(filtered));
  const days = $derived.by(() => {
    const result: Array<{ label: string; groups: typeof groups }> = [];
    for (const group of groups) {
      const label = dayLabel(group.entries[0].occurredAt);
      const current = result.at(-1);
      if (current?.label === label) current.groups.push(group);
      else result.push({ label, groups: [group] });
    }
    return result;
  });
  const presentCategories = $derived(activityCategories.filter((item) =>
    items.some((entry) => activityCategory(entry.kind) === item.id)));
  const eventProviders = $derived(unique(items.map((item) => item.providerId)));
  const filtering = $derived(Boolean(query || category || providerFilter || problemsOnly));

  function unique(values: Array<string | null | undefined>) {
    return [...new Set(values.filter((value): value is string => Boolean(value)))].toSorted();
  }

  const provider = (providerId?: string | null) => findProviderDefinition(providers, providerId);

  function providerName(providerId?: string | null) {
    if (!providerId) return "System";
    if (providerId === "library") return backend;
    return findProviderDefinition(providers, providerId)?.name ?? humanize(providerDisplayName(providers, providerId));
  }

  async function load(mode: "initial" | "refresh" | "older" = "refresh") {
    if (refreshing || loadingEarlier) return;
    if (mode === "older") loadingEarlier = true;
    else refreshing = true;
    error = "";
    try {
      const response = await eventLog.list(mode === "older"
        ? { limit: 50, before: cursor, beforeId: cursorId }
        : { limit: 50 });
      if (mode === "initial") items = response.items;
      else if (mode === "older") items = mergeActivity(items, response.items);
      else {
        const knownIds = new Set(items.map((item) => item.id));
        const added = response.items.filter((item) => !knownIds.has(item.id));
        items = mergeActivity(items, response.items);
        if (added.length) {
          streamedIds = new Set(added.map((item) => item.id));
          const announcement = `${added.length} new ${added.length === 1 ? "event" : "events"} streamed in.`;
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
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The Event log is unavailable.";
    } finally {
      loading = false;
      refreshing = false;
      loadingEarlier = false;
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
  }

  function clockTime(value: string) {
    return new Intl.DateTimeFormat(undefined, { timeStyle: "short" }).format(new Date(value));
  }

  function sentence(value?: string | null) {
    const text = humanize(value);
    return text.charAt(0) + text.slice(1).toLowerCase();
  }

  function groupSubtitle(entries: ActivityItem[]) {
    const first = entries[0];
    if (first.kind === "matching" && entries.length > 1) {
      const matched = entries.filter((item) => outcomeClass(item.state) === "accepted").length;
      const playlists = [...new Set(entries.map((item) => item.playlistName).filter(Boolean))];
      return [playlists.join(", "), `${matched} matched`, entries.length - matched ? `${entries.length - matched} need review` : ""]
        .filter(Boolean).join(" · ");
    }
    return [first.providerId ? providerName(first.providerId) : "", first.detail].filter(Boolean).join(" · ");
  }

  function groupBadge(entries: ActivityItem[], state: string) {
    if (state !== "mixed") return sentence(state);
    const succeeded = entries.filter((item) => outcomeClass(item.state) === "accepted").length;
    return `${succeeded} of ${entries.length} done`;
  }

  function rememberExpanded(key: string, open: boolean) {
    const next = new Set(expanded);
    if (open) next.add(key);
    else next.delete(key);
    expanded = next;
  }

  function fullTime(value: string) {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: "medium",
      timeStyle: "short",
    }).format(new Date(value));
  }

  function technical(item: ActivityItem) {
    const fields: Array<[string, string | null | undefined]> = [
      ["ISRC", item.isrc],
      ["Source provider ID", item.sourceProviderTrackId],
      ["Target provider ID", item.targetProviderTrackId],
      ["Media server item ID", item.backendItemId],
      ["Route decision", item.routeDecisionId],
      ["Correlation ID", item.correlationId],
      ["Actor", item.actorUserId],
      ...Object.entries(item.technicalDetails ?? {}),
    ];
    return fields.filter((entry, index) =>
      entry[1] && fields.findIndex((candidate) =>
        candidate[0].toLowerCase() === entry[0].toLowerCase()) === index);
  }

  onMount(() => {
    const motionPreference = window.matchMedia("(prefers-reduced-motion: reduce)");
    const syncMotion = (event: MediaQueryListEvent) => reducedMotion = event.matches;
    reducedMotion = motionPreference.matches;
    motionPreference.addEventListener("change", syncMotion);
    void loadProviders();
    void load("initial");
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      motionPreference.removeEventListener("change", syncMotion);
      unsubscribe();
      refreshScheduler.cancel();
      if (streamTimer) clearTimeout(streamTimer);
    };
  });
</script>

{#if loading}
  <Skeleton class="panel event-log-panel skeleton-panel" aria-label="Loading Event log" aria-busy="true" />
{:else if error && !items.length}
  <RouteError
    eyebrow={administrator ? "Event log unavailable" : "Activity unavailable"}
    title={administrator ? "Allstarr could not load durable activity." : "Allstarr could not load your activity."}
    message={error}
    onRetry={() => load("initial")}
  />
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
        <SearchField bind:value={query} label="Search" placeholder="Search songs, playlists or sources" hiddenLabel />
      </form>
      <div class="event-log-chips" role="group" aria-label="Category">
        <button type="button" class="filter-chip" aria-pressed={category === ""} onclick={() => category = ""}>All</button>
        {#each presentCategories as item (item.id)}
          <button type="button" class="filter-chip" aria-pressed={category === item.id} onclick={() => category = category === item.id ? "" : item.id}>{item.label}</button>
        {/each}
        <span class="event-log-chip-divider" aria-hidden="true"></span>
        <button type="button" class="filter-chip problems" aria-pressed={problemsOnly} onclick={() => problemsOnly = !problemsOnly}><CircleAlert size={16} aria-hidden="true" />Problems only</button>
      </div>
      <div class="event-log-toolbar-end">
        {#if eventProviders.length > 1}
          <SelectField bind:value={providerFilter} label="Provider" class="event-log-provider" options={[
            { value: "", label: "All sources" }, ...eventProviders.map((value) => ({ value, label: providerName(value) })),
          ]} />
        {/if}
        <Button variant="secondary" class="event-log-refresh" aria-label={refreshing ? "Refreshing…" : "Refresh"} disabled={refreshing || loadingEarlier} onclick={() => void load("refresh")}>
          <RefreshCw size={16} aria-hidden="true" /><span>{refreshing ? "Refreshing…" : "Refresh"}</span>
        </Button>
      </div>
    </div>

    {#each days as day (day.label)}
      <section class="event-log-day" aria-label={day.label}>
        <h2>{day.label}</h2>
        <div class="event-log-list">
          {#each day.groups as group (group.key)}
            {@const first = group.entries[0]}
            {@const groupState = groupOutcome(group.entries)}
            {@const severityState = groupSeverity(group.entries)}
            <details
              class="event-log-group"
              class:streaming={group.entries.some((item) => streamedIds.has(item.id))}
              animate:flip={{ duration: reducedMotion ? 0 : 260 }}
              open={expanded.has(group.key)}
              ontoggle={(event) => rememberExpanded(group.key, event.currentTarget.open)}
            >
              <summary>
                <span class="event-kind-icon" data-severity={severityState} data-kind={first.kind} aria-hidden="true">
                  <span><ActivityIcon kind={first.kind} /></span>
                  {#if first.artworkUrl}
                    <img src={adminUrl(first.artworkUrl)} alt="" loading="lazy" onerror={(event) => event.currentTarget.remove()} />
                  {/if}
                </span>
                <span class="event-summary-copy">
                  <strong>{group.entries.length > 1 ? group.title : sentence(first.label)}</strong>
                  {#if first.kind === "matching" && group.entries.length === 1}
                    <span class="event-route">
                      <span>{first.sourceTitle || first.detail}</span>
                      <ArrowRight size={14} aria-hidden="true" />
                      <span class:unresolved={!first.targetProviderId}>{first.targetTitle || "No playable match"}</span>
                      <small>{providerName(first.providerId)} → {first.targetProviderId ? providerName(first.targetProviderId) : "unmatched"}</small>
                    </span>
                  {:else}
                    <small>{groupSubtitle(group.entries)}</small>
                  {/if}
                </span>
                <span class="event-summary-end">
                  <Badge state={outcomeClass(groupState)}>{groupBadge(group.entries, groupState)}</Badge>
                  <time datetime={first.occurredAt} title={fullTime(first.occurredAt)}>{clockTime(first.occurredAt)}</time>
                </span>
                <ChevronDown class="event-chevron" size={18} aria-hidden="true" />
              </summary>

              <div class="event-children">
                {#each group.entries as item}
                  {@const action = activityAction(item, providerName)}
                  {@const details = technical(item)}
                  <article class="event-child">
                    <MediaArtwork class="event-art" url={item.artworkUrl} fallback={activityIcon(item.kind)} />
                    <div class="event-child-copy">
                      <header>
                        <strong>{item.kind === "matching" ? (item.sourceTitle || item.detail) : sentence(item.label)}</strong>
                        <time datetime={item.occurredAt}>{fullTime(item.occurredAt)}</time>
                      </header>
                      {#if item.kind === "matching"}
                        <p>{item.sourceArtist ? `${item.sourceArtist} · ` : ""}{providerName(item.providerId)}
                          <ArrowRight size={13} aria-hidden="true" />
                          {item.targetTitle || "No playable match"}{item.confidenceLabel ? ` · ${item.confidenceLabel} confidence` : ""}</p>
                      {:else}
                        <p>{item.detail}</p>
                      {/if}
                      <div class="event-child-meta">
                        <Badge state={outcomeClass(item.state)}>{sentence(item.state)}</Badge>
                        {#if item.durationMilliseconds}<span>Took {formatDuration(item.durationMilliseconds)}</span>{/if}
                        {#if item.playlistName}<span>{item.playlistName}</span>{/if}
                      </div>
                      {#if action || details.length}
                        <div class="event-child-actions">
                          {#if action}<Button variant="secondary" size="sm" href={action.href}>{action.label}</Button>{/if}
                          {#if details.length}
                            <details class="event-technical">
                              <summary>Technical details</summary>
                              <dl>
                                {#each details as [name, value]}
                                  <div><dt>{humanize(name)}</dt><dd>{value}</dd></div>
                                {/each}
                              </dl>
                            </details>
                          {/if}
                        </div>
                      {/if}
                    </div>
                  </article>
                {/each}
              </div>
            </details>
          {/each}
        </div>
      </section>
    {:else}
      <div class="compact-empty event-log-empty">
        <strong>{items.length ? "Nothing matches these filters" : "No activity yet"}</strong>
        <p>{items.length
          ? "Clear the filters to see everything that's loaded."
          : administrator
            ? "Matches, playlist updates, source checks and background jobs will show up here."
            : "Your listening, playlists and matches will show up here."}</p>
        {#if items.length}<Button variant="secondary" onclick={resetFilters}>Clear filters</Button>{/if}
      </div>
    {/each}

    {#if filtering && groups.length}
      <p class="event-log-count">Showing {filtered.length} of {items.length} loaded events · <Button variant="link" size="sm" onclick={resetFilters}>Clear filters</Button></p>
    {/if}

    {#if hasMore}
      <nav class="event-log-pagination" aria-label="Event log pages">
        <Button variant="secondary" disabled={loadingEarlier} onclick={() => void load("older")}>
          {loadingEarlier ? "Loading…" : "Load earlier events"}
        </Button>
      </nav>
    {/if}
  </section>
{/if}
