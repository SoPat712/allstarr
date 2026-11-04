<script lang="ts">
  import type { CacheDiagnostics, StorageOverview } from "$lib/api";
  import type { HomeSnapshot } from "$lib/home";
  import { summarizeHome } from "$lib/home";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  let { snapshot, cache, storage }: { snapshot: HomeSnapshot; cache: CacheDiagnostics | null; storage: StorageOverview | null } = $props();
  const summary = $derived(summarizeHome(snapshot));
  const checked = $derived((snapshot.providers ?? []).filter(item => item.capabilityTotal > 0));
  const failing = $derived(checked.filter(item => item.failedCapabilityCount > 0));
  const backup = $derived(storage?.backups?.slice().sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0]);
  const cacheShare = $derived(cache?.media?.maximumBytes ? Math.min(1, cache.media.payloadBytes / cache.media.maximumBytes) : null);
  const total = $derived(summary.playable + summary.unresolved);
  function bytes(value?: number | null) {
    if (value == null) return "—";
    const units = ["B", "KiB", "MiB", "GiB"];
    const index = Math.min(3, Math.floor(Math.log(Math.max(1, value)) / Math.log(1024)));
    return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value / 1024 ** index)} ${units[index]}`;
  }
  const tiles = $derived([
    { title: "Library", state: total ? summary.unresolved ? "Some need matches" : "All playable" : "No playlists yet", warn: summary.unresolved > 0,
      number: summary.playable.toLocaleString(), label: `songs in ${summary.managed} playlists`, detail: "Your linked playlists and the songs ready to play", href: "#/library/playlists", link: "Open playlists", ratio: total ? summary.playable / total : 0 },
    { title: "Matches", state: summary.unresolved ? `${summary.unresolved} to check` : "Up to date", warn: summary.unresolved > 0,
      number: summary.unresolved.toLocaleString(), label: "songs waiting for a match", detail: "Confirm a match so these songs can play", href: "#/library/matches", link: summary.unresolved ? `Review the ${summary.unresolved}` : "Open matches", ratio: total ? summary.playable / total : 0 },
    { title: "Sources", state: failing.length ? `${failing.length} failing` : checked.length ? "Working" : "Not checked", warn: failing.length > 0,
      number: (checked.length - failing.length).toLocaleString(), label: `of ${checked.length} checked sources working`, detail: failing.length ? failing.map(item => snapshot.providerCatalog?.find(provider => provider.id === item.providerId)?.name ?? item.providerId).join(", ") : "Sources are checked as you use them", href: "#/sources/services", link: failing.length ? "Review sources" : "Open sources", ratio: checked.length ? (checked.length - failing.length) / checked.length : 0 },
    { title: "Storage", state: cacheShare == null ? "Cache and kept songs" : `${Math.round(cacheShare * 100)}% of cache`, warn: cacheShare != null && cacheShare >= .95,
      number: bytes(cache?.media?.payloadBytes), label: cache?.media?.maximumBytes ? `cached of ${bytes(cache.media.maximumBytes)}` : "cached on this server", detail: `${snapshot.stats?.cacheTracks ?? "—"} cached · ${snapshot.stats?.keptTracks ?? "—"} kept · oldest cached copies leave first`, href: "#/library/cached", link: "Open storage", ratio: cacheShare },
    { title: "Background work", state: summary.activeJobs ? `${summary.activeJobs} active` : "Idle", warn: false,
      number: summary.activeJobs.toLocaleString(), label: "jobs running or waiting", detail: summary.activeJobs ? "Playlist updates and other work continue here" : "Nothing running or waiting", href: "#/activity?view=jobs", link: "Open jobs", ratio: null },
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
        <strong class="tile-number">{tile.number}</strong><span class="tile-label">{tile.label}</span>
        <div class="tile-gauge" class:unknown={tile.ratio === null} aria-hidden="true"><span style:width={`${(tile.ratio ?? 0) * 100}%`} class:warn={tile.warn}></span></div>
        <p>{tile.detail}</p><a href={tile.href}>{tile.link}<span aria-hidden="true"> →</span></a>
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
  h3 { font-size: var(--text-sm); font-weight: 600; }
  .tile-number { margin-top: var(--space-3); font: 600 1.75rem/1.2 var(--font-display); font-variant-numeric: tabular-nums; }
  .tile-label { margin-top: var(--space-1); }
  .tile-gauge { height: 5px; overflow: hidden; margin-block: var(--space-3); border-radius: var(--radius-sm); background: var(--color-panel-raised); }
  .tile-gauge > span { display: block; height: 100%; background: var(--color-signal); }
  .tile-gauge > .warn { background: var(--warning); }
  .tile-gauge.unknown { border: 1px dashed var(--color-edge-strong); background: transparent; }
  p { min-height: 2.8em; margin: 0 0 var(--space-2); overflow-wrap: anywhere; }
  a { display: inline-flex; min-height: 44px; align-items: center; align-self: start; margin-top: auto; font-size: var(--text-sm); font-weight: 600; }
  @media (max-width: 1050px) { .setup-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  @media (max-width: 540px) { .setup-grid { grid-template-columns: minmax(0, 1fr); } }
</style>
