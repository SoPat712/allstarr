<script lang="ts">
  import { onMount } from "svelte";
  import {
    eventLog,
    home,
    type ActivityItem,
    type ProviderDefinition,
  } from "$lib/api";
  import {
    activityIcon,
    activityLink,
    filterActivity,
    groupActivity,
    groupOutcome,
    groupSeverity,
    humanize,
    mergeActivity,
    outcomeClass,
  } from "$lib/activity";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import { formatDuration } from "$lib/playlists";
  import { liveUpdates } from "$lib/live-updates.svelte";

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
  let kind = $state("");
  let outcome = $state("");
  let providerFilter = $state("");
  let severity = $state("");
  let expanded = $state(new Set<string>());
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  const filtered = $derived(filterActivity(items, {
    query,
    kind,
    outcome,
    provider: providerFilter,
    severity,
  }));
  const groups = $derived(groupActivity(filtered));
  const kinds = $derived(unique(items.map((item) => item.kind)));
  const outcomes = $derived(unique(items.map((item) => item.state)));
  const eventProviders = $derived(unique(items.map((item) => item.providerId)));
  const severities = $derived(unique(items.map((item) => item.severity || "info")));
  const filtering = $derived(Boolean(query || kind || outcome || providerFilter || severity));

  function unique(values: Array<string | null | undefined>) {
    return [...new Set(values.filter((value): value is string => Boolean(value)))].toSorted();
  }

  function provider(providerId?: string | null) {
    return providers.find((item) =>
      item.id.toLowerCase() === providerId?.toLowerCase());
  }

  function providerName(providerId?: string | null) {
    if (!providerId) return "System";
    if (providerId === "library") return backend;
    return provider(providerId)?.name ?? humanize(providerId);
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
      else items = mergeActivity(items, response.items);
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

  function scheduleRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void load("refresh");
    }, 250);
  }

  function resetFilters() {
    query = "";
    kind = "";
    outcome = "";
    providerFilter = "";
    severity = "";
  }

  function rememberExpanded(key: string, open: boolean) {
    const next = new Set(expanded);
    if (open) next.add(key);
    else next.delete(key);
    expanded = next;
  }

  function relativeTime(value: string) {
    const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1_000);
    const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
    const minutes = Math.round(seconds / 60);
    if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
    return formatter.format(Math.round(hours / 24), "day");
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
      ["Backend item ID", item.backendItemId],
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
    void loadProviders();
    void load("initial");
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if loading}
  <section class="panel event-log-panel skeleton-panel" aria-label="Loading Event log" aria-busy="true"></section>
{:else if error && !items.length}
  <RouteError
    eyebrow="Event log unavailable"
    title="Allstarr could not load durable activity."
    message={error}
    onRetry={() => load("initial")}
  />
{:else}
  {#if error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>New events could not be loaded.</strong> {error}</p>
      <button type="button" onclick={() => void load("refresh")}>Retry</button>
    </div>
  {/if}

  <section class="panel event-log-panel" aria-busy={refreshing}>
    <header class="playlist-toolbar event-log-heading">
      <div>
        <p class="eyebrow">Durable activity</p>
        <h2>Event log</h2>
        <p>Matching, playlists, providers, jobs, and administrative changes.</p>
      </div>
      <button class="button-secondary" type="button" onclick={() => void load("refresh")}>Refresh</button>
    </header>

    <form class="playlist-filters event-log-filters" onsubmit={(event) => event.preventDefault()}>
      <SearchField bind:value={query} label="Search" placeholder="Event, track, provider, or correlation" />
      <label>
        <span>Category</span>
        <select bind:value={kind}>
          <option value="">All categories</option>
          {#each kinds as value}<option value={value}>{humanize(value)}</option>{/each}
        </select>
      </label>
      <label>
        <span>Outcome</span>
        <select bind:value={outcome}>
          <option value="">All outcomes</option>
          {#each outcomes as value}<option value={value}>{humanize(value)}</option>{/each}
        </select>
      </label>
      <label>
        <span>Provider</span>
        <select bind:value={providerFilter}>
          <option value="">All providers</option>
          {#each eventProviders as value}<option value={value}>{providerName(value)}</option>{/each}
        </select>
      </label>
      <label>
        <span>Severity</span>
        <select bind:value={severity}>
          <option value="">All severities</option>
          {#each severities as value}<option value={value}>{humanize(value)}</option>{/each}
        </select>
      </label>
    </form>

    <div class="event-log-count">
      <span>{filtered.length} of {items.length} loaded events</span>
      {#if filtering && groups.length}<button type="button" onclick={resetFilters}>Reset filters</button>{/if}
    </div>

    <div class="event-log-list">
      {#each groups as group (group.key)}
        {@const first = group.entries[0]}
        {@const groupState = groupOutcome(group.entries)}
        {@const severityState = groupSeverity(group.entries)}
        <details
          class="event-log-group"
          open={expanded.has(group.key)}
          ontoggle={(event) => rememberExpanded(group.key, event.currentTarget.open)}
        >
          <summary>
            <span class="event-kind-icon" data-severity={severityState} aria-hidden="true">
              <span>{activityIcon(first.kind)}</span>
              {#if first.artworkUrl}
                <img src={first.artworkUrl} alt="" loading="lazy" onerror={(event) => event.currentTarget.remove()} />
              {/if}
            </span>
            <span class="event-summary-copy">
              <span>
                <strong>{group.title}</strong>
                {#if group.entries.length > 1}<small>{group.entries.length} events</small>{/if}
              </span>
              {#if first.kind === "matching"}
                <span class="event-route">
                  <span>
                    {#if first.providerId}<ProviderMark id={first.providerId} definition={provider(first.providerId)} />{/if}
                    <span><small>{providerName(first.providerId)}</small><strong>{first.sourceTitle || first.detail}</strong></span>
                  </span>
                  <i aria-hidden="true">→</i>
                  <span class:unresolved={!first.targetProviderId}>
                    {#if first.targetProviderId}
                      <ProviderMark id={first.targetProviderId === "library" ? backend.toLowerCase() : first.targetProviderId} definition={provider(first.targetProviderId)} label={providerName(first.targetProviderId)} />
                    {:else}<b aria-hidden="true">?</b>{/if}
                    <span><small>{providerName(first.targetProviderId)}</small><strong>{first.targetTitle || "No playable match"}</strong></span>
                  </span>
                </span>
              {:else}
                <small>{providerName(first.providerId)} · {first.detail}</small>
              {/if}
            </span>
            <span class={`status-pill ${outcomeClass(groupState)}`}>{humanize(groupState)}</span>
            <time datetime={first.occurredAt}>{relativeTime(first.occurredAt)}</time>
            <span class="event-chevron" aria-hidden="true">›</span>
          </summary>

          <div class="event-children">
            {#each group.entries as item}
              {@const link = activityLink(item)}
              {@const details = technical(item)}
              <article class="event-child">
                <MediaArtwork class="event-art" url={item.artworkUrl} fallback={activityIcon(item.kind)} />
                <div class="event-child-copy">
                  <header>
                    <strong>{humanize(item.label)}</strong>
                    <time datetime={item.occurredAt}>{fullTime(item.occurredAt)}</time>
                  </header>
                  {#if item.kind === "matching"}
                    <p>{item.sourceArtist ? `${item.sourceArtist} · ` : ""}{item.sourceTitle || item.detail}</p>
                    <div class="event-child-route">
                      <span>{providerName(item.providerId)}</span>
                      <i aria-hidden="true">→</i>
                      <strong>{item.targetTitle || "No playable match"}</strong>
                      {#if item.confidenceLabel}<span>{item.confidenceLabel}</span>{/if}
                    </div>
                  {:else}
                    <p>{item.detail}</p>
                  {/if}
                  <div class="event-child-meta">
                    <span class={`status-pill ${outcomeClass(item.state)}`}>{humanize(item.state)}</span>
                    {#if item.durationMilliseconds}<span>{formatDuration(item.durationMilliseconds)}</span>{/if}
                    {#if item.playlistName}<span>{item.playlistName}</span>{/if}
                    {#if link}<a href={link}>Open related view</a>{/if}
                  </div>
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
              </article>
            {/each}
          </div>
        </details>
      {:else}
        <div class="compact-empty event-log-empty">
          <strong>{items.length ? "No events match these filters" : "No events have been recorded"}</strong>
          <p>{items.length
            ? "Reset the current filters to return to the complete loaded history."
            : "Allstarr returned zero durable events. New matching, playlist, provider, and job activity will appear here."}</p>
          {#if items.length}<button class="button-secondary" type="button" onclick={resetFilters}>Reset filters</button>{/if}
        </div>
      {/each}
    </div>

    <nav class="playlist-pagination event-log-pagination" aria-label="Event log pages">
      <span>{items.length} events retained in this view</span>
      {#if hasMore}
        <button type="button" disabled={loadingEarlier} onclick={() => void load("older")}>
          {loadingEarlier ? "Loading…" : "Load earlier events"}
        </button>
      {/if}
    </nav>
  </section>
{/if}
