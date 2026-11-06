<script lang="ts">
  import { countLabel, countNoun } from "$lib/count-label";
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { listeningStats, releaseInbox, type ListeningStats } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import UiIcon from "$lib/components/UiIcon.svelte";
  import { formatCount, listeningTime, recapClock, recapTimeline } from "$lib/intelligence";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import IntelligenceHistoryImports from "$lib/components/IntelligenceHistoryImports.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import RecapBars from "$lib/components/RecapBars.svelte";
  import { Button } from "$lib/components/ui/button";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";

  let stats = $state<ListeningStats | null>(null), before = $state<ListeningStats | null>(null);
  let period = $state("all"), year = $state(""), loading = $state(true), refreshing = $state(false), error = $state(""), expanded = $state("");
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
    const since = stats?.firstPlayAt ?? stats?.observedSince;
    const first = since ? new Date(since).getFullYear() : current - 10;
    return [{ value: "", label: "Past years" }, ...Array.from({ length: Math.max(1, current - first) }, (_, i) => ({ value: String(current - i - 1), label: String(current - i - 1) }))];
  });
  const lists = $derived(stats ? [
    { title: "Top songs", empty: "No songs yet.", round: false, rows: stats.topSongs.map(item => ({ name: item.title, detail: item.artist, count: item.playCount, artworkUrl: item.artworkUrl, seed: `${item.title} ${item.artist}`, artist: "" })) },
    { title: "Top artists", empty: "No artists yet.", round: true, rows: stats.topArtists.map(item => ({ name: item.artist, detail: "", count: item.playCount, artworkUrl: item.artworkUrl, seed: item.artist, artist: item.artist })) },
    { title: "Top albums", empty: "No albums yet.", round: false, rows: stats.topAlbums.map(item => ({ name: item.album, detail: item.artist, count: item.playCount, artworkUrl: item.artworkUrl, seed: "", artist: item.artist })) },
  ] : []);
  const heroArtist = $derived(stats?.topArtists[0]);
  const change = (now: number, then: number) => then > 0 ? Math.round((now - then) / then * 100) : null;
  const comparison = $derived.by(() => {
    if (!stats || !before?.playCount || !year) return null;
    const earlier = new Set(before.topArtists.slice(0, 10).map(item => item.artist.toLowerCase()));
    return {
      plays: change(stats.playCount, before.playCount),
      time: change(stats.estimatedTimeFromSongLengthsMilliseconds, before.estimatedTimeFromSongLengthsMilliseconds),
      newcomers: stats.topArtists.slice(0, 10).filter(item => !earlier.has(item.artist.toLowerCase())).map(item => item.artist),
      label: String(Number(year) - 1),
    };
  });
  const changeText = (value: number | null, noun: string) => value == null ? "" : value === 0 ? `Same ${noun}` : `${Math.abs(value)}% ${value > 0 ? "more" : "less"} ${noun}`;
  const timeline = $derived(recapTimeline(stats?.timeline ?? [], stats?.timelineUnit ?? "month"));
  const busiest = $derived(timeline.length ? timeline.reduce((best, point) => point.value > best.value ? point : best) : null);
  const clock = $derived(recapClock(stats?.byHour ?? []));
  const busiestHour = $derived(clock.length ? clock.reduce((best, point) => point.value > best.value ? point : best) : null);
  const hours = (milliseconds: number) => {
    const total = milliseconds / 3_600_000;
    return total >= 1 ? countLabel(Math.round(total), "hour") : listeningTime(milliseconds);
  };

  let following = $state("");
  async function follow(name: string) {
    if (following) return;
    following = name;
    try {
      const match = (await releaseInbox.search(name)).find(item => item.displayName.toLowerCase() === name.toLowerCase());
      if (!match) { toast.error(`${name} couldn't be found to follow.`); return; }
      await releaseInbox.follow(match);
      toast.success(`Following ${name} for new releases`);
    } catch { toast.error(`${name} couldn't be followed. Try again.`); }
    finally { following = ""; }
  }

  async function load() {
    const current = ++request;
    const selected = selection;
    refreshing = true;
    try {
      const value = await listeningStats.get(period, year);
      if (current === request) { stats = value; loadedSelection = selected; error = ""; before = null; }
      // A past year is compared with the one before it.
      if (year && current === request) {
        const previous = await listeningStats.get(period, String(Number(year) - 1)).catch(() => null);
        if (current === request) before = previous;
      }
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
  <h2 class="visually-hidden">Your listening</h2>
  <PageStatus items={[{ label: stats && loadedSelection === selection ? countLabel(stats.playCount, "play") : "Your listening recap" }, { label: year || periods.find(item => item.id === period)?.label || "All time" }]} />
  <div class="stats-toolbar">
    <SegmentedNav variant="segments" label="Stats period" items={periods} active={year ? "" : period} onchange={selectPeriod} />
    <div class="stats-actions"><SelectField bind:value={year} label="Year in review" options={years} onchange={() => void load()} /><RefreshButton variant="secondary" label="Refresh stats" onrefresh={load} /></div>
  </div>
  {#if loading || refreshing && loadedSelection !== selection}<RowsSkeleton />
  {:else if !stats || error && loadedSelection !== selection}<ErrorState title="Your stats are unavailable" detail={error} onretry={load} />
  {:else}
    {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
    <section class="recap-hero panel" aria-label="Summary">
      {#if heroArtist}
        <div class="hero-artist">
          <MediaArtwork url={heroArtist.artworkUrl} class="hero-art" />
          <div><small>{year ? `${year} in music` : "Your top artist"}</small><strong>{heroArtist.artist}</strong><span>{countLabel(heroArtist.playCount, "play")}</span></div>
        </div>
      {/if}
      <div class="stats-numbers">
        <article><strong>{formatCount(stats.playCount)}</strong><span>{countNoun(stats.playCount, "Play")}</span></article>
        <article><strong>{missingTime ? "Unavailable" : hours(stats.estimatedTimeFromSongLengthsMilliseconds)}</strong><span>Listening time</span>{#if missingTime}<small>Song lengths aren't available for these plays.</small>{:else if stats.coverage.unknownDurationCount}<small>{countLabel(stats.coverage.unknownDurationCount, "play")} had no song length.</small>{/if}</article>
        <article><strong>{formatCount(stats.activeDays)}</strong><span>{countNoun(stats.activeDays, "Active day")}</span></article>
        {#if stats.uniqueArtists}<article><strong>{formatCount(stats.uniqueArtists)}</strong><span>{countNoun(stats.uniqueArtists, "Artist")}</span></article>{/if}
        {#if stats.uniqueSongs}<article><strong>{formatCount(stats.uniqueSongs)}</strong><span>{countNoun(stats.uniqueSongs, "Song")}</span></article>{/if}
        {#if stats.longestStreakDays}<article><strong>{countLabel(stats.longestStreakDays, "day")}</strong><span>Longest streak</span></article>{/if}
      </div>
    </section>
    {#if comparison}
      <section class="panel comparison" aria-label="Compared with the year before">
        <h2>Compared with {comparison.label}</h2>
        <ul>
          {#if comparison.plays != null}<li class:up={comparison.plays > 0} class:down={comparison.plays < 0}><strong>{changeText(comparison.plays, "plays")}</strong></li>{/if}
          {#if comparison.time != null}<li class:up={comparison.time > 0} class:down={comparison.time < 0}><strong>{changeText(comparison.time, "listening time")}</strong></li>{/if}
          {#if comparison.newcomers.length}<li><strong>New in your top 10:</strong> {comparison.newcomers.slice(0, 4).join(", ")}{comparison.newcomers.length > 4 ? ` and ${comparison.newcomers.length - 4} more` : ""}</li>{/if}
        </ul>
      </section>
    {/if}
    {#if !stats.playCount}<p>No plays in this period.</p>{/if}
    {#if timeline.length > 1}
      <section class="panel chart-card" aria-label="Plays over time">
        <header><h2>Plays over time</h2>{#if busiest}<small>Busiest {stats.timelineUnit === "day" ? "day" : "month"}: {busiest.label}, {countLabel(busiest.value, "play")}</small>{/if}</header>
        <RecapBars points={timeline} label="Plays over time" />
      </section>
    {/if}
    {#if clock.length || stats.topGenres?.length}
      <div class="chart-row">
        {#if clock.length}
          <section class="panel chart-card" aria-label="When you listen">
            <header><h2>When you listen</h2>{#if busiestHour}<small>Most often around {busiestHour.label}</small>{/if}</header>
            <RecapBars points={clock} label="Plays by hour of the day" />
          </section>
        {/if}
        {#if stats.topGenres?.length}
          <section class="panel chart-card" aria-label="Top styles">
            <header><h2>Top styles</h2><small>Based on songs with known styles</small></header>
            <ol class="genres">{#each stats.topGenres as genre}<li><span class="genre-name">{genre.genre}</span><span class="genre-track"><span style:width={`${genre.plays / stats.topGenres[0].plays * 100}%`}></span></span><span class="plays-count">{formatCount(genre.plays)}</span></li>{/each}</ol>
          </section>
        {/if}
      </div>
    {/if}
    <div class="stats-lists">
      {#each lists as list}
        {@const open = expanded === list.title}
        <section class="panel" aria-label={list.title}>
          <h2>{list.title}</h2>
          <ol>{#each open ? list.rows : list.rows.slice(0, 10) as row, index}<li><span class="rank">{index + 1}</span><MediaArtwork url={row.artworkUrl} class={list.round ? "list-art round" : "list-art"} /><div class="stats-name"><strong>{row.name}</strong><small>{#if row.detail}{row.detail}{" · "}{/if}{countLabel(row.count, "play")}</small></div>
            {#if row.seed || row.artist}
              <DropdownMenu.Root><DropdownMenu.Trigger class="icon-button row-menu" aria-label={`Actions for ${row.name}`}><UiIcon name="more" /></DropdownMenu.Trigger>
                <DropdownMenu.Portal><DropdownMenu.Content class="bits-menu" align="end">
                  {#if row.seed}<DropdownMenu.Item class="bits-menu-item" onSelect={() => void goto(`#/for-you?start=${encodeURIComponent(row.seed)}`)}>Start a mix from this</DropdownMenu.Item>{/if}
                  {#if row.artist}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(following)} onSelect={() => void follow(row.artist)}>Follow {row.artist} for new releases</DropdownMenu.Item>{/if}
                </DropdownMenu.Content></DropdownMenu.Portal>
              </DropdownMenu.Root>
            {/if}</li>{/each}</ol>
          {#if !list.rows.length}<p>{list.empty}</p>{/if}
          {#if list.rows.length > 10}<Button variant="text" onclick={() => expanded = open ? "" : list.title}>{open ? "Show fewer" : `Show all ${list.rows.length}`}</Button>{/if}
        </section>
      {/each}
    </div>
    {#if stats.newArtists?.length}
      <section class="panel new-artists" aria-label="New to you">
        <header><h2>New to you</h2><small>{countLabel(stats.newArtistCount ?? 0, "artist")} you hadn't played before this period</small></header>
        <ul>{#each stats.newArtists as artist}<li><strong>{artist.artist}</strong><small>{countLabel(artist.playCount, "play")}</small></li>{/each}</ul>
      </section>
    {/if}
    <footer>
      <p>{stats.firstPlayAt ?? stats.observedSince ? `Counting since ${new Date((stats.firstPlayAt ?? stats.observedSince)!).toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" })}.` : "Counting starts with your first listen."} {countLabel(stats.coverage.unknownSongIdentityCount, "play couldn't", "plays couldn't")} be identified.</p>
      <p>Imported from other services: {countLabel(stats.importedCount, "play")}</p>
      {#if stats.shortPlayCount}<p>{countLabel(stats.shortPlayCount, "skip or play under 30 seconds isn't counted", "skips and plays under 30 seconds aren't counted")}.</p>{/if}
    </footer>
  {/if}
  <section aria-label="Your data"><h2>Bring in your history</h2><IntelligenceHistoryImports onchanged={importsChanged} /></section>
</div>

<style>
  .your-stats { display: grid; grid-template-columns: minmax(0, 1fr); min-width: 0; gap: var(--section-gap); }
  .visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  .stats-toolbar, .stats-actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); }
  .stats-toolbar { justify-content: space-between; }
  .stats-toolbar :global(.segmented-nav) { min-width: 0; max-width: 100%; }
  .recap-hero { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-6); padding: var(--surface-gutter); }
  .hero-artist { display: flex; align-items: center; gap: var(--space-4); min-width: min(100%, 280px); flex: 1 1 280px; }
  .hero-artist :global(.hero-art) { flex: none; width: 96px; height: 96px; border-radius: 50%; overflow: hidden; }
  .hero-artist :global(.hero-art img) { width: 100%; height: 100%; object-fit: cover; }
  .hero-artist div { display: grid; gap: var(--space-1); min-width: 0; }
  .hero-artist small { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .hero-artist strong { font-size: clamp(1.5rem, 3vw, 2rem); font-weight: 600; line-height: 1.15; overflow-wrap: anywhere; }
  .hero-artist span { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .stats-numbers { display: grid; grid-template-columns: repeat(auto-fit, minmax(8.5rem, 1fr)); gap: var(--space-4) var(--space-6); flex: 2 1 420px; min-width: 0; }
  .stats-numbers article { display: flex; flex-direction: column; gap: var(--space-1); min-width: 0; }
  .stats-numbers strong { font-size: clamp(1.25rem, 2.2vw, 1.75rem); font-weight: 600; line-height: 1.2; font-variant-numeric: tabular-nums; }
  .stats-numbers small, footer, .rank, .stats-name small, .chart-card header small, .new-artists small { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .chart-card { display: grid; gap: var(--space-4); padding: var(--surface-gutter); min-width: 0; }
  .chart-card header, .new-artists header { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: var(--space-1) var(--space-3); }
  .chart-card h2, .new-artists h2 { margin: 0; }
  .chart-row { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--section-gap); }
  .genres { display: grid; gap: var(--space-2); list-style: none; margin: 0; padding: 0; }
  .genres li { display: grid; grid-template-columns: minmax(6rem, 9rem) minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); padding: 0; border: 0; }
  .genre-name { font-size: var(--text-sm); overflow-wrap: anywhere; text-transform: capitalize; }
  .genre-track { display: block; height: 8px; }
  .genre-track span { display: block; height: 100%; min-width: 2px; background: var(--md-sys-color-primary); border-radius: 0 4px 4px 0; }
  .stats-lists { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--section-gap); align-items: start; }
  .stats-lists section { padding: var(--surface-gutter); min-width: 0; }
  h2 { font-size: 1rem; font-weight: 600; margin-bottom: var(--space-4); }
  ol { list-style: none; margin: 0; padding: 0; }
  .stats-lists li { display: flex; align-items: center; gap: var(--space-3); padding-block: var(--space-2); }
  .stats-lists li + li { border-top: 1px solid var(--color-edge); }
  .rank { min-width: 1.25rem; text-align: right; font-variant-numeric: tabular-nums; }
  .stats-lists :global(.list-art) { flex: none; width: 40px; height: 40px; border-radius: var(--radius-sm); overflow: hidden; }
  .stats-lists :global(.list-art.round) { border-radius: 50%; }
  .stats-lists :global(.list-art img) { width: 100%; height: 100%; object-fit: cover; }
  .stats-name { min-width: 0; flex: 1; }
  .stats-lists :global(.row-menu) { flex: none; width: 44px; height: 44px; }
  .stats-name strong, .stats-name small { display: block; overflow-wrap: break-word; }
  .stats-name strong { font-size: var(--text-sm); font-weight: 600; line-height: 1.3; }
  .plays-count { color: var(--color-ink-muted); font-size: var(--text-sm); white-space: nowrap; font-variant-numeric: tabular-nums; }
  @media (max-width: 480px) { .stats-numbers { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  .stats-lists section :global(button) { width: 100%; margin-top: var(--space-2); }
  .new-artists { display: grid; gap: var(--space-4); padding: var(--surface-gutter); }
  .comparison { padding: var(--surface-gutter); }
  .comparison ul { display: flex; flex-wrap: wrap; gap: var(--space-2) var(--space-6); list-style: none; margin: 0; padding: 0; font-size: var(--text-sm); }
  .comparison li strong { font-weight: 600; }
  .comparison li.up strong::before { content: "▲ "; color: var(--md-sys-color-primary); }
  .comparison li.down strong::before { content: "▼ "; color: var(--color-ink-muted); }
  .new-artists ul { display: grid; grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr)); gap: var(--space-3) var(--space-4); list-style: none; margin: 0; padding: 0; }
  .new-artists li { display: grid; gap: 2px; min-width: 0; overflow-wrap: anywhere; }
  .new-artists strong { font-size: var(--text-sm); font-weight: 600; }
  footer { display: grid; gap: var(--space-1); }
  @media (max-width: 1000px) { .chart-row, .stats-lists { grid-template-columns: minmax(0, 1fr); } }
</style>
