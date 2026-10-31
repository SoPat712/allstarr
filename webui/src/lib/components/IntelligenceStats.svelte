<script lang="ts">
  import { onMount } from "svelte";
  import { listeningStats, type ListeningStats } from "$lib/api";
  import { formatCount, listeningTime } from "$lib/intelligence";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import IntelligenceHistoryImports from "$lib/components/IntelligenceHistoryImports.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import { Skeleton } from "$lib/components/ui/skeleton";

  let stats = $state<ListeningStats | null>(null), yearStats = $state<ListeningStats | null>(null);
  let period = $state("30d"), year = $state(""), loading = $state(true), refreshing = $state(false), error = $state("");
  const recapYear = $derived(year || (new Date().getMonth() === 11 ? String(new Date().getFullYear()) : ""));
  let request = 0;
  let importedRefresh: ReturnType<typeof setTimeout> | undefined;
  let loadedSelection = $state("");
  const selection = $derived(year || period);
  const missingTime = $derived(Boolean(stats?.playCount && stats.coverage.unknownDurationCount >= stats.playCount));
  const periods = [
    { id: "7d", label: "Last 7 days" }, { id: "30d", label: "Last 30 days" },
    { id: "year", label: "This year" }, { id: "all", label: "All time" },
  ];
  const years = $derived.by(() => {
    const current = new Date().getFullYear();
    const first = stats?.observedSince ? new Date(stats.observedSince).getFullYear() : current - 10;
    return [{ value: "", label: "Past years" }, ...Array.from({ length: Math.max(1, current - first) }, (_, i) => ({ value: String(current - i - 1), label: String(current - i - 1) }))];
  });
  const lists = $derived(stats ? [
    { title: "Top songs", empty: "No songs yet.", rows: stats.topSongs.map(item => ({ name: item.title, detail: item.artist, count: item.playCount })) },
    { title: "Top artists", empty: "No artists yet.", rows: stats.topArtists.map(item => ({ name: item.artist, detail: "", count: item.playCount })) },
    { title: "Top albums", empty: "No albums yet.", rows: stats.topAlbums.map(item => ({ name: item.album, detail: item.artist, count: item.playCount })) },
  ] : []);

  async function load() {
    const current = ++request;
    const selected = selection;
    refreshing = true;
    try {
      const [value, annual] = await Promise.all([
        listeningStats.get(period, year),
        recapYear && !year ? listeningStats.get("year", recapYear).catch(() => null) : Promise.resolve(null),
      ]);
      if (current === request) { stats = value; yearStats = year ? value : annual; loadedSelection = selected; error = ""; }
    } catch { if (current === request) error = "Your listening stats could not be loaded. Try again."; }
    finally { if (current === request) { loading = false; refreshing = false; } }
  }

  function selectPeriod(value: string) { period = value; year = ""; void load(); }
  function importsChanged() {
    void load();
    clearTimeout(importedRefresh);
    importedRefresh = setTimeout(() => void load(), 31_000);
  }
  const scheduler = createRefreshScheduler(load);
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { request++; clearTimeout(importedRefresh); unsubscribe(); scheduler.cancel(); };
  });
</script>

<div class="your-stats" aria-busy={refreshing}>
  <h2>Your listening</h2>
  <div class="stats-toolbar">
    <SegmentedNav label="Stats period" items={periods} active={year ? "" : period} onchange={selectPeriod} />
    <div class="stats-actions"><SelectField bind:value={year} label="Year in review" options={years} onchange={() => void load()} /><RefreshButton variant="secondary" label="Refresh stats" onrefresh={load} /></div>
  </div>
  {#if loading}<Skeleton class="skeleton-panel" aria-label="Loading listening stats" />
  {:else if !stats || error && loadedSelection !== selection}<RouteError eyebrow="Stats unavailable" title="Your stats are unavailable." message={error} onRetry={load} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
    <div class="stats-numbers">
      <article class="panel"><strong>{formatCount(stats.playCount)}</strong><span>Plays</span></article>
      <article class="panel"><strong>{formatCount(stats.activeDays)}</strong><span>Active days</span></article>
      <article class="panel"><strong>{missingTime ? "Unavailable" : listeningTime(stats.estimatedTimeFromSongLengthsMilliseconds)}</strong><span>Estimated listening time</span><small>{missingTime ? "Song lengths aren't available for these plays." : `about ${listeningTime(stats.estimatedTimeFromSongLengthsMilliseconds)} from song lengths`}</small>{#if !missingTime && stats.coverage.unknownDurationCount}<small>{formatCount(stats.coverage.unknownDurationCount)} plays had no song length.</small>{/if}</article>
    </div>
    {#if !stats.playCount}<p>No plays in this period.</p>{/if}
    <div class="stats-lists">
      {#each lists as list}
        <section class="panel" aria-label={list.title}>
          <h2>{list.title}</h2>
          <ol>{#each list.rows as row, index}<li><span class="rank">{index + 1}</span><div class="stats-name"><strong>{row.name}</strong>{#if row.detail}<small>{row.detail}</small>{/if}</div><span class="plays-count">{formatCount(row.count)} {row.count === 1 ? "play" : "plays"}</span></li>{/each}</ol>
          {#if !list.rows.length}<p>{list.empty}</p>{/if}
        </section>
      {/each}
    </div>
    <footer>
      <p>{stats.observedSince ? `Counting since ${new Date(stats.observedSince).toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" })}.` : "Counting starts with your first listen."} {formatCount(stats.coverage.unknownSongIdentityCount)} {stats.coverage.unknownSongIdentityCount === 1 ? "play couldn't" : "plays couldn't"} be identified.</p>
      <p>Imported from other services: {formatCount(stats.importedCount)} plays</p>
    </footer>
  {/if}
  {#if recapYear && yearStats}
    <section class="settings-card year-recap" aria-label="Year in music">
      <h2>{recapYear} · Year in music</h2>
      <div class="year-tiles">
        <article><MediaArtwork /><small>Top artist</small><strong>{yearStats.topArtists[0]?.artist ?? "No artists yet"}</strong></article>
        <article><small>Top song</small><strong>{yearStats.topSongs[0]?.title ?? "No songs yet"}</strong></article>
        <article><small>Listening time</small><strong>{yearStats.coverage.unknownDurationCount >= yearStats.playCount && yearStats.playCount ? "Unavailable" : `about ${formatCount(Math.round(yearStats.estimatedTimeFromSongLengthsMilliseconds / 3600000))} hours`}</strong></article>
        <article><small>Most-played month</small><span>Not available yet</span></article>
        <article><small>New artists</small><span>Not available yet</span></article>
      </div>
    </section>
  {/if}
  <section aria-label="Your data"><h2>Bring in your history</h2><IntelligenceHistoryImports onchanged={importsChanged} /></section>
</div>

<style>
  .your-stats { display: grid; grid-template-columns: minmax(0, 1fr); min-width: 0; gap: var(--section-gap); }
  .stats-toolbar, .stats-actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); }
  .stats-toolbar { justify-content: space-between; }
  .stats-toolbar :global(.segmented-nav) { min-width: 0; max-width: 100%; }
  .stats-numbers, .stats-lists { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--section-gap); }
  .stats-numbers article { display: flex; flex-direction: column; gap: var(--space-2); padding: var(--surface-gutter); }
  .stats-numbers strong { font-size: clamp(1.5rem, 3vw, 2rem); font-weight: 600; line-height: 1.2; }
  .stats-numbers small, footer, .rank, .stats-name small { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .stats-lists section { padding: var(--surface-gutter); min-width: 0; }
  h2 { font-size: 1rem; font-weight: 600; margin-bottom: var(--space-4); }
  li { display: flex; align-items: center; gap: var(--space-3); padding-block: var(--space-3); }
  li + li { border-top: 1px solid var(--color-edge); }
  .rank { min-width: 1rem; }
  .stats-name { min-width: 0; flex: 1; }
  .stats-name strong, .stats-name small { display: block; overflow-wrap: anywhere; }
  .plays-count { font-size: var(--text-sm); white-space: nowrap; }
  footer { display: grid; gap: var(--space-2); }
  .year-recap { padding: var(--surface-gutter); }
  .year-tiles { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: var(--space-4); }
  .year-tiles article { display: flex; flex-direction: column; gap: var(--space-2); min-width: 0; overflow-wrap: anywhere; }
  .year-tiles small { color: var(--color-ink-muted); }
  .year-tiles :global(.media-art) { width: 64px; height: 64px; }
  @media (max-width: 760px) { .year-tiles { grid-template-columns: minmax(0, 1fr); } }
  @media (max-width: 760px) { .stats-lists, .stats-numbers { grid-template-columns: 1fr; } }
</style>
