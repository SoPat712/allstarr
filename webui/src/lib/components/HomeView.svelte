<script lang="ts">
  import { onMount } from "svelte";
  import { ChevronRight, CircleAlert, CircleCheck, CircleDashed, Headphones, KeyRound, Music } from "@lucide/svelte";
  import { adminUrl } from "$lib/admin-url";
  import { home } from "$lib/api";
  import { humanize, relativeTime } from "$lib/activity";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import ActivityIcon from "$lib/components/ActivityIcon.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { playbackSourceIssues, summarizeHome, type HomeSnapshot } from "$lib/home";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { accountSubtitle, findProviderDefinition, providerDisplayName } from "$lib/sources";

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
      !snapshot.stats &&
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
      ["Home overview", home.overview()],
      ["Now playing", home.nowPlaying()],
    ] as const;
    const results = await Promise.allSettled(requests.map((request) => request[1]));
    const next: HomeSnapshot = { failures: [] };

    results.forEach((result, index) => {
      const label = requests[index][0];
      if (result.status === "rejected") {
        next.failures.push(`${label}: ${result.reason instanceof Error ? result.reason.message : "Unavailable"}`);
        return;
      }

      if (label === "Home overview") {
        const overview = result.value as Awaited<ReturnType<typeof home.overview>>;
        next.providerCatalog = overview.schema.providers;
        next.status = overview.status;
        next.stats = overview.stats;
        next.activity = overview.activity.items;
        next.providers = overview.providerHealth.providers;
      }
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
    if (!snapshot) return;
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

  function deliveryComplete(kind: string, state: string) {
    return kind === "completed" && ["delivered", "ignored"].includes(state);
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

  function plural(count: number, one: string, many = `${one}s`) {
    return `${count.toLocaleString()} ${count === 1 ? one : many}`;
  }

  function listenTrend(current = 0, previous = 0) {
    const change = current - previous;
    if (!change) return "the same as last week";
    return `${Math.abs(change).toLocaleString()} ${change > 0 ? "more" : "fewer"} than last week`;
  }

  const attention = $derived.by(() => {
    if (!snapshot || !summary) return [];
    const items: Array<{ key: string; title: string; detail: string; href: string; action: string }> = [];
    if (summary.unresolved > 0)
      items.push({
        key: "unresolved",
        title: `${plural(summary.unresolved, "song")} ${summary.unresolved === 1 ? "is" : "are"} waiting for a match`,
        detail: "Listeners can see them, but they can't play until a match is confirmed.",
        href: "#/library/mappings",
        action: "Review matches",
      });
    if (administrator)
      for (const issue of sourceIssues)
        items.push({
          key: `source:${issue.providerId}`,
          title: `${issue.providerName} can't play songs`,
          detail: humanize(issue.reason),
          href: `#/integrations/services/${encodeURIComponent(issue.providerId)}`,
          action: `Open ${issue.providerName}`,
        });
    const storageReadiness = snapshot.status?.durableStorage?.readiness;
    if (administrator && snapshot.status && storageReadiness !== "Ready")
      items.push({
        key: "storage",
        title: "Allstarr's database isn't ready",
        detail: storageReadiness ? `Current state: ${humanize(storageReadiness)}` : "Its state couldn't be read.",
        href: "#/settings/backups",
        action: "Open backups",
      });
    for (const provider of snapshot.providers ?? [])
      if (provider.failedCapabilityCount > 0 && !sourceIssues.some((issue) => issue.providerId === provider.providerId))
        items.push({
          key: `health:${provider.providerId}`,
          title: `${providerName(provider.providerId)} is failing ${plural(provider.failedCapabilityCount, "check")}`,
          detail: `${provider.healthyCapabilityCount} of ${provider.capabilityTotal} checks passed ${relativeTime(provider.lastCheckedAt, "")}`.trim(),
          href: `#/integrations/services/${encodeURIComponent(provider.providerId)}`,
          action: "Open service",
        });
    return items;
  });

  onMount(() => {
    void refresh();
    nowPlayingTimer = setInterval(() => void refreshNowPlaying(), 5_000);
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
      if (nowPlayingTimer) clearInterval(nowPlayingTimer);
    };
  });
</script>

{#if loading}
  <section class="home-loading" aria-busy="true" aria-label="Loading Home">
    {#each Array(4) as _}
      <Skeleton class="skeleton-card" />
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
<div class="home-view">
  {#if snapshot.failures.length}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Some live data is unavailable.</strong> {snapshot.failures.join(" · ")}</p>
      <Button variant="secondary" size="sm" disabled={refreshing} onclick={() => void refresh()}>{refreshing ? "Trying again…" : "Retry"}</Button>
    </div>
  {/if}

  <section class="home-now" aria-label="Now playing" aria-live="off">
    {#if snapshot.nowPlaying?.length}
      {#each snapshot.nowPlaying as item, index (`${item.userId ?? "unknown"}:${item.deviceId}`)}
        <article class="now-playing-card" class:hero={index === 0}>
          {#if index === 0 && item.artworkUrl}
            <div class="now-playing-backdrop" aria-hidden="true" style={`--artwork: url("${adminUrl(item.artworkUrl)}")`}></div>
          {/if}
          <span class="now-playing-artwork">
            {#if item.artworkUrl}
              <img src={adminUrl(item.artworkUrl)} alt="" />
            {:else}
              <Music size={index === 0 ? 40 : 24} aria-hidden="true" />
            {/if}
          </span>
          <div class="now-playing-body">
            <div class="now-playing-title">
              <h2>{item.title}</h2>
              <p>{item.artist}{item.album ? ` · ${item.album}` : ""}</p>
            </div>

            <div class="playback-progress">
              <span class="playback-progress-track" style={`--scrobble-at: ${Math.min(100, ((item.scrobbleThresholdSeconds ?? 0) / (item.durationSeconds || 1)) * 100)}%`}>
                <span class="playback-bar" role="progressbar" aria-label={`Playback progress for ${item.title}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round((item.progress ?? 0) * 100)}>
                  <span style={`width: ${Math.min(100, Math.max(0, (item.progress ?? 0) * 100))}%`}></span>
                </span>
                {#if item.scrobbleThresholdSeconds}
                  <i title={`Scrobble at ${clockTime(item.scrobbleThresholdSeconds)}`} aria-label={`Scrobble threshold at ${clockTime(item.scrobbleThresholdSeconds)}`}></i>
                {/if}
              </span>
              <span class="playback-time">{clockTime(item.positionSeconds)} / {clockTime(item.durationSeconds)}</span>
            </div>

            <div class="now-playing-meta">
              <span class="listener-profile">
                <span class="listener-avatar" aria-label={`${item.userName} profile`}>
                  <span aria-hidden="true">{initials(item.userName)}</span>
                  {#if item.avatarUrl}
                    <img src={adminUrl(item.avatarUrl)} alt="" onerror={(event) => event.currentTarget.remove()} />
                  {/if}
                </span>
                <span>
                  <strong>{item.userName}</strong>
                  <small>{item.client}{item.device ? ` · ${item.device}` : ""}</small>
                </span>
              </span>
              <span class="playback-route">
                <ProviderMark id={item.providerId} definition={providerDefinition(item.providerId)} />
                <span>
                  <small title={item.routeReason ?? undefined}>{item.sourceConfirmed ? (item.cached ? "Cached from" : "Playing from") : "Catalog source · playback unconfirmed"}</small>
                  <strong>{providerName(item.providerId)}{item.sourceConfirmed && item.providerAccountName ? ` · ${item.providerAccountName}` : ""}</strong>
                </span>
              </span>
              <span class="scrobble-state" class:complete={item.scrobbled}>
                {#if item.scrobbled}<CircleCheck size={16} aria-hidden="true" />{:else}<CircleDashed size={16} aria-hidden="true" />{/if}
                {item.scrobbled ? "Scrobbled" : item.scrobbleEligible ? "Scrobbling" : "Listening"}
              </span>
            </div>

            {#if item.scrobbleDeliveries?.length}
              <div class="scrobble-targets" aria-label="Scrobble delivery targets">
                {#each item.scrobbleDeliveries as delivery (delivery.targetId)}
                  <span class:complete={deliveryComplete(delivery.kind, delivery.state)} class:attention={delivery.requiresReauthentication} title={delivery.message || humanize(delivery.state)}>
                    <ProviderMark id={delivery.targetId} definition={providerDefinition(delivery.targetId)} />
                    <small>{providerName(delivery.targetId)}</small>
                    {#if delivery.requiresReauthentication}<KeyRound size={13} aria-label="Reconnect required" />{/if}
                    {#if deliveryComplete(delivery.kind, delivery.state)}<CircleCheck size={13} aria-label="Delivered" />{/if}
                  </span>
                {/each}
              </div>
            {/if}
          </div>
        </article>
      {/each}
    {:else}
      <div class="now-playing-empty">
        <span class="now-playing-artwork" aria-hidden="true"><Headphones size={32} /></span>
        <div>
          <h2>Nothing is playing</h2>
          <p>{administrator ? "When someone plays music through Allstarr, it shows up here." : "When you play music through Allstarr, it shows up here."}</p>
        </div>
      </div>
    {/if}
  </section>

  {#if attention.length}
    <section class="panel home-attention" aria-labelledby="home-attention-title">
      <h2 id="home-attention-title">Needs your attention</h2>
      <ul>
        {#each attention as item (item.key)}
          <li>
            <CircleAlert size={20} aria-hidden="true" />
            <span><strong>{item.title}</strong><small>{item.detail}</small></span>
            <Button variant="secondary" size="sm" href={item.href}>{item.action}</Button>
          </li>
        {/each}
      </ul>
    </section>
  {/if}

  <section class="panel home-overview-panel" aria-label="Overview" aria-busy={refreshing}>
    <div class="home-listening">
      <h2>This week</h2>
      {#if !snapshot.stats}
        <p class="home-sentence">Listening stats aren't available right now.</p>
      {:else if !snapshot.stats.currentWeekListens}
        <p class="home-sentence">No songs finished this week yet.</p>
      {:else}
        <p class="home-sentence">
          <strong>{plural(snapshot.stats.currentWeekListens, "song")}</strong> finished this week,
          {listenTrend(snapshot.stats.currentWeekListens, snapshot.stats.previousWeekListens)}.
          {#if snapshot.stats.topArtist?.name}
            Most played this month: <strong>{snapshot.stats.topArtist.name}</strong>, {plural(snapshot.stats.topArtist.listens, "play")}.
          {/if}
        </p>
      {/if}
      {#if snapshot.stats}
        <p class="home-sentence-detail">{plural(snapshot.stats.completedListens, "song")} in the last 24 hours · {plural(snapshot.stats.scrobbleDeliveries, "scrobble")} sent</p>
      {/if}
    </div>

    <dl class="home-facts">
      <a href="#/library/playlists">
        <dt>Playlists</dt>
        <dd>{plural(summary.managed, "playlist")}</dd>
        <ChevronRight size={16} aria-hidden="true" />
      </a>
      <a href="#/library/mappings">
        <dt>Playable songs</dt>
        <dd>{summary.playable.toLocaleString()} of {(summary.playable + summary.unresolved).toLocaleString()}</dd>
        <ChevronRight size={16} aria-hidden="true" />
      </a>
      <a href="#/activity">
        <dt>Background work</dt>
        <dd>{summary.activeJobs ? `${plural(summary.activeJobs, "job")} running` : "Idle"}</dd>
        <ChevronRight size={16} aria-hidden="true" />
      </a>
      {#if administrator}
        <a href="#/library/cached">
          <dt>Stored songs</dt>
          <dd>{snapshot.stats?.cacheTracks == null ? "—" : `${(snapshot.stats.cacheTracks + (snapshot.stats.keptTracks ?? 0)).toLocaleString()} (${snapshot.stats.cacheTracks} cached, ${snapshot.stats.keptTracks ?? 0} kept)`}</dd>
          <ChevronRight size={16} aria-hidden="true" />
        </a>
        <a href="#/integrations/services">
          <dt>Media server</dt>
          <dd>{snapshot.status?.backendType ?? "Unknown"}</dd>
          <ChevronRight size={16} aria-hidden="true" />
        </a>
      {/if}
    </dl>
  </section>

  <section class="home-columns">
    <article class="panel home-panel">
      <header>
        <h2>Sources</h2>
        <a href="#/integrations/services">Manage</a>
      </header>

      {#if snapshot.providers?.length}
        <div class="provider-list">
          {#each snapshot.providers.slice(0, 6) as provider}
            <a class="provider-line" href={`#/integrations/services/${encodeURIComponent(provider.providerId)}`}>
              <ProviderMark
                id={provider.providerId}
                definition={providerDefinition(provider.providerId)}
              />
              <span>
                <strong>{providerName(provider.providerId)}</strong>
                <small>
                  {provider.connectedAccounts?.length
                    ? provider.connectedAccounts.map(accountSubtitle).join("; ")
                    : plural(provider.enabledAccountCount, "connected account")}
                </small>
              </span>
              <span class="provider-result" class:attention={provider.failedCapabilityCount > 0} class:healthy={provider.capabilityTotal > 0 && provider.failedCapabilityCount === 0}>
                {#if provider.capabilityTotal}
                  {provider.failedCapabilityCount ? `${provider.failedCapabilityCount} failing` : "Working"}
                  <small>{relativeTime(provider.lastCheckedAt, "Not checked yet")}</small>
                {:else}Not checked yet{/if}
              </span>
            </a>
          {/each}
        </div>
      {:else}
        <div class="compact-empty">
          <strong>No sources connected</strong>
          <p>Connect a music service to play songs you don't have locally.</p>
          <Button variant="secondary" size="sm" href="#/integrations/services">Connect a source</Button>
        </div>
      {/if}
    </article>

    <article class="panel home-panel">
      <header>
        <h2>Recent activity</h2>
        <a href="#/activity">View all</a>
      </header>

      {#if snapshot.activity?.length}
        <div class="activity-list">
          {#each snapshot.activity as item}
            <a href="#/activity" class="activity-line">
              <span
                class="activity-artwork"
                class:failed={["failed", "degraded", "unavailable"].includes(item.state.toLowerCase())}
                data-kind={item.kind}
                aria-hidden="true"
              ><ActivityIcon kind={item.kind} /></span>
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
          <strong>No activity yet</strong>
          <p>{administrator ? "Matches, playlist updates and source checks will show up here." : "Your listening, playlists and matches will show up here."}</p>
        </div>
      {/if}
    </article>
  </section>
</div>
{/if}
