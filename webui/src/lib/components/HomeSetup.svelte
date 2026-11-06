<script lang="ts">
  import { formatLocalDateTime } from "$lib/dates";
  import { countLabel, countNoun } from "$lib/count-label";
  import type { ActivityTask, MatchReviewResponse, StorageOverview } from "$lib/api";
  import type { HomeSnapshot } from "$lib/home";
  import { homeSourceHealth, nextScheduledWork, summarizeHome } from "$lib/home";
  import { providerDisplayName } from "$lib/sources";
  import { Button } from "$lib/components/ui/button";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import StatusGauge from "$lib/components/patterns/StatusGauge.svelte";
  import type { StatusSegment } from "$lib/components/patterns/helpers";
  let { snapshot, audio, matchStats, storage, indexedSongs, listeners, tasks }: {
    snapshot: HomeSnapshot;
    audio: { bytes: number; maximum: number | null; keptBytes: number | null } | null;
    matchStats: MatchReviewResponse["stats"] | null; storage: StorageOverview | null;
    indexedSongs: number | null; listeners: number | null; tasks: ActivityTask[];
  } = $props();
  const summary = $derived(summarizeHome(snapshot));
  const health = $derived(homeSourceHealth(snapshot));
  const backup = $derived(storage?.backups?.slice().sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0]);
  const cacheShare = $derived(audio?.maximum ? Math.min(1, audio.bytes / audio.maximum) : null);
  const needsReview = $derived(matchStats?.attention ?? summary.unresolved);
  const total = $derived(summary.playable + summary.unresolved);
  const next = $derived(nextScheduledWork(tasks));
  const stats = $derived(snapshot.stats);
  const name = (id: string) => providerDisplayName(snapshot.providerCatalog ?? [], id);
  function bytes(value: number) {
    const units = ["B", "KiB", "MiB", "GiB"];
    const index = Math.min(3, Math.floor(Math.log(Math.max(1, value)) / Math.log(1024)));
    return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value / 1024 ** index)} ${units[index]}`;
  }
  const segment = (label: string, value: number, tone: StatusSegment["tone"]): StatusSegment => ({ label, value, tone });
  const tiles = $derived([
    { title: "Library", state: !stats ? "Not available" : total ? summary.unresolved ? "Some need matches" : "All playable" : "No playlists yet", tone: !stats ? "neutral" : summary.unresolved ? "warn" : "ok",
      number: stats ? summary.playable.toLocaleString() : null, label: stats ? `${countNoun(summary.playable, "song")} in ${countLabel(summary.managed, "playlist")}` : null,
      detail: indexedSongs !== null && snapshot.status?.backendType ? `${countLabel(indexedSongs, "song")} indexed in ${snapshot.status.backendType}` : null,
      href: "#/library/playlists", link: "Open playlists", gauge: stats ? [segment("Playable", summary.playable, "good"), segment("Needs a match", summary.unresolved, "warn")] : [] },
    { title: "Matches", state: !matchStats ? "Not checked" : needsReview ? `${needsReview} to check` : "Up to date", tone: !matchStats ? "neutral" : needsReview ? "warn" : "ok",
      number: matchStats?.matched?.toLocaleString() ?? null, label: matchStats ? "matched" : null,
      detail: matchStats?.automatic != null && matchStats.manualMatches != null && (matchStats.ambiguous ?? matchStats.suggested) != null ? `${matchStats.automatic.toLocaleString()} by Allstarr · ${matchStats.manualMatches.toLocaleString()} by you · ${(matchStats.ambiguous ?? matchStats.suggested).toLocaleString()} unsure` : null,
      href: "#/library/matches", link: needsReview ? `Review the ${needsReview}` : "Open matches", gauge: matchStats?.automatic != null && matchStats.manualMatches != null ? [segment("By Allstarr", matchStats.automatic, "info"), segment("By you", matchStats.manualMatches, "accent"), segment("Unsure", matchStats.ambiguous ?? matchStats.suggested, "warn")] : [] },
    { title: "Sources", state: health.failing.length ? `${health.failing.length} failing` : health.checked.length ? "Working" : "Not checked", tone: health.failing.length ? "stop" : health.checked.length ? "ok" : "neutral",
      number: snapshot.providers ? (health.checked.length - health.failing.length).toLocaleString() : null, label: snapshot.providers ? `of ${health.checked.length} working` : null,
      detail: health.checked.length ? health.failing.length ? [health.down.length ? `${health.down.map(item => name(item.providerId)).join(", ")} down` : "", health.slow.length ? `${health.slow.map(item => name(item.providerId)).join(", ")} slow` : ""].filter(Boolean).join(" · ") : "All working" : null,
      href: "#/sources/services", link: health.failing.length ? "Review sources" : "Open sources", gauge: [] },
    { title: "Storage", state: cacheShare == null ? "Cache" : `${Math.round(cacheShare * 100)}% of cache`, tone: cacheShare != null && cacheShare >= .95 ? "warn" : "neutral",
      number: audio ? bytes(audio.bytes) : null, label: audio ? `cached${audio.maximum ? ` of ${bytes(audio.maximum)}` : ""}` : null,
      detail: stats?.cacheTracks != null && stats.keptTracks != null ? `${stats.cacheTracks.toLocaleString()} cached · ${stats.keptTracks.toLocaleString()} kept${audio?.keptBytes != null ? ` (${bytes(audio.keptBytes)})` : ""} · oldest leave first` : null,
      href: "#/library/cached", link: "Open storage", gauge: audio?.maximum ? [segment("Cached", audio.bytes, "info"), segment("Free", Math.max(0, audio.maximum - audio.bytes), "empty")] : [] },
    { title: "Background work", state: !stats ? "Not available" : stats.waitingJobs ? `${stats.waitingJobs} waiting` : summary.activeJobs ? `${summary.activeJobs} active` : "Idle", tone: summary.activeJobs ? "info" : "neutral",
      number: stats?.completedJobsToday?.toLocaleString() ?? null, label: stats?.completedJobsToday != null ? `${countNoun(stats.completedJobsToday, "job")} done today` : null,
      detail: [next ? `Next: ${next.name} at ${new Date(next.at).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })}` : "", stats?.failedJobsToday != null ? stats.failedJobsToday ? `${stats.failedJobsToday} failed` : "nothing failed" : ""].filter(Boolean).join(" · ") || null,
      href: "#/activity?view=jobs", link: "Open jobs", gauge: stats?.completedJobsToday != null && stats.failedJobsToday != null ? [segment("Finished today", stats.completedJobsToday, "good"), segment("Failed today", stats.failedJobsToday, "stop")] : [] },
    { title: "Backups", state: backup ? backup.status === "verified" ? "Verified" : "Needs a look" : storage ? "No backup yet" : "Not available", tone: backup ? backup.status === "verified" ? "ok" : "warn" : "neutral",
      number: backup ? new Date(backup.createdAt).toLocaleDateString(undefined, { month: "short", day: "numeric" }) : null, label: backup ? `${new Date(backup.createdAt).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })} · ${bytes(backup.bytes)}` : null,
      detail: [storage?.backupStatus?.nextRunAt ? `Next ${formatLocalDateTime(storage.backupStatus.nextRunAt)}` : "", storage?.backupPolicy?.dailyRetentionCount != null && storage.backupPolicy.weeklyRetentionCount != null ? `${storage.backupPolicy.dailyRetentionCount} daily, ${storage.backupPolicy.weeklyRetentionCount} weekly kept` : ""].filter(Boolean).join(" · ") || null,
      href: "#/settings/backups", link: "Open backups", gauge: storage?.backups?.length ? [segment("Verified backups", storage.backups.filter(item => item.status === "verified").length, "good"), segment("Other backups", storage.backups.filter(item => item.status !== "verified").length, "warn")] : [] },
  ]);
</script>
<section aria-label="Your setup" class="home-setup">
  <header><h2>Your setup</h2><span>{[snapshot.status?.backendType, listeners !== null ? countLabel(listeners, "listener") : null].filter(Boolean).join(" · ")}</span></header>
  <div class="setup-grid">
    {#each tiles as tile (tile.title)}
      <article class="setup-tile">
        <div class="tile-heading"><h3>{tile.title}</h3><StatePill tone={tile.tone as "ok" | "warn" | "stop" | "neutral" | "info"} text={tile.state} filled compact /></div>
        <div class="tile-value">{#if tile.number !== null}<strong class="tile-number">{tile.number}</strong> <span class="tile-label">{tile.label}</span>{/if}</div>
        {#if tile.title === "Sources"}
          <div class="source-dots" aria-hidden="true">{#each [...health.checked.filter(source => !health.failing.includes(source)), ...health.down, ...health.slow] as source}<i class:failing={health.down.includes(source)} class:slow={health.slow.includes(source)}></i>{/each}</div>
        {:else if tile.gauge.length}
          <div class="tile-gauge" class:match-gauge={tile.title === "Matches"}><StatusGauge segments={tile.gauge} compact /></div>
        {/if}
        {#if tile.detail}<p title={tile.detail}>{tile.detail}</p>{/if}<Button variant="text" size="sm" href={tile.href}>{tile.link}</Button>
      </article>
    {/each}
  </div>
</section>
<style>
  header, .tile-heading { display: flex; justify-content: space-between; align-items: center; gap: var(--space-2); }
  header { margin-bottom: var(--space-3); }
  h2 { font-size: 1.125rem; font-weight: 600; }
  header > span { color: var(--color-ink-muted); font-size: .78rem; }
  .setup-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); grid-auto-rows: 1fr; gap: var(--space-3); }
  .setup-tile { display: flex; min-width: 0; flex-direction: column; gap: 7px; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); padding: 14px 16px; background: var(--color-panel); }
  h3 { color: var(--color-ink-muted); font-size: .74rem; font-weight: 400; text-transform: uppercase; letter-spacing: .05em; }
  .tile-value { min-height: 27px; line-height: 1.2; }
  .tile-number { font: 700 1.45rem/1.1 var(--font-display); font-variant-numeric: tabular-nums; }
  .tile-label { color: var(--color-ink-muted); font-size: .82rem; }
  .source-dots { display: flex; gap: 3px; min-height: 5px; overflow: hidden; }
  .source-dots i { min-width: 0; flex: 1; max-width: 7px; height: 8px; border-radius: 2px; background: var(--success); }
  .source-dots i.failing { background: var(--danger); }
  .source-dots i.slow { background: var(--warning); }
  p { color: var(--color-ink-muted); font-size: .74rem; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
  .setup-tile :global([data-slot="button"]) { align-self: start; margin-top: auto; padding-inline: 0; }
  @media (max-width: 1050px) { .setup-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  @media (max-width: 540px) { .setup-grid { grid-template-columns: minmax(0, 1fr); } }
</style>
