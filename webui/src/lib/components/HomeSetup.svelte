<script lang="ts">
  import { countLabel, countNoun } from "$lib/count-label";
  import type { MatchReviewResponse, StorageOverview } from "$lib/api";
  import type { HomeSnapshot } from "$lib/home";
  import { summarizeHome } from "$lib/home";
  import { Button } from "$lib/components/ui/button";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  let { snapshot, audio, matchStats, storage }: { snapshot: HomeSnapshot; audio: { bytes: number; maximum: number; keptBytes: number | null } | null; matchStats: MatchReviewResponse["stats"] | null; storage: StorageOverview | null } = $props();
  const summary = $derived(summarizeHome(snapshot));
  const checked = $derived((snapshot.providers ?? []).filter(item => item.capabilityTotal > 0));
  const failing = $derived(checked.filter(item => item.failedCapabilityCount > 0));
  const backup = $derived(storage?.backups?.slice().sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0]);
  const cacheShare = $derived(audio?.maximum ? Math.min(1, audio.bytes / audio.maximum) : null);
  const needsReview = $derived(matchStats?.attention ?? summary.unresolved);
  const total = $derived(summary.playable + summary.unresolved);
  function bytes(value?: number | null) {
    if (value == null) return "—";
    const units = ["B", "KiB", "MiB", "GiB"];
    const index = Math.min(3, Math.floor(Math.log(Math.max(1, value)) / Math.log(1024)));
    return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value / 1024 ** index)} ${units[index]}`;
  }
  const tiles = $derived([
    { title: "Library", state: total ? summary.unresolved ? "Some need matches" : "All playable" : "No playlists yet", warn: summary.unresolved > 0,
      number: summary.playable.toLocaleString(), label: `${countNoun(summary.playable, "song")} in ${countLabel(summary.managed, "playlist")}`, detail: "Your linked playlists and the songs ready to play", href: "#/library/playlists", link: "Open playlists", ratio: total ? summary.playable / total : 0 },
    { title: "Matches", state: needsReview ? `${needsReview} to check` : "Up to date", warn: needsReview > 0,
      number: matchStats?.matched?.toLocaleString() ?? "—", label: "matched", detail: matchStats && matchStats.automatic != null && matchStats.manualMatches != null ? `${matchStats.automatic.toLocaleString()} by Allstarr · ${matchStats.manualMatches.toLocaleString()} by you · ${(matchStats.ambiguous ?? matchStats.suggested).toLocaleString()} unsure` : "Match breakdown unavailable", href: "#/library/matches", link: needsReview ? `Review the ${needsReview}` : "Open matches", ratio: total ? summary.playable / total : 0 },
    { title: "Sources", state: failing.length ? `${failing.length} failing` : checked.length ? "Working" : "Not checked", warn: failing.length > 0,
      number: (checked.length - failing.length).toLocaleString(), label: `of ${countLabel(checked.length, "checked source")} working`, detail: failing.length ? failing.map(item => snapshot.providerCatalog?.find(provider => provider.id === item.providerId)?.name ?? item.providerId).join(", ") : "Sources are checked as you use them", href: "#/sources/services", link: failing.length ? "Review sources" : "Open sources", ratio: checked.length ? (checked.length - failing.length) / checked.length : 0 },
    { title: "Storage", state: cacheShare == null ? "Cache and kept songs" : `${Math.round(cacheShare * 100)}% of cache`, warn: cacheShare != null && cacheShare >= .95,
      number: bytes(audio?.bytes), label: audio ? `cached of ${bytes(audio.maximum)}` : "Audio cache usage unavailable", detail: `${snapshot.stats?.cacheTracks ?? "—"} cached · ${snapshot.stats?.keptTracks ?? "—"} kept${audio?.keptBytes != null ? ` (${bytes(audio.keptBytes)})` : ""} · oldest cached copies leave first`, href: "#/library/cached", link: "Open storage", ratio: cacheShare },
    { title: "Background work", state: summary.activeJobs ? `${summary.activeJobs} active` : "Idle", warn: false,
      number: summary.activeJobs.toLocaleString(), label: `${countNoun(summary.activeJobs, "job")} running or waiting`, detail: summary.activeJobs ? "Playlist updates and other work continue here" : "Nothing running or waiting", href: "#/activity?view=jobs", link: "Open jobs", ratio: null },
    { title: "Backups", state: backup ? backup.status === "verified" ? "Verified" : "Needs a look" : storage ? "No backup yet" : "Not available", warn: Boolean(backup && backup.status !== "verified"),
      number: backup ? new Date(backup.createdAt).toLocaleDateString(undefined, { month: "short", day: "numeric" }) : "—", label: backup ? `${new Date(backup.createdAt).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })} · ${bytes(backup.bytes)}` : "Create a backup to protect your setup", detail: storage?.backupStatus?.nextRunAt ? `Next ${new Date(storage.backupStatus.nextRunAt).toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" })}` : "Review your backup schedule and saved copies", href: "#/settings/backups", link: "Open backups", ratio: backup ? backup.status === "verified" ? 1 : 0 : null },
  ]);
</script>
<section aria-label="Your setup" class="home-setup">
  <header><h2>Your setup</h2><span>{snapshot.status?.backendType ?? "Your music server"}</span></header>
  <div class="setup-grid">
    {#each tiles as tile (tile.title)}
      <article class="setup-tile">
        <div class="tile-heading"><h3>{tile.title}</h3><StatePill tone={tile.warn ? "warn" : tile.ratio === 1 ? "ok" : "neutral"} text={tile.state} /></div>
        <div class="tile-value"><strong class="tile-number">{tile.number}</strong> <span class="tile-label">{tile.label}</span></div>
        {#if tile.title === "Sources"}
          <div class="source-dots" aria-hidden="true">{#each checked as source}<i class:failing={source.failedCapabilityCount > 0}></i>{/each}</div>
        {:else if tile.title === "Matches" && matchStats?.automatic != null && matchStats?.manualMatches != null}
          <div class="tile-gauge match-gauge" aria-hidden="true"><span style:flex={matchStats.automatic}></span><span class="chosen" style:flex={matchStats.manualMatches}></span><span class="unsure" style:flex={matchStats.ambiguous ?? matchStats.suggested}></span></div>
        {:else if !["Matches", "Backups"].includes(tile.title) && tile.ratio !== null}
          <div class="tile-gauge" aria-hidden="true"><span style:width={`${tile.ratio * 100}%`} class:warn={tile.warn}></span></div>
        {/if}
        <p>{tile.detail}</p><Button variant="text" size="sm" href={tile.href}>{tile.link}</Button>
      </article>
    {/each}
  </div>
</section>
<style>
  header, .tile-heading { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: var(--space-2); }
  header { margin-bottom: var(--space-3); }
  h2 { font-size: 1.125rem; font-weight: 600; }
  header > span, p, .tile-label { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .setup-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--space-3); }
  .setup-tile { display: flex; min-width: 0; flex-direction: column; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); padding: var(--space-4); background: var(--color-panel); }
  h3 { font-size: var(--text-xs); font-weight: 500; text-transform: uppercase; letter-spacing: .04em; }
  .tile-value { margin-block: var(--space-2); }
  .tile-number { font: 600 1.5rem/1.2 var(--font-display); font-variant-numeric: tabular-nums; }

  .tile-gauge { height: 5px; overflow: hidden; margin-block: var(--space-3); border-radius: var(--radius-sm); background: var(--color-panel-raised); }
  .tile-gauge > span { display: block; height: 100%; background: var(--color-signal); }
  .tile-gauge > .warn { background: var(--warning); }
  .match-gauge { display: flex; gap: 2px; }
  .match-gauge .chosen { background: var(--md-sys-color-tertiary); }
  .match-gauge .unsure { background: var(--warning); }
  .source-dots { display: flex; flex-wrap: wrap; gap: 3px; margin-block: var(--space-3); }
  .source-dots i { width: 6px; height: 8px; border-radius: 2px; background: var(--success); }
  .source-dots i.failing { background: var(--warning); }
  p { margin-top: var(--space-2); margin-bottom: var(--space-2); overflow-wrap: anywhere; }
  .setup-tile :global([data-slot="button"]) { align-self: start; margin-top: auto; }
  @media (max-width: 1050px) { .setup-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  @media (max-width: 540px) { .setup-grid { grid-template-columns: minmax(0, 1fr); } }
</style>
