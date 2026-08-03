<script lang="ts">
  import { onMount } from "svelte";
  import { home, playlistLinks } from "$lib/api";
  import { activityIcon, humanize } from "$lib/activity";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import { summarizeHome, type HomeSnapshot } from "$lib/home";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let { administrator }: { administrator: boolean } = $props();

  let snapshot = $state<HomeSnapshot | null>(null);
  let loading = $state(true);
  let refreshing = $state(false);
  let pendingRefresh = false;
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  const summary = $derived(snapshot ? summarizeHome(snapshot) : null);
  const completelyUnavailable = $derived(
    snapshot !== null &&
      !snapshot.status &&
      !snapshot.playlists &&
      !snapshot.playlistLinks &&
      !snapshot.jobs &&
      !snapshot.activity &&
      !snapshot.providers,
  );

  async function refresh() {
    if (refreshing) {
      pendingRefresh = true;
      return;
    }

    refreshing = true;
    const requests = [
      ["Provider catalog", home.schema()],
      ["Runtime status", home.status()],
      ["Playlist inventory", home.playlists()],
      ["Linked playlists", playlistLinks.list()],
      ["Jobs", home.jobs()],
      ...(administrator
        ? [
            ["Recent activity", home.activity()],
            ["Provider health", home.providers()],
          ]
        : []),
    ] as const;
    const results = await Promise.allSettled(requests.map((request) => request[1]));
    const next: HomeSnapshot = { failures: [] };

    results.forEach((result, index) => {
      const label = requests[index][0];
      if (result.status === "rejected") {
        next.failures.push(`${label}: ${result.reason instanceof Error ? result.reason.message : "Unavailable"}`);
        return;
      }

      if (label === "Provider catalog") next.providerCatalog = (result.value as Awaited<ReturnType<typeof home.schema>>).providers;
      if (label === "Runtime status") next.status = result.value as Awaited<ReturnType<typeof home.status>>;
      if (label === "Playlist inventory") next.playlists = result.value as Awaited<ReturnType<typeof home.playlists>>;
      if (label === "Linked playlists") next.playlistLinks = (result.value as Awaited<ReturnType<typeof playlistLinks.list>>).playlistLinks;
      if (label === "Jobs") next.jobs = (result.value as Awaited<ReturnType<typeof home.jobs>>).jobs;
      if (label === "Recent activity") next.activity = (result.value as Awaited<ReturnType<typeof home.activity>>).items;
      if (label === "Provider health") next.providers = (result.value as Awaited<ReturnType<typeof home.providers>>).providers;
    });

    snapshot = next;
    loading = false;
    refreshing = false;
    if (pendingRefresh) {
      pendingRefresh = false;
      scheduleRefresh();
    }
  }

  function scheduleRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void refresh();
    }, 250);
  }

  function relativeTime(value?: string | null) {
    if (!value) return "Not checked";
    const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1_000);
    const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
    const minutes = Math.round(seconds / 60);
    if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
    return formatter.format(Math.round(hours / 24), "day");
  }

  function providerDefinition(providerId: string) {
    return snapshot?.providerCatalog?.find(
      (provider) => provider.id.toLowerCase() === providerId.toLowerCase(),
    );
  }

  function providerName(providerId: string) {
    return providerDefinition(providerId)?.name ?? providerId;
  }

  function accountName(providerId: string, displayName?: string | null) {
    return displayName?.startsWith("Legacy .env import")
      ? providerName(providerId)
      : displayName || providerName(providerId);
  }

  function activityDetail(value: string) {
    return /[_-]/.test(value) ? humanize(value) : value;
  }

  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if loading}
  <section class="home-grid" aria-busy="true" aria-label="Loading Home">
    {#each Array(4) as _}
      <div class="metric-card skeleton-card"></div>
    {/each}
    <div class="panel skeleton-panel"></div>
  </section>
{:else if completelyUnavailable}
  <RouteError
    eyebrow="Home unavailable"
    title="Allstarr could not load its current state."
    message={snapshot?.failures[0] ?? "The server did not return a usable response."}
    onRetry={refresh}
  />
{:else if snapshot && summary}
  {#if snapshot.failures.length}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Some live data is unavailable.</strong> {snapshot.failures.join(" · ")}</p>
      <button type="button" onclick={() => void refresh()}>Retry</button>
    </div>
  {/if}

  <section class="home-grid" aria-label="Allstarr overview" aria-busy={refreshing}>
    <article class="metric-card">
      <span class="metric-icon backend" aria-hidden="true">◇</span>
      <div>
        <p>Media server</p>
        <strong>{snapshot.status?.backendType ?? "Unknown"}</strong>
      </div>
      <small class:attention={snapshot.status?.durableStorage?.readiness !== "Ready"}>
        {snapshot.status?.durableStorage?.readiness ?? "Unavailable"}
      </small>
    </article>

    <article class="metric-card">
      <span class="metric-icon playlists" aria-hidden="true">♫</span>
      <div class="split-metric">
        <a href="#/library/playlists"><p>Linked</p><strong>{summary.managed}</strong></a>
        <a href="#/library/playlists"><p>Not linked</p><strong>{summary.unmanaged}</strong></a>
      </div>
      <small>{summary.managed + summary.unmanaged} playlists in the library</small>
    </article>

    <article class="metric-card">
      <span class="metric-icon playable" aria-hidden="true">▶</span>
      <div>
        <p>Playable tracks</p>
        <strong>{summary.playable.toLocaleString()}</strong>
      </div>
      <small class:attention={summary.unresolved > 0}>
        {summary.unresolved
          ? `${summary.unresolved.toLocaleString()} awaiting a match`
          : "Every indexed track has a route"}
      </small>
    </article>

    <article class="metric-card">
      <span class="metric-icon jobs" aria-hidden="true">↻</span>
      <div>
        <p>Active work</p>
        <strong>{summary.activeJobs}</strong>
      </div>
      <small class:attention={summary.activeJobs > 0}>
        {summary.activeJobs ? "Operations are running" : "Queue is clear"}
      </small>
    </article>
  </section>

  <section class="home-columns">
    <article class="panel home-panel">
      <header>
        <div>
          <p class="eyebrow">Sources</p>
          <h2>Provider readiness</h2>
        </div>
        <a href="#/sources">Manage</a>
      </header>

      {#if snapshot.providers?.length}
        <div class="provider-list">
          {#each snapshot.providers.slice(0, 6) as provider}
            <div class="provider-line">
              <ProviderMark
                id={provider.providerId}
                definition={providerDefinition(provider.providerId)}
              />
              <span>
                <strong>{accountName(provider.providerId, provider.connectedAccountName)}</strong>
                <small>
                  {provider.enabledAccountCount} connected account{provider.enabledAccountCount === 1 ? "" : "s"}
                  {provider.connectedAccountName?.startsWith("Legacy .env import") ? " · Imported" : ""}
                </small>
              </span>
              <span class:attention={provider.failedCapabilityCount > 0} class="provider-result">
                {provider.capabilityTotal
                  ? `${provider.healthyCapabilityCount}/${provider.capabilityTotal}`
                  : "—"}
                <small>{relativeTime(provider.lastCheckedAt)}</small>
              </span>
            </div>
          {/each}
        </div>
      {:else}
        <div class="compact-empty">
          <strong>No provider checks yet</strong>
          <p>Connect a Source to see its capability health here.</p>
          <a href="#/sources">Open Sources</a>
        </div>
      {/if}
    </article>

    <article class="panel home-panel">
      <header>
        <div>
          <p class="eyebrow">Now</p>
          <h2>Recent activity</h2>
        </div>
        <a href="#/activity">View all</a>
      </header>

      {#if snapshot.activity?.length}
        <div class="activity-list">
          {#each snapshot.activity as item}
            <a href="#/activity" class="activity-line">
              <span
                class="activity-artwork"
                class:failed={["failed", "degraded", "unavailable"].includes(item.state.toLowerCase())}
                aria-hidden="true"
              >{activityIcon(item.kind)}</span>
              <span>
                <strong>{humanize(item.label)}</strong>
                <small>{providerName(item.source)} · {activityDetail(item.detail)}</small>
              </span>
              <time datetime={item.occurredAt}>{relativeTime(item.occurredAt)}</time>
            </a>
          {/each}
        </div>
      {:else}
        <div class="compact-empty">
          <strong>No recent activity</strong>
          <p>Provider checks, matches, and background work will appear here.</p>
        </div>
      {/if}
    </article>
  </section>
{/if}
