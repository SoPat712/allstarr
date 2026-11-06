<script lang="ts">
  import { countLabel, countNoun } from "$lib/count-label";
  import { onMount } from "svelte";
  import { CircleCheck, CircleDashed, Headphones, KeyRound, Music, Pause } from "@lucide/svelte";
  import { adminUrl } from "$lib/admin-url";
  import { home, listeningStats, mixes, releaseInbox, settings, downloads, matchReview, playlistLinks, sources, activityWork, type ActivityTask, type MatchReviewResponse, type ListeningStats, type MixCard, type ReleaseInboxEntry, type StorageOverview } from "$lib/api";
  import { humanize, relativeTime, isRoutineActivity, isProblem, activityHeadline } from "$lib/activity";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import ActivityStatusIcon from "$lib/components/ActivityStatusIcon.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import HomeSetup from "$lib/components/HomeSetup.svelte";
  import MixShelf from "$lib/components/MixShelf.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import RecapBars from "$lib/components/RecapBars.svelte";
  import { listeningTime, recapTimeline } from "$lib/intelligence";
  import { Button } from "$lib/components/ui/button";
  import { homeSourceHealth, listenTrend, playbackMeta, playbackProgress, playbackSourceIssues, summarizeHome, type HomeSnapshot } from "$lib/home";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";
  import { reportHref } from "$lib/support";

  let { administrator }: { administrator: boolean } = $props();

  let week = $state<ListeningStats | null>(null);
  let shelf = $state<MixCard[]>([]);
  let releases = $state<ReleaseInboxEntry[]>([]);
  let audio = $state<{ bytes: number; maximum: number | null; keptBytes: number | null } | null>(null);
  let matchStats = $state<MatchReviewResponse["stats"] | null>(null);
  let storage = $state<StorageOverview | null>(null);
  let indexedSongs = $state<number | null>(null);
  let listeners = $state<number | null>(null);
  let tasks = $state<ActivityTask[]>([]);
  let extrasPending = false, extrasAt = 0, disposed = false;
  const daily = $derived(recapTimeline(week?.timeline ?? [], week?.timelineUnit ?? "day"));
  async function loadExtras() {
    if (extrasPending || Date.now() - extrasAt < 30_000) return;
    extrasPending = true;
    const results = await Promise.allSettled([listeningStats.get("7d"), mixes.list(), administrator ? Promise.resolve(null) : releaseInbox.get(), administrator ? downloads.list("cache") : Promise.resolve(null), administrator ? settings.storage() : Promise.resolve(null), administrator ? settings.config() : Promise.resolve(null), administrator ? downloads.list("kept") : Promise.resolve(null), administrator ? matchReview.list({ pageSize: 1 }) : Promise.resolve(null), administrator ? playlistLinks.libraryIndexCounts() : Promise.resolve(null), administrator ? sources.accounts() : Promise.resolve(null), administrator ? activityWork.tasks("everyone") : Promise.resolve(null)] as const);
    if (!disposed) {
      if (results[0].status === "fulfilled") week = results[0].value;
      if (results[1].status === "fulfilled" && Array.isArray(results[1].value)) shelf = results[1].value.filter(card => card.readiness.ready).sort((a, b) => Number(b.inApps) - Number(a.inApps)).slice(0, 6);
      if (results[2].status === "fulfilled" && results[2].value) releases = results[2].value.entries ?? [];
      if (results[3].status === "fulfilled" && typeof results[3].value?.totalSize === "number") {
        const config = results[5].status === "fulfilled" ? results[5].value : null;
        const configured = Number((config?.cache as Record<string, unknown> | undefined)?.audioMaximumGiB);
        const maximum = Number.isFinite(configured) && configured > 0 ? configured * 1024 ** 3 : null;
        audio = { bytes: results[3].value.totalSize, maximum, keptBytes: results[6].status === "fulfilled" ? results[6].value?.totalSize ?? null : null };
      }
      if (results[7].status === "fulfilled") matchStats = results[7].value?.stats ?? null;
      if (results[4].status === "fulfilled") storage = results[4].value;
      if (results[8].status === "fulfilled" && typeof results[8].value?.trackCount === "number") indexedSongs = results[8].value.trackCount;
      if (results[9].status === "fulfilled" && Array.isArray(results[9].value?.audienceUsers)) listeners = results[9].value.audienceUsers.length;
      if (results[10].status === "fulfilled" && Array.isArray(results[10].value?.items)) tasks = results[10].value.hasMore ? [] : results[10].value.items;
      extrasAt = Date.now();
    }
    extrasPending = false;
  }
  let snapshot = $state<HomeSnapshot | null>(null);
  let loading = $state(true);
  let refreshing = $state(false);
  let pendingRefresh = false;
  let nowPlayingRequest = 0;
  let appliedPlaybackRequest = 0;
  let nowPlayingTimer: ReturnType<typeof setInterval> | null = null;
  let playbackReference = $state<{ capturedAt: string; receivedAt: number } | null>(null);
  let playbackNow = $state(0);
  let visible = $state(false);
  let reducedMotion = $state(false);

  const summary = $derived(snapshot ? summarizeHome(snapshot) : null);
  const playing = $derived((snapshot?.nowPlaying ?? []).map(item => ({
    item,
    display: playbackProgress(item, playbackReference ? {
      capturedAt: playbackReference.capturedAt,
      elapsedSeconds: Math.max(0, (playbackNow - playbackReference.receivedAt) / 1000),
    } : null),
  })));
  const advancing = $derived(playing.some(row => row.display.advancing));
  $effect(() => {
    if (!visible || !advancing) return;
    const timer = setInterval(() => { playbackNow = performance.now(); }, reducedMotion ? 1_000 : 200);
    return () => clearInterval(timer);
  });
  const sourceHealth = $derived(snapshot ? homeSourceHealth(snapshot) : { checked: [], failing: [], slow: [], down: [] });
  const sourceIssues = $derived(playbackSourceIssues(snapshot?.providerCatalog ?? [], snapshot?.providers ?? []));
  const failingNames = $derived(sourceHealth.failing.map(provider => providerDisplayName(snapshot?.providerCatalog ?? [], provider.providerId)));
  const sourcesNeedingLook = $derived(new Set([...sourceHealth.failing.map(source => source.providerId), ...sourceIssues.map(source => source.providerId)]).size);
  const phoneSourceNames = $derived(failingNames.slice(0, 3).join(", ") + (failingNames.length > 3 ? ` and ${failingNames.length - 3} more` : ""));
  const completelyUnavailable = $derived(
    snapshot !== null &&
      !snapshot.status &&
      !snapshot.stats &&
      !snapshot.activity &&
      !snapshot.providers &&
      !snapshot.nowPlaying,
  );

  async function refresh(includePlayback = true) {
    if (refreshing) {
      pendingRefresh = true;
      return;
    }

    refreshing = true;
    if (includePlayback) extrasAt = 0;
    void loadExtras();
    const playbackRequest = ++nowPlayingRequest;
    const requests = [
      ["Home overview", home.overview()],
      ...(includePlayback ? [["Now playing", readNowPlaying()] as const] : []),
    ] as const;
    const results = await Promise.allSettled(requests.map((request) => request[1]));
    const next: HomeSnapshot = { ...snapshot, failures: [] };
    let nextPlayback: Awaited<ReturnType<typeof readNowPlaying>> | null = null;

    results.forEach((result, index) => {
      const label = requests[index][0];
      if (result.status === "rejected") {
        next.failures.push(`${label}: ${result.reason instanceof Error ? result.reason.message : "Unavailable"}`);
        if (label === "Now playing") next.nowPlaying = snapshot?.nowPlaying;
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
      if (label === "Now playing") {
        nextPlayback = result.value as Awaited<ReturnType<typeof readNowPlaying>>;
        next.nowPlaying = nextPlayback.items;
      }
    });

    if (playbackRequest < appliedPlaybackRequest) next.nowPlaying = snapshot?.nowPlaying;
    else if (nextPlayback) {
      appliedPlaybackRequest = playbackRequest;
      adoptPlaybackClock(nextPlayback);
    }
    snapshot = next;
    loading = false;
    refreshing = false;
    if (pendingRefresh) {
      pendingRefresh = false;
      scheduleRefresh();
    }
  }

  const refreshScheduler = createRefreshScheduler(() => refresh(false));
  const scheduleRefresh = refreshScheduler.schedule;

  async function readNowPlaying() {
    const response = await home.nowPlaying();
    return { ...response, receivedAt: performance.now() };
  }

  function adoptPlaybackClock(response: Awaited<ReturnType<typeof readNowPlaying>>) {
    playbackNow = performance.now();
    playbackReference = response.capturedAt ? { capturedAt: response.capturedAt, receivedAt: response.receivedAt } : null;
  }

  async function refreshNowPlaying() {
    if (!snapshot || !visible) return;
    const playbackRequest = ++nowPlayingRequest;
    try {
      const response = await readNowPlaying();
      if (playbackRequest < appliedPlaybackRequest) return;
      appliedPlaybackRequest = playbackRequest;
      adoptPlaybackClock(response);
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
    if (seconds === undefined || seconds === null || !Number.isFinite(seconds)) return "";
    const whole = Math.max(0, Math.floor(seconds));
    return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
  }

  const activitySource = (source: string | null | undefined) => {
    const name = source ? providerName(source) : "";
    return name && name !== "Unknown source" ? `${name} · ` : "";
  };

  const visibleActivity = $derived((snapshot?.activity ?? []).filter((item) => !isRoutineActivity(item) && (isProblem(item) || !/record[\s_-]+(?:a[\s_-]+)?listen/i.test(item.label))).slice(0, 4));

  const attention = $derived.by(() => {
    if (!snapshot || !summary) return [];
    const items: Array<{ key: string; title: string; detail: string; href: string; action: string }> = [];
    if (summary.unresolved > 0)
      items.push({
        key: "unresolved",
        title: countLabel(summary.unresolved, "song is waiting for a match", "songs are waiting for a match"),
        detail: "Listeners can see them, but they can't play until a match is confirmed.",
        href: "#/library/matches",
        action: "Review matches",
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
    if (failingNames.length)
      items.push({
        key: "health:sources",
        title: countLabel(failingNames.length, "source is failing checks", "sources are failing checks"),
        detail: failingNames.join(", ") + ". Playback falls back to the others.",
        href: "#/sources/services",
        action: "Review sources",
      });
    else if (sourceIssues.length)
      items.push({ key: "source:unavailable", title: `${sourceIssues[0].providerName} can't play songs`, detail: humanize(sourceIssues[0].reason), href: "#/sources/services", action: "Review sources" });
    return administrator ? items.toSorted((a, b) => Number(b.key.startsWith("health:") || b.key.startsWith("source:")) - Number(a.key.startsWith("health:") || a.key.startsWith("source:"))) : [];
  });

  onMount(() => {
    const motion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const syncMotion = () => { reducedMotion = motion.matches; };
    const syncVisibility = () => { visible = !document.hidden; playbackNow = performance.now(); };
    syncMotion();
    syncVisibility();
    motion.addEventListener("change", syncMotion);
    document.addEventListener("visibilitychange", syncVisibility);
    void refresh();
    nowPlayingTimer = setInterval(() => void refreshNowPlaying(), 5_000);
    const unsubscribe = liveUpdates.subscribe(update => {
      scheduleRefresh();
      if (update.reason !== "poll") void refreshNowPlaying();
    });
    return () => {
      disposed = true;
      unsubscribe();
      refreshScheduler.cancel();
      if (nowPlayingTimer) clearInterval(nowPlayingTimer);
      motion.removeEventListener("change", syncMotion);
      document.removeEventListener("visibilitychange", syncVisibility);
    };
  });
</script>

{#if loading}
  <RowsSkeleton label="Loading Home" />
{:else if completelyUnavailable}
  <ErrorState title="Home could not be loaded" detail={snapshot?.failures[0] ?? "The server did not answer."} onretry={refresh} />
{:else if snapshot && summary}
<div class="home-view">
  <PageStatus separated items={[
    { label: playing.length ? playing.every(row => row.item.isPaused === true) ? "Playback paused" : "Playing normally" : snapshot.nowPlaying === undefined ? "Playback status unavailable" : "Nothing playing" },
    ...(administrator ? [...(sourcesNeedingLook ? [{ label: countLabel(sourcesNeedingLook, "source needs a look", "sources need a look"), tone: "stop" as const }] : []), ...(snapshot.stats?.waitingJobs ? [{ label: `${countLabel(snapshot.stats.waitingJobs, "job")} waiting`, hideOnPhone: true }] : [])] : []),
  ]} />
  {#if snapshot.failures.length}<ErrorState title="Some live data is unavailable" detail={snapshot.failures.join(" · ")} onretry={refresh} />{/if}

  <section class="home-now" aria-label="Now playing" aria-live="off">
    {#if snapshot.nowPlaying?.length}
      {#each playing as { item, display }, index (`${item.userId ?? "unknown"}:${item.deviceId}`)}
        <article class="now-playing-card compact-playing">
          <span class="now-playing-artwork">
            {#if item.artworkUrl}
              <img src={adminUrl(item.artworkUrl)} alt="" />
            {:else}
              <Music size={32} aria-hidden="true" />
            {/if}
          </span>
          <div class="now-playing-body">
            <div class="now-playing-title">
              <h2>{item.title}</h2>
              <p>{item.artist}{item.album ? ` · ${item.album}` : ""}</p>
            </div>

            <p class="playback-meta" title={item.routeReason ?? undefined}>{playbackMeta(item, providerName(item.providerId))}</p>
            <div class="playback-progress">
              <span class="playback-progress-track" style={`--scrobble-at: ${Math.min(100, ((item.scrobbleThresholdSeconds ?? 0) / (item.durationSeconds || 1)) * 100)}%`}>
                <span class="playback-bar" role="progressbar" aria-label={`Playback progress for ${item.title}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={display.progress === null ? undefined : Math.round(display.progress * 100)} aria-valuetext={display.state === "stale" ? `Estimated ${clockTime(display.seconds)}; awaiting an app update` : undefined}>
                  <span class="transition-[width] duration-200 motion-reduce:transition-none" style={`width: ${(display.progress ?? 0) * 100}%`}></span>
                </span>
                {#if item.scrobbleThresholdSeconds}<i title={`Scrobble at ${clockTime(item.scrobbleThresholdSeconds)}`} aria-label={`Scrobble threshold at ${clockTime(item.scrobbleThresholdSeconds)}`}></i>{/if}
              </span>
              {#if display.seconds !== null || (item.durationSeconds ?? 0) > 0}
                <span class="playback-time" title={display.state === "stale" ? "Estimated position; waiting for an update from your music app." : display.state === "estimated" ? "Progress estimated between updates from your music app." : "Position reported by your music app."}>
                  {#if display.seconds !== null}<span>{display.state === "stale" ? "~" : ""}{clockTime(display.seconds)}</span>{/if}
                  {#if display.seconds !== null && (item.durationSeconds ?? 0) > 0}<span class="sr-only">{" / "}</span>{/if}
                  {#if (item.durationSeconds ?? 0) > 0}<span class:only-total={display.seconds === null}>{clockTime(item.durationSeconds)}</span>{/if}
                </span>
              {/if}
            </div>
            <div class="playback-controls">
              <span class="scrobble-state playback-state" class:complete={item.scrobbled && display.state !== "paused" && display.state !== "stale"}>
                {#if display.state === "paused"}<Pause size={13} aria-hidden="true" />{:else if item.scrobbled && display.state !== "stale"}<CircleCheck size={13} aria-hidden="true" />{:else}<CircleDashed size={13} aria-hidden="true" />{/if}
                {display.state === "paused" ? "Paused" : display.state === "stale" ? "Awaiting update" : item.scrobbled ? "Scrobbled" : item.scrobbleEligible ? "Scrobbling" : item.isPaused === false ? "Listening" : "Playback reported"}
              </span>
              <Button variant="text" size="sm" href={reportHref({ itemId: item.itemId,
                ...(item.userId ? { playback: { subjectUserId: item.userId, deviceId: item.deviceId } } : {}) })}>Report a problem</Button>
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
          {#if index === 0}<StatePill tone="info" text={`${snapshot.nowPlaying.length} listening`} filled compact />{/if}
        </article>
      {/each}
    {:else if snapshot.nowPlaying === undefined || snapshot.failures.some(failure => failure.startsWith("Now playing:"))}
      <ErrorState title="Playback status unavailable" detail="Allstarr could not read the current playback status." onretry={refresh} />
    {/if}
  </section>

  {#if attention.length}
    {@const item = attention[0]}
    <section class="attention-banner" aria-label="Needs your attention">
      <div title={`${item.title} · ${item.detail}`}><strong>{item.title}</strong><span class="desktop-attention">{" · "}{item.detail}</span><span class="phone-attention">{item.key === "health:sources" ? phoneSourceNames : item.detail}</span></div>
      <Button variant="outline" size="sm" href={item.href}>{item.action}</Button>
    </section>
  {/if}
  {#if administrator}<HomeSetup {snapshot} {audio} {matchStats} {storage} {indexedSongs} {listeners} {tasks} />{/if}

  <div class="home-summary" class:administrator>
  <section class="week-panel panel" aria-label={administrator ? "This week" : "Your week"} aria-busy={refreshing}>
    <header><h2>{administrator ? "This week" : "Your week"}</h2><Button variant="text" size="sm" href="#/for-you?section=recap">Open Recap</Button></header>
    {#if snapshot.stats}
      <div class="week-numbers">
        <div><strong>{snapshot.stats.currentWeekListens.toLocaleString()}</strong><span>{countNoun(snapshot.stats.currentWeekListens, "song")} finished<span class="desktop-kpi">{" · "}{listenTrend(snapshot.stats.currentWeekListens, snapshot.stats.previousWeekListens)}</span></span></div>
        <div class="phone-trend"><strong>{snapshot.stats.currentWeekListens - snapshot.stats.previousWeekListens >= 0 ? "+" : "−"}{Math.abs(snapshot.stats.currentWeekListens - snapshot.stats.previousWeekListens).toLocaleString()}</strong><span>on last week</span></div>
        <div class="day-kpi">{#if !administrator && week?.estimatedTimeFromSongLengthsMilliseconds != null}<strong>{listeningTime(week.estimatedTimeFromSongLengthsMilliseconds)}</strong><span>listening time</span>{:else}<strong>{snapshot.stats.completedListens.toLocaleString()}</strong><span>in the last 24 hours</span>{/if}</div>
        {#if snapshot.stats.topArtist?.name}<div><strong>{snapshot.stats.topArtist.name}</strong><span><span class="desktop-kpi">most played this month{" · "}{countLabel(snapshot.stats.topArtist.listens, "play")}</span><span class="phone-kpi">most played</span></span></div>{/if}
        {#if !administrator && week?.newArtistCount != null || snapshot.stats.scrobblesToday != null}<div class="scrobble-kpi">{#if !administrator && week?.newArtistCount != null}<strong>{week.newArtistCount.toLocaleString()}</strong><span>{countNoun(week.newArtistCount, "new artist")}</span>{:else if snapshot.stats.scrobblesToday != null}<strong>{snapshot.stats.scrobblesToday.toLocaleString()}</strong><span>{countNoun(snapshot.stats.scrobblesToday, "scrobble")}<span class="desktop-kpi">{" sent"}</span>{" today"}</span>{/if}</div>{/if}
      </div>
    {:else}<p class="muted">Listening stats aren't available right now.</p>{/if}
    {#if daily.length}<div class="week-chart"><p>Songs finished each day</p><RecapBars points={daily} label="Songs finished each day" unit="songs" compact /></div>{:else}<p class="muted">Your daily listening chart appears when listening stats are available.</p>{/if}
  </section>

  {#if administrator || visibleActivity.length}
    <section class="recent-panel panel" aria-label="Recent activity">
      <header><h2>Recent activity</h2><Button variant="text" size="sm" href="#/activity">View all</Button></header>
      {#if visibleActivity.length}
        {#each visibleActivity as item}
          <ItemRow title={activityHeadline(item)} subtitle={`${activitySource(item.source)}${activityDetail(item.detail)} · ${relativeTime(item.occurredAt)}`} href="#/activity">
            {#snippet leading()}<ActivityStatusIcon state={item.state} severity={item.severity} kind={item.kind} />{/snippet}
          </ItemRow>
        {/each}
      {:else}<EmptyState title="Nothing new lately" description="Matches, playlist updates and source problems will show up here." />{/if}
    </section>
  {/if}
  </div>
  <div class="home-mixes">
  {#if shelf.length}<MixShelf title="Pick up where you left off" cards={shelf} columns={6} seeAll="#/for-you" seeAllLabel="All mixes" onopen={card => { window.location.hash = `#/for-you?mix=${encodeURIComponent(card.id)}`; }} />
  {:else}<EmptyState title="Your mixes start here" description="Play some music or import your history to get mixes made for you.">{#snippet icon()}<Headphones size={24} />{/snippet}{#snippet action()}<Button variant="outline" href="#/for-you">Explore your mixes</Button>{/snippet}</EmptyState>{/if}
  </div>
  {#if !administrator && releases.length}<div class="release-note"><span>New from artists you follow: <strong>{releases[0].title}</strong> by {releases[0].artist}</span><Button variant="text" size="sm" href="#/for-you?section=releases">See releases</Button></div>{/if}
</div>
{/if}
<style>
  .home-view { display: flex; flex-direction: column; gap: var(--space-4); }
  .home-summary { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--space-3); order: 1; }
  .home-summary.administrator { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .home-mixes, .release-note { order: 3; }
  .home-now:empty { display: none; }
  .now-playing-card.compact-playing { grid-template-columns: 72px minmax(0, 1fr) auto; align-items: center; gap: 16px; padding: 14px; border: 1px solid var(--color-edge); }
  .compact-playing .now-playing-artwork { width: 72px; }
  .compact-playing .now-playing-title h2 { font-size: 1.15rem; line-height: 1.2; }
  .compact-playing .now-playing-title p { font-size: .78rem; line-height: 1.2; }
  .compact-playing .now-playing-body { gap: 3px; }
  .playback-meta { color: var(--color-ink-muted); font-size: .7rem; line-height: 1.25; overflow-wrap: anywhere; }
  .compact-playing .playback-progress { display: grid; gap: 3px; margin-top: 4px; }
  .playback-time { display: flex; justify-content: space-between; font-size: .7rem; line-height: 1.2; }
  .only-total { margin-left: auto; }
  .playback-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
  .playback-state { margin-left: 0; display: inline-flex; align-items: center; gap: 4px; font-size: .74rem; color: var(--color-ink-muted); }
  .playback-state.complete { color: var(--success); }
  .playback-controls :global([data-slot="button"]) { padding: 0; height: 24px; font-size: .74rem; }
  .attention-banner { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-radius: var(--radius-lg); background: var(--warning-container); color: var(--on-warning-container); }
  .attention-banner > div { display: flex; align-items: baseline; gap: 3px; min-width: 0; flex: 1; font-size: .78rem; white-space: nowrap; overflow: hidden; }
  .attention-banner strong { flex: none; }
  .desktop-attention { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
  .attention-banner > :global(a) { flex: none; }
  .phone-attention, .phone-kpi, .week-numbers > .phone-trend { display: none; }
  .muted { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .week-panel { padding: 14px 16px; }
  .week-panel, .recent-panel { border: 1px solid var(--color-edge); border-radius: var(--radius-lg); }
  header { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  h2 { font-size: .98rem; font-weight: 600; }
  header :global([data-slot="button"]) { flex: none; }
  .week-numbers { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-2); margin-block: var(--space-3) var(--space-4); }
  .week-numbers > div { display: grid; align-content: start; gap: var(--space-1); min-width: 0; padding: 10px 12px; border-radius: var(--radius-md); background: var(--color-panel-raised); }
  .week-numbers strong { font: 700 1.3rem/1.1 var(--font-display); overflow-wrap: anywhere; }
  .week-numbers span, .week-chart > p { color: var(--color-ink-muted); font-size: .7rem; }
  .week-chart { overflow-x: auto; }
  .week-chart :global(.recap-bars) { min-width: 0; }
  .week-chart > p { margin-bottom: 10px; font-size: .7rem; letter-spacing: .06em; text-transform: uppercase; }
  .recent-panel { overflow: hidden; }
  .recent-panel header { padding: var(--space-2) var(--space-4); }
  .release-note { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: var(--space-3); font-size: var(--text-sm); }
  @media (max-width: 850px) { .home-summary.administrator { grid-template-columns: minmax(0, 1fr); } }
  @media (max-width: 650px) {
    .home-view { gap: 12px; }
    h2 { font-size: 1.125rem; }
    .home-view :global(.home-setup) { order: 2; }
    .home-summary { gap: 12px; }
    .week-panel, .recent-panel { padding: 0; border: 0; background: transparent; box-shadow: none; }
    .week-panel header :global(a), .week-chart, .desktop-kpi, .week-numbers > .day-kpi { display: none; }
    .phone-kpi, .week-numbers > .phone-trend { display: grid; }
    .week-numbers { margin-block: 12px 0; }
    .recent-panel header { padding: 0 0 12px; }
    .recent-panel { border-radius: 0; }
    .recent-panel :global(.item-row) { background: transparent; padding-inline: 0; }
    .recent-panel :global(.item-row:nth-of-type(n + 3)) { display: none; }
    .attention-banner { display: grid; gap: 8px; }
    .attention-banner > div { display: block; white-space: normal; }
    .attention-banner > :global(a) { justify-self: start; }
    .phone-attention { display: block; }
    .desktop-attention { display: none; }
    .now-playing-card.compact-playing { grid-template-columns: 56px minmax(0, 1fr); gap: 12px; }
    .compact-playing .now-playing-artwork { width: 56px; align-self: start; }
    .compact-playing .now-playing-title { padding-right: 80px; }
    .compact-playing > :global(.state-pill) { position: absolute; top: 14px; right: 14px; }
    .playback-controls { gap: 8px; }
  }
</style>
