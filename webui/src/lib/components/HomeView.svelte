<script lang="ts">
  import { onMount } from "svelte";
  import { home, playlistLinks } from "$lib/api";
  import { activityIcon, humanize, relativeTime } from "$lib/activity";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Progress } from "$lib/components/ui/progress";
  import { Badge } from "$lib/components/ui/badge";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { playbackSourceIssues, summarizeHome, type HomeSnapshot } from "$lib/home";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  let { administrator }: { administrator: boolean } = $props();

  let snapshot = $state<HomeSnapshot | null>(null);
  let loading = $state(true);
  let refreshing = $state(false);
  let pendingRefresh = false;
  let nowPlayingTimer: ReturnType<typeof setInterval> | null = null;

  const summary = $derived(snapshot ? summarizeHome(snapshot) : null);
  const sourceIssues = $derived(playbackSourceIssues(snapshot?.providerCatalog ?? []));
  const completelyUnavailable = $derived(
    snapshot !== null &&
      !snapshot.status &&
      !snapshot.playlists &&
      !snapshot.playlistLinks &&
      !snapshot.jobs &&
      !snapshot.activity &&
      !snapshot.providers &&
      !snapshot.nowPlaying,
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
            ["Now playing", home.nowPlaying()],
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
      if (label === "Now playing") next.nowPlaying = (result.value as Awaited<ReturnType<typeof home.nowPlaying>>).items;
    });

    snapshot = next;
    loading = false;
    refreshing = false;
    if (pendingRefresh) {
      pendingRefresh = false;
      scheduleRefresh();
    }
  }

  const refreshScheduler = createRefreshScheduler(refresh);
  const scheduleRefresh = refreshScheduler.schedule;

  async function refreshNowPlaying() {
    if (!administrator || !snapshot) return;
    try {
      const response = await home.nowPlaying();
      snapshot = { ...snapshot, nowPlaying: response.items };
    } catch {
      // Keep the last known playback state; the normal refresh surfaces persistent failures.
    }
  }

  const providerDefinition = (providerId: string) =>
    findProviderDefinition(snapshot?.providerCatalog ?? [], providerId);

  function providerName(providerId: string) {
    return providerDisplayName(snapshot?.providerCatalog ?? [], providerId);
  }

  function accountName(providerId: string, displayName?: string | null) {
    return displayName?.startsWith("Legacy .env import")
      ? providerName(providerId)
      : displayName || providerName(providerId);
  }

  function activityDetail(value: string) {
    return /[_-]/.test(value) ? humanize(value) : value;
  }

  function clockTime(seconds?: number | null) {
    if (seconds === undefined || seconds === null || !Number.isFinite(seconds)) return "—";
    const whole = Math.max(0, Math.floor(seconds));
    return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
  }

  function initials(name: string) {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map((part) => part[0]).join("").toUpperCase() || "?";
  }

  onMount(() => {
    void refresh();
    if (administrator) nowPlayingTimer = setInterval(() => void refreshNowPlaying(), 5_000);
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
      if (nowPlayingTimer) clearInterval(nowPlayingTimer);
    };
  });
</script>

{#if loading}
  <section class="home-grid" aria-busy="true" aria-label="Loading Home">
    {#each Array(4) as _}
      <Skeleton class="metric-card skeleton-card" />
    {/each}
    <Skeleton class="panel skeleton-panel" />
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
      <Button variant="secondary" size="sm" onclick={() => void refresh()}>Retry</Button>
    </div>
  {/if}

  {#if sourceIssues.length}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p>
        <strong>{sourceIssues[0].providerName} needs attention.</strong>
        {humanize(sourceIssues[0].reason)}{sourceIssues.length > 1 ? ` · ${sourceIssues.length - 1} more` : ""}
      </p>
      <Button variant="secondary" size="sm" href={`#/integrations/services?source=${encodeURIComponent(sourceIssues[0].providerId)}&section=configuration`}>Open service</Button>
    </div>
  {/if}

  <section class="home-grid" aria-label="Allstarr overview" aria-busy={refreshing}>
    <article class="metric-card">
      <span class="metric-icon backend" aria-hidden="true">◇</span>
      <div>
        <p>Media server</p>
        <strong>{snapshot.status?.backendType ?? "Unknown"}</strong>
      </div>
      <Badge state={snapshot.status?.durableStorage?.readiness === "Ready" ? "healthy" : "degraded"}>
        {snapshot.status?.durableStorage?.readiness ?? "Unavailable"}
      </Badge>
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

  {#if administrator}
    <section class="panel now-playing-panel" aria-label="Now playing">
      <header>
        <div>
          <p class="eyebrow">Listening now</p>
          <h2>Now playing</h2>
        </div>
        <Badge state={snapshot.nowPlaying?.length ? "healthy" : ""}>
          {snapshot.nowPlaying?.length ?? 0} active
        </Badge>
      </header>

      {#if snapshot.nowPlaying?.length}
        <div class="now-playing-rail" aria-label="Active listeners">
          {#each snapshot.nowPlaying as item (item.deviceId)}
            <article class="now-playing-card">
              <div class="listener-profile">
                <span class="listener-avatar" aria-label={`${item.userName} profile`}>
                  <span aria-hidden="true">{initials(item.userName)}</span>
                  {#if item.avatarUrl}
                    <img src={item.avatarUrl} alt="" onerror={(event) => event.currentTarget.remove()} />
                  {/if}
                </span>
                <span>
                  <strong>{item.userName}</strong>
                  <small>{item.client}{item.device ? ` · ${item.device}` : ""}</small>
                </span>
              </div>

              <div class="now-playing-track">
                <span class="now-playing-artwork">
                  {#if item.artworkUrl}
                    <img src={item.artworkUrl} alt="" />
                  {:else}
                    <span aria-hidden="true">♫</span>
                  {/if}
                </span>
                <span class="track-copy">
                  <strong>{item.title}</strong>
                  <small>{item.artist}{item.album ? ` · ${item.album}` : ""}</small>
                </span>
              </div>

              <div class="now-playing-facts">
                <Badge>{providerName(item.providerId)}</Badge>
                <span class="scrobble-state" class:complete={item.scrobbled}>
                  <span aria-hidden="true">{item.scrobbled ? "✓" : "○"}</span>
                  {item.scrobbled ? "Scrobbled" : "Not scrobbled"}
                </span>
              </div>

              <div class="playback-progress">
                <Progress class="min-w-0 flex-1" max={1} value={item.progress ?? 0} aria-label={`Playback progress for ${item.title}`} />
                <span>{clockTime(item.positionSeconds)} / {clockTime(item.durationSeconds)}</span>
              </div>
            </article>
          {/each}
        </div>
      {:else}
        <div class="now-playing-empty">
          <strong>Nothing is playing right now.</strong>
          <span>Active Jellyfin music sessions will appear here.</span>
        </div>
      {/if}
    </section>
  {/if}

  <section class="home-columns">
    <article class="panel home-panel">
      <header>
        <div>
          <p class="eyebrow">Sources</p>
          <h2>Provider readiness</h2>
        </div>
        <a href="#/integrations/services">Manage</a>
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
          <a href="#/integrations/services">Open Services</a>
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
