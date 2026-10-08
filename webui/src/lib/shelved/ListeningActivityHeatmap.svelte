<script lang="ts">
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import { formatDuration } from "$lib/playlists";

  export type ListeningActivityBucket = {
    date: string;
    count: number;
    importedCount?: number;
    playbackCount?: number;
    durationMilliseconds: number;
  };

  type Granularity = "daily" | "monthly";
  type HeatmapCell = { date: string; index: number };
  type ActivityYear = { id: string; label: string; count: number; importedCount: number };

  let {
    buckets,
    granularity,
    timeZoneId = "UTC",
    from,
    through,
  }: {
    buckets: readonly ListeningActivityBucket[];
    granularity: Granularity;
    timeZoneId?: string;
    from?: string;
    through?: string;
  } = $props();

  let heatmapElement = $state<HTMLElement>();
  let selectedIndex = $state(0);
  let dragging = $state(false);
  let activePointerId = $state<number | null>(null);
  let selectionKey = $state("");
  let selectedYear = $state("");
  let yearSelectionKey = $state("");

  const calendarEnd = $derived(endDateKey());
  const calendarStart = $derived(startDateKey());
  const activityYears = $derived.by((): ActivityYear[] => {
    const years = new Map<string, ActivityYear>();
    for (const bucket of buckets) {
      const id = bucket.date.slice(0, 4);
      if (!/^\d{4}$/.test(id)) continue;
      const current = years.get(id) ?? { id, label: id, count: 0, importedCount: 0 };
      current.count += bucket.count;
      current.importedCount += importedListens(bucket);
      years.set(id, current);
    }
    const endingYear = calendarEnd.slice(0, 4);
    if (/^\d{4}$/.test(endingYear) && !years.has(endingYear))
      years.set(endingYear, { id: endingYear, label: endingYear, count: 0, importedCount: 0 });
    return [...years.values()].sort((left, right) => Number(left.id) - Number(right.id));
  });
  const yearItems = $derived(activityYears.map(({ id, label }) => ({ id, label })));
  const useYearWindow = $derived(longRange());
  const displayStart = $derived(windowStartDateKey());
  const displayEnd = $derived(windowEndDateKey());
  const monthlyBuckets = $derived.by(() => {
    if (!displayStart || !displayEnd) return [];
    const source = new Map<string, ListeningActivityBucket>();
    for (const bucket of buckets) {
      const date = bucket.date.slice(0, 7);
      const current = source.get(date) ?? emptyBucket(date);
      current.count += bucket.count;
      current.importedCount = importedListens(current) + importedListens(bucket);
      current.playbackCount = playbackListens(current) + playbackListens(bucket);
      current.durationMilliseconds += bucket.durationMilliseconds;
      source.set(date, current);
    }
    const first = toUtcDate(displayStart.slice(0, 7));
    const end = toUtcDate(displayEnd.slice(0, 7));
    const count = (end.getUTCFullYear() - first.getUTCFullYear()) * 12 + end.getUTCMonth() - first.getUTCMonth() + 1;
    return Array.from({ length: count }, (_, index) => {
      const month = utcDate(first.getUTCFullYear(), first.getUTCMonth() + index, 1);
      const date = dateKey(month).slice(0, 7);
      return source.get(date) ?? emptyBucket(date);
    });
  });

  const dailyCalendarBuckets = $derived.by(() => {
    if (!displayStart || !displayEnd) return [];
    const first = toUtcDate(displayStart);
    const last = toUtcDate(displayEnd);
    const source = new Map(buckets.map((bucket) => [bucket.date, bucket]));
    const days: ListeningActivityBucket[] = [];
    for (let cursor = new Date(first); cursor <= last; cursor.setUTCDate(cursor.getUTCDate() + 1)) {
      const date = dateKey(cursor);
      days.push(source.get(date) ?? emptyBucket(date));
    }
    return days;
  });

  const dailyCells = $derived.by((): HeatmapCell[] => {
    if (!dailyCalendarBuckets.length) return [];
    const leading = toUtcDate(dailyCalendarBuckets[0].date).getUTCDay();
    const trailing = (7 - ((leading + dailyCalendarBuckets.length) % 7)) % 7;
    return [
      ...Array.from({ length: leading }, () => ({ date: "", index: -1 })),
      ...dailyCalendarBuckets.map((bucket, index) => ({ date: bucket.date, index })),
      ...Array.from({ length: trailing }, () => ({ date: "", index: -1 })),
    ];
  });

  const displayedBuckets = $derived(granularity === "daily" ? dailyCalendarBuckets : monthlyBuckets);
  const maxCount = $derived(Math.max(1, ...displayedBuckets.map((bucket) => bucket.count)));
  const totalCount = $derived(displayedBuckets.reduce((total, bucket) => total + bucket.count, 0));
  const totalImportedCount = $derived(displayedBuckets.reduce((total, bucket) => total + importedListens(bucket), 0));
  const totalPlaybackCount = $derived(displayedBuckets.reduce((total, bucket) => total + playbackListens(bucket), 0));
  const selectedBucket = $derived(displayedBuckets[selectedIndex] ?? displayedBuckets.at(-1));
  const selectedValueText = $derived(selectedBucket
    ? `${dateLabel(selectedBucket.date)}. ${bucketSummary(selectedBucket)}.`
    : "No listening activity recorded.");
  const dailyColumns = $derived(Math.max(1, Math.ceil(dailyCells.length / 7)));

  $effect(() => {
    const nextKey = `${calendarStart}:${calendarEnd}:${activityYears.map((year) => year.id).join(",")}`;
    if (nextKey === yearSelectionKey && activityYears.some((year) => year.id === selectedYear)) return;
    yearSelectionKey = nextKey;
    if (activityYears.some((year) => year.id === selectedYear)) return;
    selectedYear = preferredYear(activityYears)?.id ?? calendarEnd.slice(0, 4);
  });

  $effect(() => {
    const nextKey = `${granularity}:${displayedBuckets.length}:${displayedBuckets[0]?.date ?? ""}:${displayedBuckets.at(-1)?.date ?? ""}`;
    if (nextKey !== selectionKey) {
      selectionKey = nextKey;
      selectedIndex = Math.max(0, displayedBuckets.length - 1);
    } else if (selectedIndex >= displayedBuckets.length) {
      selectedIndex = Math.max(0, displayedBuckets.length - 1);
    }
  });

  function toUtcDate(value: string) {
    const [year, month, day] = value.slice(0, 10).split("-").map(Number);
    return utcDate(year, (month || 1) - 1, day || 1);
  }

  function utcDate(year: number, month: number, day: number) {
    const value = new Date(Date.UTC(2000, month, day, 12));
    value.setUTCFullYear(year);
    return value;
  }

  function dateKey(value: Date) {
    return value.toISOString().slice(0, 10);
  }

  function endDateKey() {
    if (!through) return buckets.at(-1)?.date ?? "";
    return zonedDateKey(new Date(new Date(through).getTime() - 1));
  }

  function startDateKey() {
    if (!from) return "";
    return zonedDateKey(new Date(from));
  }

  function zonedDateKey(date: Date) {
    const parts = new Intl.DateTimeFormat("en-US", {
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      timeZone: timeZoneId,
    }).formatToParts(date);
    const value = Object.fromEntries(parts.map((part) => [part.type, part.value]));
    return `${value.year.padStart(4, "0")}-${value.month}-${value.day}`;
  }

  function longRange() {
    if (!calendarStart || !calendarEnd) return activityYears.length > 1;
    const startingYear = Number(calendarStart.slice(0, 4));
    if (startingYear < 100) return true;
    return Math.round((toUtcDate(calendarEnd).getTime() - toUtcDate(calendarStart).getTime()) / 86_400_000) > 370;
  }

  function windowStartDateKey() {
    if (!calendarEnd) return "";
    if (!useYearWindow) return calendarStart || buckets[0]?.date || calendarEnd;
    const year = Number(selectedYear || preferredYear(activityYears)?.id || calendarEnd.slice(0, 4));
    let start = dateKey(utcDate(year, 0, 1));
    if (Number(calendarStart.slice(0, 4)) >= 100 && calendarStart.slice(0, 4) === String(year) && calendarStart > start)
      start = calendarStart;
    return start;
  }

  function windowEndDateKey() {
    if (!calendarEnd) return "";
    if (!useYearWindow) return calendarEnd;
    const year = Number(selectedYear || preferredYear(activityYears)?.id || calendarEnd.slice(0, 4));
    const end = dateKey(utcDate(year, 11, 31));
    return calendarEnd.slice(0, 4) === String(year) && calendarEnd < end ? calendarEnd : end;
  }

  function preferredYear(years: readonly ActivityYear[]) {
    const hasImports = years.some((year) => year.importedCount > 0);
    return [...years].sort((left, right) =>
      (hasImports ? right.importedCount - left.importedCount : right.count - left.count) ||
      right.count - left.count || Number(right.id) - Number(left.id))[0];
  }

  function importedListens(bucket: ListeningActivityBucket) {
    return bucket.importedCount ?? 0;
  }

  function playbackListens(bucket: ListeningActivityBucket) {
    return bucket.playbackCount ?? Math.max(0, bucket.count - importedListens(bucket));
  }

  function emptyBucket(date: string): ListeningActivityBucket {
    return { date, count: 0, importedCount: 0, playbackCount: 0, durationMilliseconds: 0 };
  }

  function dateLabel(value: string) {
    const date = toUtcDate(value);
    return date.toLocaleDateString(undefined, {
      weekday: granularity === "daily" ? "long" : undefined,
      month: "long",
      day: granularity === "daily" ? "numeric" : undefined,
      year: "numeric",
      timeZone: "UTC",
    });
  }

  function rangeLabel() {
    if (!displayedBuckets.length) return "No listening activity in this period.";
    const first = dateLabel(displayedBuckets[0].date);
    const last = dateLabel(displayedBuckets.at(-1)!.date);
    return `Showing ${first} through ${last}`;
  }

  function listeningTime(milliseconds: number) {
    const minutes = Math.round(milliseconds / 60_000);
    return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} hr ${minutes % 60} min`;
  }

  function averageListen(bucket: ListeningActivityBucket) {
    return bucket.count ? formatDuration(Math.round(bucket.durationMilliseconds / bucket.count)) : "—";
  }

  function intensity(count: number) {
    if (!count) return 0;
    return Math.min(4, Math.max(1, Math.ceil(count / maxCount * 4)));
  }

  function bucketSummary(bucket: ListeningActivityBucket) {
    const listens = `${bucket.count.toLocaleString()} ${bucket.count === 1 ? "listen" : "listens"}`;
    return `${listens}, ${listeningTime(bucket.durationMilliseconds)} total, ${averageListen(bucket)} average, ${importedListens(bucket).toLocaleString()} imported, ${playbackListens(bucket).toLocaleString()} playback`;
  }

  function detailShare(bucket: ListeningActivityBucket) {
    return totalCount ? Math.round(bucket.count / totalCount * 100) : 0;
  }

  function selectIndex(index: number) {
    if (!displayedBuckets.length) return;
    selectedIndex = Math.max(0, Math.min(index, displayedBuckets.length - 1));
  }

  function selectFromPointer(event: PointerEvent) {
    if (!heatmapElement) return;
    const element = document.elementFromPoint(event.clientX, event.clientY);
    const cell = element?.closest<HTMLElement>("[data-heatmap-index]");
    if (!cell || !heatmapElement.contains(cell)) return;
    const index = Number(cell.dataset.heatmapIndex);
    if (Number.isInteger(index)) selectIndex(index);
  }

  function handlePointerMove(event: PointerEvent) {
    if (event.pointerType === "mouse" || dragging) selectFromPointer(event);
  }

  function handlePointerDown(event: PointerEvent) {
    if (event.pointerType === "mouse" && event.button !== 0) return;
    event.preventDefault();
    dragging = true;
    activePointerId = event.pointerId;
    heatmapElement?.setPointerCapture(event.pointerId);
    selectFromPointer(event);
  }

  function handlePointerUp(event: PointerEvent) {
    if (activePointerId !== event.pointerId) return;
    selectFromPointer(event);
    if (heatmapElement?.hasPointerCapture(event.pointerId)) heatmapElement.releasePointerCapture(event.pointerId);
    activePointerId = null;
    dragging = false;
  }

  function handlePointerCancel(event: PointerEvent) {
    if (activePointerId !== event.pointerId) return;
    if (heatmapElement?.hasPointerCapture(event.pointerId)) heatmapElement.releasePointerCapture(event.pointerId);
    activePointerId = null;
    dragging = false;
  }

  function handleKeydown(event: KeyboardEvent) {
    if (!displayedBuckets.length) return;
    const delta = granularity === "daily"
      ? event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1
        : event.key === "ArrowDown" ? 7 : event.key === "ArrowUp" ? -7 : 0
      : event.key === "ArrowRight" || event.key === "ArrowDown" ? 1
        : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
    if (event.key === "Home") {
      event.preventDefault();
      selectIndex(0);
    } else if (event.key === "End") {
      event.preventDefault();
      selectIndex(displayedBuckets.length - 1);
    } else if (delta) {
      event.preventDefault();
      selectIndex(selectedIndex + delta);
    }
  }
</script>

{#if displayedBuckets.length}
  {#if useYearWindow && yearItems.length > 1}
    <div class="heatmap-year-picker">
      <span>Activity year</span>
      <SegmentedNav items={yearItems} active={selectedYear} label="Activity year" class="heatmap-year-tabs" onchange={(year) => selectedYear = year} />
    </div>
  {/if}
  <p class="heatmap-range">{rangeLabel()} · {timeZoneId} · {totalImportedCount.toLocaleString()} imported · {totalPlaybackCount.toLocaleString()} playback</p>
  {#if totalCount === 0}<p class="heatmap-empty">No activity in this period. Play music or import a history file to begin.</p>{/if}
  <div
    bind:this={heatmapElement}
    class:dragging
    class="heatmap-surface"
    role="slider"
    aria-label={`${granularity === "daily" ? "Daily" : "Monthly"} listening activity`}
    aria-valuemin="0"
    aria-valuemax={Math.max(0, displayedBuckets.length - 1)}
    aria-valuenow={selectedIndex}
    aria-valuetext={selectedValueText}
    tabindex="0"
    onkeydown={handleKeydown}
    onpointerdown={handlePointerDown}
    onpointermove={handlePointerMove}
    onpointerup={handlePointerUp}
    onpointercancel={handlePointerCancel}
  >
    {#if granularity === "daily"}
      <div class="heatmap-grid daily-grid" style={`--heatmap-columns:${dailyColumns}`} aria-hidden="true">
        {#each dailyCells as cell}
          {#if cell.index >= 0}
            {@const bucket = displayedBuckets[cell.index]}
            <span class="heatmap-cell" class:selected={cell.index === selectedIndex} data-heatmap-index={cell.index} data-heatmap-date={bucket.date} data-intensity={intensity(bucket.count)}></span>
          {:else}
            <span class="heatmap-cell is-empty"></span>
          {/if}
        {/each}
      </div>
    {:else}
      <div class="heatmap-grid monthly-grid" style={`--heatmap-columns:${displayedBuckets.length}`} aria-hidden="true">
        {#each displayedBuckets as bucket, index}
          <span class="heatmap-cell" class:selected={index === selectedIndex} data-heatmap-index={index} data-heatmap-date={bucket.date} data-intensity={intensity(bucket.count)}></span>
        {/each}
      </div>
    {/if}
  </div>
  <div class="heatmap-legend" aria-hidden="true"><span>Less</span><i data-intensity="0"></i><i data-intensity="1"></i><i data-intensity="2"></i><i data-intensity="3"></i><i data-intensity="4"></i><span>More</span></div>
  {#if selectedBucket}
    <section class="heatmap-detail" aria-live="polite" aria-label="Selected listening activity">
      <div><strong>{dateLabel(selectedBucket.date)}</strong><small>{granularity === "daily" ? "Daily activity" : "Monthly activity"} · {timeZoneId}</small></div>
      <dl>
        <div><dt>Listens</dt><dd>{selectedBucket.count.toLocaleString()}</dd></div>
        <div><dt>Listening time</dt><dd>{listeningTime(selectedBucket.durationMilliseconds)}</dd></div>
        <div><dt>Average listen</dt><dd>{averageListen(selectedBucket)}</dd></div>
        <div><dt>Imported</dt><dd>{importedListens(selectedBucket).toLocaleString()}</dd></div>
        <div><dt>Playback</dt><dd>{playbackListens(selectedBucket).toLocaleString()}</dd></div>
        <div><dt>Share shown</dt><dd>{detailShare(selectedBucket)}%</dd></div>
      </dl>
    </section>
  {/if}
{:else}
  <p class="heatmap-empty">No activity in this period. Play music or import a history file to begin.</p>
{/if}

<style>
  .heatmap-year-picker{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:center;gap:.75rem;margin-top:.75rem}.heatmap-year-picker>span{color:var(--color-ink-muted);font-size:var(--text-xs);font-weight:700}
  .heatmap-range{margin:.75rem 0 0;color:var(--color-ink-muted);font-size:var(--text-xs)}
  .heatmap-surface{max-width:100%;margin-top:.65rem;border-radius:var(--radius-md);outline:0;touch-action:pan-y;user-select:none}
  .heatmap-surface:focus-visible{outline:2px solid var(--focus-ring);outline-offset:4px}
  .heatmap-grid{display:grid;grid-template-columns:repeat(var(--heatmap-columns),minmax(0,1fr));gap:clamp(1px,.18rem,3px);max-width:100%;min-width:0}
  .daily-grid{width:min(100%,calc(var(--heatmap-columns) * 1rem));grid-template-rows:repeat(7,minmax(0,1fr));grid-auto-flow:column}
  .monthly-grid{grid-template-rows:minmax(2.5rem,1fr)}
  .heatmap-cell{display:block;min-width:0;aspect-ratio:1;border:1px solid transparent;border-radius:4px;background:var(--color-panel-raised);cursor:crosshair}
  .heatmap-cell[data-intensity="1"],.heatmap-legend i[data-intensity="1"]{background:color-mix(in srgb,var(--color-signal) 24%,var(--color-panel-raised))}
  .heatmap-cell[data-intensity="2"],.heatmap-legend i[data-intensity="2"]{background:color-mix(in srgb,var(--color-signal) 46%,var(--color-panel-raised))}
  .heatmap-cell[data-intensity="3"],.heatmap-legend i[data-intensity="3"]{background:color-mix(in srgb,var(--color-signal) 70%,var(--color-panel-raised))}
  .heatmap-cell[data-intensity="4"],.heatmap-legend i[data-intensity="4"]{background:var(--color-signal)}
  .heatmap-cell.is-empty{visibility:hidden;background:transparent;cursor:default}
  .heatmap-cell.selected{border-color:var(--color-signal-text);box-shadow:inset 0 0 0 1px var(--color-panel)}
  .heatmap-surface.dragging .heatmap-cell{cursor:grabbing}
  .heatmap-legend{display:flex;align-items:center;justify-content:flex-end;gap:.3rem;margin-top:.55rem;color:var(--color-ink-muted);font-size:var(--text-xs)}
  .heatmap-legend i{display:block;width:.7rem;height:.7rem;border:1px solid var(--color-edge);border-radius:2px;background:var(--color-panel-raised)}
  .heatmap-detail{display:grid;grid-template-columns:minmax(12rem,.65fr) minmax(0,1.35fr);align-items:center;gap:1rem;margin-top:.85rem;border-top:1px solid var(--color-edge);padding-top:.85rem}
  .heatmap-detail>div{display:grid;gap:.15rem}.heatmap-detail small,.heatmap-detail dt{color:var(--color-ink-muted);font-size:var(--text-xs)}
  .heatmap-detail dl{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:.75rem;margin:0}.heatmap-detail dl div{display:flex;min-width:0;flex-direction:column-reverse;gap:.1rem}.heatmap-detail dd{overflow:hidden;margin:0;font-family:var(--font-display);font-weight:750;text-overflow:ellipsis;white-space:nowrap}
  .heatmap-empty{margin:.75rem 0 0;color:var(--color-ink-muted)}
  @media (prefers-reduced-motion: reduce){.heatmap-surface,.heatmap-cell{transition:none}}
  @media(max-width:620px){.heatmap-year-picker{grid-template-columns:1fr;gap:.35rem}.heatmap-detail{grid-template-columns:1fr}.heatmap-detail dl{grid-template-columns:1fr 1fr}.monthly-grid{grid-template-rows:minmax(2.25rem,1fr)}.daily-grid .heatmap-cell{min-height:.5rem;aspect-ratio:auto}.heatmap-cell{border-radius:3px}.heatmap-surface{overflow:hidden}}
</style>
