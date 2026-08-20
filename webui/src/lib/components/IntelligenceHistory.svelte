<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Badge } from "$lib/components/ui/badge";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { FileUp, X } from "@lucide/svelte";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import {
    intelligence,
    type IntelligenceScope,
    type ListeningHistoryActivity,
    type ListeningHistoryDetail,
    type ListeningHistoryImport,
    type ListeningHistoryItem,
    type ListeningHistoryOverview,
    type ListeningHistoryTopItem,
  } from "$lib/api";
  import { formatDuration } from "$lib/playlists";

  type HistorySection = "overview" | "history" | "imports";
  type ImportQueueItem = {
    id: string;
    file: File | null;
    fileKey: string;
    fileName: string;
    sizeBytes: number;
    error: string;
    result: ListeningHistoryImport | null;
    selected: boolean;
  };

  let { scope, section, policyEnabled = false, retentionDays = 0, onChanged = () => {} }: {
    scope: IntelligenceScope;
    section: HistorySection;
    policyEnabled?: boolean;
    retentionDays?: number;
    onChanged?: () => void | Promise<void>;
  } = $props();

  let period = $state("all");
  let fromDate = $state(isoDate(new Date(Date.now() - 29 * 86_400_000)));
  let toDate = $state(isoDate(new Date()));
  let timeZoneId = $state(Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
  let search = $state("");
  let source = $state("");
  let client = $state("");
  let artist = $state("");
  let album = $state("");
  let track = $state("");
  let overview = $state<ListeningHistoryOverview | null>(null);
  let activity = $state<ListeningHistoryActivity | null>(null);
  let top = $state<Record<"artist" | "album" | "track", ListeningHistoryTopItem[]>>({ artist: [], album: [], track: [] });
  let topKind = $state<"artist" | "album" | "track">("artist");
  let activityGranularity = $state<"daily" | "monthly">("daily");
  let items = $state<ListeningHistoryItem[]>([]);
  let nextCursor = $state<string | null>(null);
  let loading = $state(false);
  let historyLoading = $state(false);
  let action = $state("");
  let error = $state("");
  let detail = $state<ListeningHistoryDetail | null>(null);
  let detailOpen = $state(false);
  let detailError = $state("");
  let editTitle = $state("");
  let editArtist = $state("");
  let editAlbum = $state("");
  let editAlbumArtist = $state("");
  let deleteOpen = $state(false);
  let importDeleteOpen = $state(false);
  let importDeleteItem = $state<ImportQueueItem | null>(null);
  let importItems = $state<ImportQueueItem[]>([]);
  let retainedListenCount = $state<number | null>(null);
  let readingImportId = $state("");
  let importError = $state("");
  let importDragging = $state(false);
  let importSequence = 0;
  let loadedKey = "";
  let previousScopeKey = "";

  const scopeKey = $derived(`${scope.protocol}\0${scope.backendInstanceId}\0${scope.libraryScopeId}`);
  const selectedTop = $derived(top[topKind]);
  const topTrack = $derived(top.track[0]);
  const periodName = $derived(period === "all" ? "All time" : period === "365" ? "Last year" : period === "custom" ? "Selected dates" : `Last ${period} days`);
  const exportUrl = $derived(intelligence.historyExportUrl(scope));
  const pendingImportItems = $derived(importItems.filter((item) => item.file && !item.result));
  const completedImportItems = $derived(importItems.filter((item) => item.result));
  const previewedImportItems = $derived(completedImportItems.filter((item) => item.result?.state === "previewed"));
  const selectedPreviewItems = $derived(previewedImportItems.filter((item) => item.selected));
  const activeImportItems = $derived(completedImportItems.filter((item) => item.result?.state === "pending" || item.result?.state === "running"));
  const hasStaleImportReceipts = $derived(retainedListenCount === 0 && completedImportItems.some((item) =>
    item.result?.state === "completed" && (item.result.importedRows ?? 0) > 0));
  const hasHistoryFilters = $derived(Boolean(search.trim() || source.trim() || client.trim() || artist.trim() || album.trim() || track.trim()));
  const monthlyActivity = $derived.by(() => {
    const months = new Map<string, { date: string; count: number; durationMilliseconds: number }>();
    for (const bucket of activity?.buckets ?? []) {
      const date = bucket.date.slice(0, 7);
      const current = months.get(date) ?? { date, count: 0, durationMilliseconds: 0 };
      current.count += bucket.count;
      current.durationMilliseconds += bucket.durationMilliseconds;
      months.set(date, current);
    }
    return [...months.values()].slice(-12);
  });
  const importBatch = $derived(previewedImportItems.reduce((summary, item) => {
    const preview = item.result?.preview;
    if (preview) {
      summary.newRows += preview.newRows;
      summary.duplicates += preview.duplicateExisting + preview.duplicateInFile;
      summary.skipped += preview.skipped;
      summary.outsideRetention += preview.outsideRetentionRows ?? 0;
    }
    return summary;
  }, { newRows: 0, duplicates: 0, skipped: 0, outsideRetention: 0 }));

  $effect(() => {
    if (scopeKey !== previousScopeKey) {
      previousScopeKey = scopeKey;
      detailOpen = false;
      detail = null;
      importItems = [];
    }
    const key = `${scopeKey}\0${section}\0${period}\0${fromDate}\0${toDate}\0${timeZoneId}`;
    if (key === loadedKey) return;
    loadedKey = key;
    if (section === "imports") void loadImports();
    else void loadAll();
  });

  $effect(() => {
    if (!activeImportItems.length) return;
    const imports = activeImportItems.map((item) => ({ queueId: item.id, importId: item.result!.importId }));
    const timer = setTimeout(() => imports.forEach((item) => void refreshImport(item.queueId, item.importId)), 1500);
    return () => clearTimeout(timer);
  });

  function isoDate(value: Date) {
    return value.toISOString().slice(0, 10);
  }

  function bounds() {
    if (period === "all") return { from: undefined, to: undefined, timeZoneId };
    const end = period === "custom" ? new Date(`${toDate}T00:00:00.000Z`) : new Date();
    if (period === "custom") end.setUTCDate(end.getUTCDate() + 1);
    const start = period === "custom"
      ? new Date(`${fromDate}T00:00:00.000Z`)
      : new Date(end.getTime() - Number(period) * 86_400_000);
    return { from: start.toISOString(), to: end.toISOString(), timeZoneId };
  }

  function historyQuery(cursor?: string) {
    return {
      ...bounds(), limit: 50, cursor,
      search: search.trim(), source: source.trim(), client: client.trim(),
      artist: artist.trim(), album: album.trim(), track: track.trim(),
    };
  }

  async function loadAll() {
    loading = true;
    error = "";
    const range = bounds();
    try {
      const [nextOverview, nextActivity, artists, albums, tracks, history] = await Promise.all([
        intelligence.historyOverview(scope, range.from, range.to, range.timeZoneId),
        intelligence.historyActivity(scope, range.from, range.to, range.timeZoneId),
        intelligence.historyTop(scope, "artist", range.from, range.to, range.timeZoneId),
        intelligence.historyTop(scope, "album", range.from, range.to, range.timeZoneId),
        intelligence.historyTop(scope, "track", range.from, range.to, range.timeZoneId),
        intelligence.history(scope, historyQuery()),
      ]);
      overview = nextOverview;
      activity = nextActivity;
      top = { artist: artists.items, album: albums.items, track: tracks.items };
      items = history.items;
      nextCursor = history.nextCursor ?? null;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Listening history could not be loaded.";
    } finally {
      loading = false;
    }
  }

  async function loadHistory(reset = true) {
    historyLoading = true;
    error = "";
    try {
      const response = await intelligence.history(scope, historyQuery(reset ? undefined : nextCursor ?? undefined));
      items = reset ? response.items : [...items, ...response.items];
      nextCursor = response.nextCursor ?? null;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Listening history could not be loaded.";
    } finally {
      historyLoading = false;
    }
  }

  async function loadImports() {
    importError = "";
    try {
      const [response, retained] = await Promise.all([
        intelligence.historyImports(scope),
        intelligence.historyOverview(scope, undefined, undefined, timeZoneId).catch(() => null),
      ]);
      retainedListenCount = retained?.allTime.completedListens ?? null;
      importItems = response.items.map((result) => ({
        id: `saved-history-import-${result.importId}`,
        file: null,
        fileKey: `saved:${result.importId}`,
        fileName: result.displayFileName ?? "History import",
        sizeBytes: result.sizeBytes ?? 0,
        error: "",
        result,
        selected: result.state === "previewed" && (result.preview?.newRows ?? 0) > 0,
      }));
    } catch (cause) {
      retainedListenCount = null;
      importError = cause instanceof Error ? cause.message : "Saved history imports could not be loaded.";
    }
  }

  async function openDetail(item: ListeningHistoryItem) {
    detailOpen = true;
    detail = null;
    detailError = "";
    try {
      detail = await intelligence.historyDetail(scope, item.id);
      editTitle = detail.item.title ?? "";
      editArtist = detail.item.artist ?? "";
      editAlbum = detail.item.album ?? "";
      editAlbumArtist = detail.identity.albumArtist ?? "";
    } catch (cause) {
      detailError = cause instanceof Error ? cause.message : "This listen could not be opened.";
    }
  }

  async function saveDetail(event: SubmitEvent) {
    event.preventDefault();
    if (!detail) return;
    action = "save-detail";
    detailError = "";
    try {
      await intelligence.correctHistory(scope, detail.item.id, {
        title: editTitle,
        artist: editArtist,
        album: editAlbum || null,
        albumArtist: editAlbumArtist || null,
        expectedRevision: detail.item.revision,
      });
      await openDetail(detail.item);
      await loadHistory();
    } catch (cause) {
      detailError = cause instanceof Error ? cause.message : "This listen could not be saved.";
    } finally {
      action = "";
    }
  }

  async function removeDetail() {
    if (!detail) return;
    action = "delete-detail";
    detailError = "";
    try {
      await intelligence.deleteHistory(scope, detail.item.id, detail.item.revision);
      deleteOpen = false;
      detailOpen = false;
      detail = null;
      await loadAll();
    } catch (cause) {
      detailError = cause instanceof Error ? cause.message : "This listen could not be deleted.";
    } finally {
      action = "";
    }
  }

  function importFileError(file: File) {
    const extension = file.name.split(".").at(-1)?.toLowerCase();
    if (!extension || !["json", "jsonl", "zip"].includes(extension)) return "Choose a JSON, JSONL, or ZIP export.";
    if (file.size > 64 * 1024 * 1024) return "This file is larger than 64 MB.";
    return "";
  }

  function queueImportFiles(files: File[]) {
    const existing = new Set(importItems.map((item) => item.fileKey));
    const additions = files.flatMap((file) => {
      const fileKey = `${file.name}\0${file.size}\0${file.lastModified}`;
      if (existing.has(fileKey)) return [];
      existing.add(fileKey);
      return [{
        id: `history-file-${++importSequence}`,
        file,
        fileKey,
        fileName: file.name,
        sizeBytes: file.size,
        error: importFileError(file),
        result: null,
        selected: false,
      }];
    });
    importItems = [...importItems, ...additions];
    const valid = additions.filter((item) => !item.error);
    if (valid.length) void previewImports(valid);
  }

  function chooseImportFiles(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    queueImportFiles(Array.from(input.files ?? []));
    input.value = "";
  }

  function dropImportFiles(event: DragEvent) {
    event.preventDefault();
    importDragging = false;
    queueImportFiles(Array.from(event.dataTransfer?.files ?? []));
  }

  function updateImportItem(id: string, changes: Partial<ImportQueueItem>) {
    importItems = importItems.map((item) => item.id === id ? { ...item, ...changes } : item);
  }

  function removeImportItem(id: string) {
    importItems = importItems.filter((item) => item.id !== id);
  }

  async function previewImports(pending: ImportQueueItem[]) {
    const requestedScope = { ...scope };
    const requestedScopeKey = scopeKey;
    pending = pending.map((item) => ({ ...item }));
    if (!pending.length || action) return;
    action = "preview-import";
    importError = "";
    for (const item of pending) {
      readingImportId = item.id;
      updateImportItem(item.id, { error: "" });
      try {
        const result = await intelligence.previewHistoryImport(requestedScope, item.file!);
        if (scopeKey === requestedScopeKey) updateImportItem(item.id, {
          file: null,
          result,
          selected: result.state === "previewed" && (result.preview?.newRows ?? 0) > 0,
        });
      } catch (cause) {
        if (scopeKey === requestedScopeKey) updateImportItem(item.id, {
          error: cause instanceof Error ? cause.message : "This history file could not be read.",
        });
      }
    }
    readingImportId = "";
    action = "";
  }

  async function changeImport(item: ImportQueueItem, operation: "apply" | "resume" | "cancel") {
    if (!item.result) return;
    action = `${operation}:${item.id}`;
    importError = "";
    try {
      const result = await intelligence.changeHistoryImport(scope, item.result, operation);
      updateImportItem(item.id, { result });
      if (result.state === "completed") {
        await loadAll();
        await onChanged();
      }
    } catch (cause) {
      importError = cause instanceof Error ? cause.message : "The history import could not be changed.";
    } finally {
      action = "";
    }
  }

  function confirmRemoveImport(item: ImportQueueItem) {
    importDeleteItem = item;
    importDeleteOpen = true;
  }

  async function removeSavedImport() {
    const item = importDeleteItem;
    if (!item?.result) return;
    action = `remove:${item.id}`;
    importError = "";
    try {
      const result = await intelligence.removeHistoryImport(scope, item.result);
      removeImportItem(item.id);
      importDeleteOpen = false;
      importDeleteItem = null;
      if (result.removedListens > 0) await onChanged();
    } catch (cause) {
      importError = cause instanceof Error ? cause.message : "This history import could not be removed.";
      importDeleteOpen = false;
    } finally {
      action = "";
    }
  }

  async function applyAllPreviews() {
    const items = selectedPreviewItems.map((item) => ({ ...item }));
    if (!items.length || action) return;
    action = "apply-all";
    importError = "";
    let completed = false;
    for (const item of items) {
      try {
        const result = await intelligence.changeHistoryImport(scope, item.result!, "apply");
        updateImportItem(item.id, { result });
        completed ||= result.state === "completed";
      } catch (cause) {
        importError ||= cause instanceof Error ? cause.message : "A history import could not be started.";
      }
    }
    if (completed) {
      await loadAll();
      await onChanged();
    }
    action = "";
  }

  async function refreshImport(queueId: string, importId: string) {
    try {
      const previous = importItems.find((item) => item.id === queueId)?.result;
      const next = await intelligence.historyImport(scope, importId);
      const completed = next.state === "completed" && previous?.state !== "completed";
      updateImportItem(queueId, { result: next });
      if (completed) {
        await loadAll();
        await onChanged();
      }
    } catch (cause) {
      importError = cause instanceof Error ? cause.message : "Import progress could not be loaded.";
    }
  }

  function listeningTime(milliseconds: number) {
    const minutes = Math.round(milliseconds / 60_000);
    return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} hr ${minutes % 60} min`;
  }

  function words(value?: string | null) {
    if (!value) return "Unknown";
    if (value.toLowerCase() === "notrequested") return "Not requested";
    return value.replaceAll("_", " ").replaceAll("-", " ");
  }

  function sourceLabel(value?: string | null) {
    if (!value) return "Unknown source";
    return ({
      jellyfin: "Jellyfin",
      lastfm: "Last.fm",
      listenbrainz: "ListenBrainz",
      spotify: "Spotify",
      subsonic: "Subsonic",
    } as Record<string, string>)[value.toLowerCase()] ?? words(value);
  }

  function clientLabel(value?: string | null) {
    if (!value) return "Unknown client";
    return ({ ios: "iOS", macos: "macOS", tvos: "tvOS" } as Record<string, string>)[value.toLowerCase()] ?? words(value);
  }

  function listenDate(value?: string | null) {
    return value
      ? new Date(value).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" })
      : "Date unavailable";
  }

  function listenTime(value?: string | null, durationMilliseconds?: number | null) {
    const time = value
      ? new Date(value).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })
      : "Time unavailable";
    return durationMilliseconds ? `${time} · ${formatDuration(durationMilliseconds)}` : time;
  }

  function musicBrainzState(value?: string | null) {
    return ({
      notrequested: "No lookup queued",
      pending: "Lookup queued",
      resolved: "Details found",
      unresolved: "No match found",
      failed: "Lookup failed",
    } as Record<string, string>)[value?.toLowerCase() ?? ""] ?? words(value);
  }

  function fileSize(bytes: number) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  function retentionLabel(days: number) {
    if (days === 0) return "all history";
    if (days === 3650) return "10 years";
    if (days === 365) return "1 year";
    return `${days} days`;
  }

  function outsideCurrentRetention(item: ListeningHistoryImport) {
    if (retentionDays === 0) return false;
    const latest = item.preview?.latest ? new Date(item.preview.latest).getTime() : 0;
    return item.state === "completed" && Boolean(item.importedRows) && latest > 0 &&
      latest < Date.now() - retentionDays * 86_400_000;
  }
</script>

<section class="history-workspace">
  {#if section !== "imports"}
    <header class="history-heading">
      <div><p class="eyebrow">Your listening</p><h3>{section === "overview" ? "Listening overview" : "Listening history"}</h3><p>Private activity for this account and library only.</p></div>
      {#if section === "overview"}<div class="history-actions"><Badge state={policyEnabled ? "healthy" : "suggested"}>{policyEnabled ? retentionDays === 0 ? "Retention: Unlimited" : `Retention: ${retentionLabel(retentionDays)}` : "Saving off"}</Badge></div>
      {:else}<div class="history-actions"><Button variant="secondary" href={exportUrl} download>Download my history</Button></div>{/if}
    </header>

    <form class="history-toolbar panel" onsubmit={(event) => { event.preventDefault(); void (section === "overview" ? loadAll() : loadHistory()); }}>
      {#if section === "history"}<SearchField bind:value={search} label="Search listening history" placeholder="Search songs, artists, or albums" />{/if}
      <SegmentedNav items={[
        { id: "all", label: "All time" }, { id: "30", label: "30 days" }, { id: "90", label: "90 days" },
        { id: "365", label: "1 year" }, { id: "custom", label: "Custom" },
      ]} active={period} label="History period" class="period-tabs" onchange={(value) => period = value} />
      <Button type="submit" disabled={loading || historyLoading}>{loading || historyLoading ? "Updating…" : section === "overview" ? "Update overview" : "Apply filters"}</Button>
      <details class="filter-disclosure">
        <summary class="disclosure-summary"><DisclosureLabel title={section === "overview" ? "Dates and time zone" : "More filters"} description={section === "overview" ? "Choose an exact reporting window" : "Filter by artist, album, song, source, or client"} /></summary>
        <div class="advanced-filters">
          {#if period === "custom"}<label class="field"><span>From</span><input bind:value={fromDate} type="date" required /></label><label class="field"><span>Through</span><input bind:value={toDate} type="date" required /></label>{/if}
          {#if section === "history"}
            <label class="field"><span>Artist</span><input bind:value={artist} maxlength="500" /></label>
            <label class="field"><span>Album</span><input bind:value={album} maxlength="500" /></label>
            <label class="field"><span>Song</span><input bind:value={track} maxlength="500" /></label>
            <label class="field"><span>Source</span><input bind:value={source} maxlength="32" placeholder="import or playback" /></label>
            <label class="field"><span>Client</span><input bind:value={client} maxlength="200" placeholder="Jellyfin or Subsonic" /></label>
          {/if}
          <label class="field"><span>Time zone</span><input bind:value={timeZoneId} maxlength="100" /></label>
        </div>
      </details>
    </form>

    {#if loading}
      <div class="history-stats" role="status" aria-busy="true" aria-label="Loading listening history">{#each Array(5) as _}<Skeleton class="panel skeleton-panel" />{/each}</div>
    {/if}
  {/if}
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

  {#if section === "overview" && !loading && overview}
    {#if overview.nowPlaying}
      <button class="panel now-playing" type="button" onclick={() => void openDetail(overview!.nowPlaying!)}><span aria-hidden="true">▶</span><span><small>Playing now</small><strong>{overview.nowPlaying.title ?? "Unknown song"}</strong><small>{overview.nowPlaying.artist ?? "Unknown artist"}</small></span></button>
    {/if}
    <div class="history-stats">
      <article class="panel"><strong>{overview.selected.completedListens.toLocaleString()}</strong><span>listens</span></article>
      <article class="panel"><strong>{listeningTime(overview.selected.listeningTimeMilliseconds)}</strong><span>listening time</span></article>
      <article class="panel"><strong>{overview.selected.distinctTracks.toLocaleString()}</strong><span>different songs</span></article>
      <article class="panel"><strong>{overview.selected.distinctArtists.toLocaleString()}</strong><span>artists</span></article>
      <article class="panel"><strong>{overview.currentStreakDays} days</strong><span>current streak · {overview.longestStreakDays} longest</span></article>
    </div>

    <section class="panel recap-card">
      <div><p class="eyebrow">Listening recap</p><h3>{periodName}</h3></div>
      {#if overview.selected.completedListens}
        <p>You listened {overview.selected.completedListens.toLocaleString()} times across {overview.selected.distinctTracks.toLocaleString()} songs and {overview.selected.distinctArtists.toLocaleString()} artists, for {listeningTime(overview.selected.listeningTimeMilliseconds)}.{#if topTrack} Your most-played song was <strong>{topTrack.title ?? "Unknown song"}</strong>{topTrack.artist ? ` by ${topTrack.artist}` : ""}.{/if}</p>
      {:else}<p>No completed listens were recorded for this period. Play music or import a history file to build this recap.</p>{/if}
      {#if overview.allTime.firstListen}<p class="muted">Your first recorded listen was <time datetime={overview.allTime.firstListen}>{new Date(overview.allTime.firstListen).toLocaleDateString()}</time>.</p>{/if}
    </section>

    <div class="history-insights">
      <section class="panel activity-card">
        <header><div><p class="eyebrow">Activity</p><h3>Listening rhythm</h3></div><SegmentedNav items={[{ id: "daily", label: "Daily" }, { id: "monthly", label: "Monthly" }]} active={activityGranularity} label="Listening activity period" onchange={(value) => activityGranularity = value as typeof activityGranularity} /></header>
        {#if activityGranularity === "daily" && activity?.buckets.length}
          {@const displayedActivity = activity.buckets.slice(-90)}
          {@const mobileActivity = displayedActivity.slice(-30)}
          <p class="activity-range">
            <span class="activity-range-wide">Showing {displayedActivity[0].date} through {displayedActivity.at(-1)!.date}</span>
            <span class="activity-range-mobile">Showing {mobileActivity[0].date} through {mobileActivity.at(-1)!.date}</span>
          </p>
          <div class="activity-grid" aria-label="Listening activity by day">
            {#each displayedActivity as bucket}
              <span style={`--activity:${Math.min(1, .18 + bucket.count / 20)}`} title={`${bucket.date}: ${bucket.count} listens`} aria-label={`${bucket.date}: ${bucket.count} listens`}></span>
            {/each}
          </div>
        {:else if activityGranularity === "monthly" && monthlyActivity.length}
          {@const largestMonth = Math.max(...monthlyActivity.map((item) => item.count), 1)}
          <ol class="monthly-activity">
            {#each monthlyActivity as bucket}<li><span><strong>{new Date(`${bucket.date}-01T00:00:00`).toLocaleDateString(undefined, { month: "short", year: "numeric" })}</strong><small>{listeningTime(bucket.durationMilliseconds)}</small></span><meter min="0" max={largestMonth} value={bucket.count}>{bucket.count}</meter><span>{bucket.count} listens</span></li>{/each}
          </ol>
        {:else}<p class="muted">No activity in this period. Play music or import a history file to begin.</p>{/if}
      </section>

      <section class="panel top-card">
        <header><div><p class="eyebrow">Most played</p><h3>Top {topKind === "track" ? "songs" : `${topKind}s`}</h3></div><SegmentedNav items={[{ id: "artist", label: "Artists" }, { id: "album", label: "Albums" }, { id: "track", label: "Songs" }]} active={topKind} label="Top listening category" onchange={(value) => topKind = value as typeof topKind} /></header>
        <ol>{#each selectedTop.slice(0, 5) as item}<li><span><strong>{topKind === "artist" ? item.artist : topKind === "album" ? item.album : item.title}</strong>{#if topKind !== "artist"}<small>{item.artist}</small>{/if}</span><span>{item.listenCount} listens</span></li>{:else}<li class="muted">Nothing to rank yet. Play music or import a history file to begin.</li>{/each}</ol>
      </section>
    </div>

    <div class="history-breakdowns">
      {#each [
        { label: "Sources", items: overview.breakdowns?.sources ?? [] },
        { label: "Providers", items: overview.breakdowns?.providers ?? [] },
        { label: "Listening apps", items: overview.breakdowns?.clients ?? [] },
      ] as group}
        <section class="panel breakdown-card"><p class="eyebrow">Listening mix</p><h3>{group.label}</h3><ol>{#each group.items.slice(0, 5) as item}<li><span><strong>{words(item.value)}</strong><small>{listeningTime(item.durationMilliseconds)}</small></span><span>{item.listenCount} listens</span></li>{:else}<li class="muted">No {group.label.toLowerCase()} recorded.</li>{/each}</ol></section>
      {/each}
    </div>
  {/if}

  {#if (section === "overview" || section === "history") && !loading}
    {@const shownItems = section === "overview" ? items.slice(0, 5) : items}
    <section class="panel history-list-card">
      <header><div><h3>{section === "overview" ? "Recently played" : "Recent listens"}</h3><p>{section === "overview" ? "Your latest completed listens." : "Open a listen to review or correct its song details."}</p></div><span class="history-count">{shownItems.length} {shownItems.length === 1 ? "listen" : "listens"}</span></header>
      {#if shownItems.length}<div class="history-column-head" aria-hidden="true"><span></span><span>Track</span><span>Source</span><span>Listened</span></div>{/if}
      <ul class="history-list">
        {#each shownItems as item}
          <li><button type="button" onclick={() => void openDetail(item)}>
            <MediaArtwork class="track-art" url={item.artworkUrl} />
            <span class="history-copy"><strong>{item.title ?? "Unknown song"}</strong><small>{item.artist ?? "Unknown artist"}{item.album ? ` · ${item.album}` : ""}</small></span>
            <span class="history-meta">
              <span class="history-route"><strong>{sourceLabel(item.provider ?? item.source)}</strong><small>{clientLabel(item.client)}</small>{#if item.targetStatuses.length}<small>{item.targetStatuses.map((status) => `${sourceLabel(status.target)}: ${words(status.state)}`).join(" · ")}</small>{/if}</span>
              <span class="history-time"><time datetime={item.listenedAt ?? undefined}>{listenDate(item.listenedAt)}</time><small>{listenTime(item.listenedAt, item.durationMilliseconds)}</small></span>
            </span>
          </button></li>
        {:else}
          <li>{#if section === "history" && hasHistoryFilters}<div class="compact-empty"><strong>No listens match these filters</strong><p>Try a wider period or clear a filter.</p></div>
          {:else}<div class="compact-empty"><strong>No completed listens yet</strong><p>Turn on automatic history in Automation, then play music or import a history file.</p></div>{/if}</li>
        {/each}
      </ul>
      {#if section === "history" && nextCursor}<Button class="load-more" variant="secondary" disabled={historyLoading} onclick={() => void loadHistory(false)}>{historyLoading ? "Loading…" : "Load older listens"}</Button>{/if}
    </section>
  {/if}

  {#if section === "imports"}<section class="panel import-card">
    <header><div><p class="eyebrow">Spotify and service exports</p><h3>Import listening history</h3><p><strong>Spotify:</strong> upload the JSON files from your Extended Streaming History download. Last.fm, ListenBrainz, Koito, and Maloja exports are also supported. Every file is previewed before anything is added.</p></div></header>
    {#if importError}<p class="notice-error" role="alert">{importError}</p>{/if}
    <div class="import-picker">
      <label class="upload-zone" class:dragging={importDragging} ondragenter={(event) => { event.preventDefault(); importDragging = true; }} ondragover={(event) => event.preventDefault()} ondragleave={() => { importDragging = false; }} ondrop={dropImportFiles}>
        <input aria-label="History export files" type="file" accept="application/json,text/plain,application/zip,.json,.jsonl,.zip" multiple disabled={Boolean(action)} onchange={chooseImportFiles} />
        <span class="upload-symbol" aria-hidden="true"><FileUp size={22} /></span>
        <span class="upload-copy"><strong>Choose or drop history export files</strong><small>Spotify JSON, or Last.fm, ListenBrainz, Koito, and Maloja JSON, JSONL, or ZIP · 64 MB each</small></span>
        <span class="upload-browse" aria-hidden="true">Browse files</span>
      </label>
      {#if pendingImportItems.length}
        <ul class="upload-queue" aria-label="Selected history exports" aria-live="polite">
          {#each pendingImportItems as item}
            <li class:error={Boolean(item.error)}>
              <span class="file-marker" aria-hidden="true"><FileUp size={16} /></span>
              <span class="file-copy"><strong>{item.fileName}</strong><small>{readingImportId === item.id ? "Reading file…" : item.error || fileSize(item.sizeBytes)}</small></span>
              {#if readingImportId !== item.id}<button class="icon-button" type="button" aria-label={`Remove ${item.fileName}`} disabled={Boolean(action)} onclick={() => removeImportItem(item.id)}><X size={16} aria-hidden="true" /></button>{/if}
            </li>
          {/each}
        </ul>
      {/if}
    </div>
    <div class:warning={!policyEnabled} class="import-readiness">
      <Badge state={policyEnabled ? "healthy" : "suggested"}>{policyEnabled ? "Saving on" : "Saving off"}</Badge>
      <span><strong>{policyEnabled ? `Keeping ${retentionLabel(retentionDays)}` : "Recommendations are off"}</strong><small>{policyEnabled ? retentionDays === 0 ? "Imported listens are kept until you remove them or choose a limit." : `Imported listens older than ${retentionLabel(retentionDays)} are removed automatically.` : `You can preview files, but turn on automatic history and choose retention before adding them.`}</small></span>
      <Button variant="secondary" href="#/intelligence?section=automation">Review automation</Button>
    </div>
    {#if hasStaleImportReceipts}
      <div class="import-readiness warning" role="status">
        <Badge state="suggested">Re-import needed</Badge>
        <span><strong>No imported listens are currently retained</strong><small>These completed rows are import receipts, not saved listening events. Re-upload the original export files to restore Overview and History.</small></span>
      </div>
    {/if}
    {#if completedImportItems.length}
      {#if previewedImportItems.length}
        <dl class="import-batch-summary"><div><dd>{importBatch.newRows.toLocaleString()}</dd><dt>will be kept</dt></div><div><dd>{importBatch.outsideRetention.toLocaleString()}</dd><dt>outside retention</dt></div><div><dd>{importBatch.duplicates.toLocaleString()}</dd><dt>already present</dt></div><div><dd>{importBatch.skipped.toLocaleString()}</dd><dt>skipped</dt></div></dl>
        <div class="import-batch-actions"><span><strong>{selectedPreviewItems.length} of {previewedImportItems.length} ready files selected</strong><small>Files with new listens are selected automatically. Clear any file you do not want to add.</small></span><Button disabled={Boolean(action) || !selectedPreviewItems.length} onclick={() => void applyAllPreviews()}>{action === "apply-all" ? "Starting imports…" : `Add all ${selectedPreviewItems.length} ready ${selectedPreviewItems.length === 1 ? "file" : "files"}`}</Button></div>
      {/if}
      <ul class="import-results" aria-label="History import previews">
        {#each completedImportItems as item}
          {@const result = item.result!}
          {@const activeImport = result.state === "pending" || result.state === "running"}
          <li class="import-result">
            <header>
              {#if result.state === "previewed"}
                <label class="import-result-select"><Checkbox aria-label={`Include ${result.displayFileName ?? item.fileName}`} checked={item.selected} onCheckedChange={(checked) => updateImportItem(item.id, { selected: checked })} /><span role="status" aria-atomic="true"><strong>{result.displayFileName ?? item.fileName}</strong><small>{fileSize(result.sizeBytes ?? item.sizeBytes)} · {words(result.state)}</small></span></label>
              {:else}
                <span class="import-result-select" role="status" aria-atomic="true"><span><strong>{result.displayFileName ?? item.fileName}</strong><small>{fileSize(result.sizeBytes ?? item.sizeBytes)} · {words(result.state)}{result.importedRows !== undefined ? ` · ${result.importedRows.toLocaleString()} added when imported` : ""}</small></span></span>
              {/if}
              <Badge state={result.state === "completed" ? "healthy" : result.state === "failed" || result.state === "cancelled" ? "rejected" : "suggested"}>{words(result.state)}</Badge>
            </header>
            {#if result.preview}
              <dl class="import-preview"><div><dd>{result.preview.newRows.toLocaleString()}</dd><dt>{result.state === "previewed" ? "will be kept" : "eligible at import"}</dt></div><div><dd>{(result.preview.outsideRetentionRows ?? 0).toLocaleString()}</dd><dt>outside retention</dt></div><div><dd>{(result.preview.duplicateExisting + result.preview.duplicateInFile).toLocaleString()}</dd><dt>already present</dt></div><div><dd>{result.preview.skipped.toLocaleString()}</dd><dt>skipped</dt></div></dl>
              {#if result.state === "previewed" && (result.preview.outsideRetentionRows ?? 0) > 0}<p class="credential-safety">Only listens inside your current {retentionLabel(retentionDays)} window will be saved. Change retention in Automation, then preview the files again if you want to keep older history.</p>
              {:else if outsideCurrentRetention(result)}<p class="credential-safety">These imported listens are now outside your {retentionLabel(retentionDays)} retention window, so they no longer appear in Overview or History.</p>
              {:else}<p class="credential-safety">Allstarr keeps these listens private and does not send them to Last.fm or ListenBrainz.</p>{/if}
            {/if}
            <footer>
              {#if result.state === "previewed"}<Button disabled={Boolean(action) || (result.preview?.newRows ?? 0) === 0} onclick={() => void changeImport(item, "apply")}>{action === `apply:${item.id}` ? "Starting…" : (result.preview?.newRows ?? 0) === 0 ? "Nothing inside retention" : "Add to my history"}</Button>{/if}
              {#if activeImport}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void changeImport(item, "cancel")}>{action === `cancel:${item.id}` ? "Cancelling…" : "Cancel import"}</Button>{/if}
              {#if result.state === "failed" || result.state === "cancelled"}<Button disabled={Boolean(action)} onclick={() => void changeImport(item, "resume")}>{action === `resume:${item.id}` ? "Resuming…" : "Resume import"}</Button>{/if}
              {#if !activeImport}<Button variant="destructive" disabled={Boolean(action)} onclick={() => confirmRemoveImport(item)}>{result.state === "completed" && (result.importedRows ?? 0) > 0 ? "Undo import" : result.state === "previewed" ? "Discard preview" : "Remove import"}</Button>{/if}
            </footer>
            {#if result.lastErrorMessage}<p class="notice-error" role="alert">{result.lastErrorMessage}</p>{/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>{/if}
</section>

<Dialog.Root bind:open={detailOpen}>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog history-detail-dialog">
      <header><div><p class="eyebrow">Listening history</p><Dialog.Title>Edit listen</Dialog.Title><Dialog.Description>Correct the public song details or remove this listen from your history.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close listen details"><X size={18} aria-hidden="true" /></Dialog.Close></header>
      {#if detailError}<p class="notice-error" role="alert">{detailError}</p>{/if}
      {#if !detail}<div class="detail-loading" role="status" aria-busy="true">Loading listen…</div>{:else}
        <form class="history-detail-form" onsubmit={saveDetail}>
          <label class="field"><span>Song</span><input bind:value={editTitle} maxlength="500" required /></label>
          <label class="field"><span>Artist</span><input bind:value={editArtist} maxlength="500" required /></label>
          <label class="field"><span>Album</span><input bind:value={editAlbum} maxlength="500" /></label>
          <label class="field"><span>Album artist</span><input bind:value={editAlbumArtist} maxlength="500" /></label>
          <dl><div><dt>Listened</dt><dd>{detail.item.listenedAt ? new Date(detail.item.listenedAt).toLocaleString() : "Unknown"}</dd></div><div><dt>Duration</dt><dd>{formatDuration(detail.item.durationMilliseconds)}</dd></div><div><dt>Source</dt><dd>{sourceLabel(detail.provenance.source)}</dd></div><div><dt>Client</dt><dd>{clientLabel(detail.provenance.client)}</dd></div><div><dt>Imported</dt><dd>{detail.provenance.imported ? "Yes" : "No"}</dd></div><div><dt>MusicBrainz details</dt><dd>{musicBrainzState(detail.item.enrichmentState)}{detail.identity.musicBrainzEnrichmentConfidence != null ? ` · ${Math.round(detail.identity.musicBrainzEnrichmentConfidence * 100)}%` : ""}</dd></div></dl>
          {#if detail.item.targetStatuses.length}<section class="target-statuses"><strong>Listening services</strong><ul>{#each detail.item.targetStatuses as status}<li><span>{words(status.target)} · {words(status.state)}</span>{#if status.message}<small>{status.message}</small>{/if}</li>{/each}</ul></section>{/if}
          <footer><Button variant="destructive" onclick={() => deleteOpen = true}>Delete listen</Button><span><Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close><Button type="submit" disabled={Boolean(action)}>{action === "save-detail" ? "Saving…" : "Save changes"}</Button></span></footer>
        </form>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<ConfirmDialog bind:open={deleteOpen} title="Delete this listen?" description="This removes the listen and its delivery status from this library. This cannot be undone." confirmLabel={action === "delete-detail" ? "Deleting…" : "Delete listen"} cancelLabel="Keep listen" disabled={Boolean(action)} onConfirm={removeDetail} />
<ConfirmDialog
  bind:open={importDeleteOpen}
  title={importDeleteItem?.result?.state === "completed" ? "Undo this history import?" : "Remove this history import?"}
  description={(importDeleteItem?.result?.importedRows ?? 0) > 0
    ? `This removes any listens still stored from ${importDeleteItem?.fileName ?? "this file"} (${(importDeleteItem?.result?.importedRows ?? 0).toLocaleString()} were added), its saved import record, and any temporary upload. Other history is not affected.`
    : `This removes ${importDeleteItem?.fileName ?? "this file"} from saved imports and deletes any temporary upload. No listening history will be removed.`}
  confirmLabel={action.startsWith("remove:") ? "Removing…" : (importDeleteItem?.result?.importedRows ?? 0) > 0 ? "Undo import" : "Remove import"}
  cancelLabel="Keep import"
  disabled={Boolean(action)}
  onConfirm={removeSavedImport}
/>

<style>
  .history-workspace{min-width:0}
  .history-workspace{display:grid;gap:1rem}.history-heading,.history-list-card>header,.activity-card>header,.top-card>header,.import-card>header{display:flex;align-items:start;justify-content:space-between;gap:1rem}.history-heading h3,.history-list-card h3,.activity-card h3,.top-card h3,.import-card h3{margin:.2rem 0}.history-heading p:last-child,.import-card>header p:last-child{max-width:70ch;margin:0;color:var(--color-ink-muted)}.history-toolbar{display:grid;grid-template-columns:minmax(16rem,1fr) auto auto;align-items:end;gap:.75rem;padding:1rem}.history-toolbar :global(.period-tabs){width:22rem}.history-toolbar details{grid-column:1/-1}.history-toolbar summary{cursor:pointer}.advanced-filters{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:.75rem;margin-top:.75rem}.history-stats{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:.75rem}.history-stats article{display:grid;gap:.2rem;padding:1rem}.history-stats strong{font-family:var(--font-display);font-size:1.35rem}.history-stats span{color:var(--color-ink-muted);font-size:.78rem}.now-playing{display:flex;align-items:center;gap:.8rem;width:100%;padding:1rem;text-align:left}.now-playing>span:first-child{display:grid;place-items:center;width:2.5rem;height:2.5rem;border-radius:50%;background:var(--color-signal);color:var(--color-on-signal)}.now-playing small,.now-playing strong{display:block}.recap-card{display:grid;gap:.65rem;padding:1.15rem}.recap-card h3,.recap-card p{margin:0}.history-insights{display:grid;grid-template-columns:1fr 1fr;gap:1rem}.activity-card,.top-card,.history-list-card,.import-card{padding:1.15rem}.activity-range{margin:.75rem 0 0;color:var(--color-ink-muted);font-size:.78rem}.activity-range-mobile{display:none}.activity-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(13px,1fr));gap:.3rem;margin-top:.5rem}.activity-grid span{aspect-ratio:1;border-radius:3px;background:color-mix(in srgb,var(--color-signal) calc(var(--activity) * 100%),var(--color-panel-raised))}.monthly-activity{display:grid;margin:.75rem 0 0;padding:0;list-style:none}.monthly-activity li{display:grid;grid-template-columns:minmax(7rem,.5fr) minmax(8rem,1fr) auto;align-items:center;gap:.75rem;border-top:1px solid var(--color-edge);padding:.65rem 0}.monthly-activity strong,.monthly-activity small{display:block}.monthly-activity small,.monthly-activity li>span:last-child{color:var(--color-ink-muted)}.monthly-activity meter{width:100%;accent-color:var(--color-signal)}.top-card ol,.breakdown-card ol{margin:.75rem 0 0;padding:0;list-style:none}.top-card li,.breakdown-card li{display:flex;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding:.65rem 0}.top-card li span:first-child strong,.top-card li span:first-child small,.breakdown-card li span:first-child strong,.breakdown-card li span:first-child small{display:block}.top-card li small,.top-card li>span:last-child,.breakdown-card li small,.breakdown-card li>span:last-child{color:var(--color-ink-muted)}.history-breakdowns{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:1rem}.breakdown-card{padding:1.15rem}.breakdown-card h3{margin:.2rem 0}.history-list{display:grid;margin:.6rem 0 0;padding:0;list-style:none}.history-list>li>button{display:grid;grid-template-columns:auto minmax(0,1fr) minmax(10rem,.45fr) auto;align-items:center;gap:.85rem;width:100%;border-top:1px solid var(--color-edge);padding:.8rem 0;text-align:left}.history-copy strong,.history-copy small,.history-route strong,.history-route small{display:block}.history-copy small,.history-route small{color:var(--color-ink-muted)}.history-route{text-align:right}:global(.load-more){display:block;margin:1rem auto 0}.import-card{display:grid;gap:1.25rem}.import-readiness,.import-batch-actions{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:.8rem;border:1px solid var(--color-edge);border-radius:var(--radius-md);background:var(--color-panel-raised);padding:.8rem}.import-readiness.warning{border-color:color-mix(in srgb,var(--color-warning) 40%,var(--color-edge))}.import-readiness>span:nth-child(2),.import-batch-actions>span{display:grid;gap:.15rem}.import-readiness small,.import-batch-actions small{color:var(--color-ink-muted)}.import-picker{display:grid;gap:.75rem}.upload-zone{position:relative;display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:1rem;min-height:7rem;border:1px dashed color-mix(in srgb,var(--color-signal) 55%,var(--color-edge));border-radius:var(--radius-lg);background:color-mix(in srgb,var(--color-signal) 4%,var(--color-panel-raised));padding:1rem;cursor:pointer}.upload-zone:hover,.upload-zone.dragging{border-color:var(--color-signal);background:color-mix(in srgb,var(--color-signal) 9%,var(--color-panel-raised))}.upload-zone:focus-within{outline:2px solid var(--focus-ring);outline-offset:2px}.upload-zone input{position:absolute;inset:0;width:100%;height:100%;opacity:0;cursor:pointer}.upload-symbol,.file-marker{display:grid;place-items:center;border-radius:var(--radius-md);background:color-mix(in srgb,var(--color-signal) 12%,transparent);color:var(--color-signal-text)}.upload-symbol{width:3rem;height:3rem}.upload-copy,.file-copy{display:grid;min-width:0;gap:.2rem}.upload-copy strong{font-family:var(--font-display);font-size:1rem}.upload-copy small,.file-copy small,.import-result header small{color:var(--color-ink-muted)}.upload-browse{border:1px solid var(--color-edge);border-radius:var(--radius-md);background:var(--color-panel-raised);padding:.65rem .9rem;font-size:var(--text-sm);font-weight:700}.upload-queue,.import-results{display:grid;margin:0;padding:0;list-style:none}.upload-queue{gap:.4rem}.upload-queue li{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:.75rem;min-height:3.5rem;border-radius:var(--radius-md);background:var(--color-panel-raised);padding:.55rem .65rem}.upload-queue li.error{background:rgb(255 107 122 / 8%)}.file-marker{width:2rem;height:2rem}.file-copy strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.upload-queue .icon-button{width:var(--control-sm);height:var(--control-sm)}.import-batch-summary{display:grid;grid-template-columns:repeat(4,1fr);margin:0;border:1px solid var(--color-edge);border-radius:var(--radius-md)}.import-batch-summary div{display:flex;flex-direction:column-reverse;padding:.75rem}.import-batch-summary div+div{border-left:1px solid var(--color-edge)}.import-batch-summary dd{margin:0;font-family:var(--font-display);font-size:1.2rem;font-weight:750}.import-batch-summary dt{color:var(--color-ink-muted);font-size:.76rem}.import-results{gap:1.25rem}.import-result{display:grid;gap:.85rem;border-top:1px solid var(--color-edge);padding-top:1.25rem}.import-result>header{display:flex;align-items:start;justify-content:space-between;gap:1rem}.import-result-select{display:flex;align-items:start;gap:.65rem}.import-result-select span>*{display:block}.import-preview{display:grid;grid-template-columns:repeat(4,1fr);margin:0;border-block:1px solid var(--color-edge)}.import-preview div{display:flex;flex-direction:column-reverse;gap:.15rem;padding:.75rem}.import-preview div+div{border-left:1px solid var(--color-edge)}.import-preview dd{margin:0;font-family:var(--font-display);font-size:1.2rem;font-weight:750}.import-preview dt{color:var(--color-ink-muted);font-size:.76rem}.credential-safety{margin:0;color:var(--color-ink-muted);font-size:var(--text-sm)}.import-result>footer{display:flex;justify-content:flex-end;gap:.5rem}
  :global(.history-detail-dialog){display:grid;grid-template-rows:auto minmax(0,1fr);width:min(44rem,calc(100vw - 2rem));max-height:min(48rem,calc(100dvh - 2rem));overflow:hidden}.history-detail-form{display:grid;grid-template-columns:1fr 1fr;gap:1rem;overflow:auto;padding:1rem}.history-detail-form dl,.target-statuses,.history-detail-form footer{grid-column:1/-1}.history-detail-form dl{display:grid;grid-template-columns:repeat(3,1fr);gap:.75rem;margin:0}.history-detail-form dl div{border:1px solid var(--color-edge);border-radius:var(--radius-md);padding:.7rem}.history-detail-form dt{color:var(--color-ink-muted);font-size:.72rem}.history-detail-form dd{margin:.2rem 0 0}.target-statuses ul{margin:0;padding:0;list-style:none}.target-statuses li{display:flex;justify-content:space-between;gap:1rem}.target-statuses small{color:var(--color-ink-muted)}.history-detail-form footer{display:flex;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding-top:1rem}.history-detail-form footer>span{display:flex;gap:.5rem}
  @media(max-width:900px){.history-toolbar{grid-template-columns:1fr auto}.history-toolbar>:global(nav[aria-label="History period"]){grid-column:1/-1;grid-row:2}.history-stats{grid-template-columns:repeat(3,1fr)}.history-breakdowns{grid-template-columns:1fr 1fr}.advanced-filters{grid-template-columns:1fr 1fr}.history-list>li>button{grid-template-columns:auto minmax(0,1fr) auto}.history-route{grid-column:2;text-align:left}.import-preview{grid-template-columns:1fr 1fr}}
  @media(max-width:620px){.history-heading{flex-direction:column}.history-actions,.history-actions>:global([data-slot="button"]){width:100%}.history-toolbar{grid-template-columns:1fr}.history-toolbar>:global(nav[aria-label="History period"]){grid-column:auto;grid-row:auto}.history-toolbar :global(.period-tabs){width:100%}.history-toolbar details{grid-column:auto}.advanced-filters,.history-insights,.history-breakdowns,.history-detail-form,.history-detail-form dl{grid-template-columns:1fr}.history-stats{grid-template-columns:1fr 1fr}.history-stats article:last-child{grid-column:1/-1}.history-list>li>button{grid-template-columns:auto minmax(0,1fr)}.history-list>li>button>:global(.badge){grid-column:2;justify-self:start}.history-route{grid-column:2}.activity-card>header,.top-card>header,.history-detail-form footer{align-items:stretch;flex-direction:column}.activity-range-wide,.activity-grid span:nth-last-child(n+31){display:none}.activity-range-mobile{display:inline}.top-card>header{display:grid}.monthly-activity li{grid-template-columns:minmax(0,1fr) auto}.monthly-activity meter{grid-column:1/-1;grid-row:2}.import-card{gap:1rem;padding:1rem}.upload-zone{grid-template-columns:auto minmax(0,1fr);min-height:6rem}.import-readiness,.import-batch-actions{grid-template-columns:auto minmax(0,1fr)}.import-readiness>:global([data-slot="button"]),.import-batch-actions>:global([data-slot="button"]),.upload-browse{grid-column:1/-1;width:100%;text-align:center}.import-batch-summary{grid-template-columns:1fr}.import-batch-summary div+div{border-top:1px solid var(--color-edge);border-left:0}.import-result>footer,.import-result>footer>:global([data-slot="button"]){width:100%}.import-preview{grid-template-columns:1fr 1fr}.import-preview div:nth-child(3){border-left:0}.import-preview div:nth-child(n+3){border-top:1px solid var(--color-edge)}.import-result>header{align-items:start}.import-result>footer{display:grid}.history-detail-form dl,.target-statuses,.history-detail-form footer{grid-column:auto}.history-detail-form footer>span{display:grid}.history-detail-form footer :global(button){width:100%}}

  .history-list-card{overflow:hidden;padding:0}
  .history-list-card>header{align-items:center;padding:1rem 1.1rem .8rem}
  .history-list-card>header h3{margin:0}
  .history-list-card>header p{margin:.2rem 0 0;color:var(--color-ink-muted);font-size:var(--text-sm)}
  .history-count{flex:none;border-radius:999px;background:var(--color-panel-raised);padding:.3rem .6rem;color:var(--color-ink-muted);font-size:var(--text-xs);font-weight:700}
  .history-column-head{display:grid;grid-template-columns:2.5rem minmax(0,1fr) minmax(7rem,.32fr) minmax(9rem,.4fr);align-items:center;gap:.75rem}
  .history-list>li>button{display:grid;grid-template-columns:2.5rem minmax(0,1fr) minmax(17rem,.72fr);align-items:center;gap:.75rem}
  .history-column-head{border-block:1px solid var(--color-edge);background:var(--color-panel-raised);padding:.42rem 1.1rem;color:var(--color-ink-muted);font-size:.7rem;font-weight:700}
  .history-list{margin:0}
  .history-list>li>button{min-height:3.75rem;border-top:1px solid var(--color-edge);padding:.5rem 1.1rem}
  .history-list>li:first-child>button{border-top:0}
  .history-list>li>button:hover{background:color-mix(in srgb,var(--color-signal) 5%,transparent)}
  .history-list :global(.track-art){width:2.5rem;height:2.5rem}
  .history-copy,.history-meta,.history-route,.history-time{min-width:0}
  .history-meta{display:grid;grid-template-columns:minmax(7rem,.8fr) minmax(9rem,1fr);gap:.75rem}
  .history-copy strong,.history-copy small,.history-route strong,.history-route small,.history-time time,.history-time small{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .history-copy strong{font-size:var(--text-sm)}
  .history-copy small,.history-route small,.history-time small{color:var(--color-ink-muted);font-size:var(--text-xs)}
  .history-route,.history-time{grid-column:auto;text-align:left}
  .history-route strong,.history-time time{font-size:var(--text-sm);font-weight:700}

  @media(max-width:760px){
    .history-column-head{display:none}
    .history-list-card>header{border-bottom:1px solid var(--color-edge)}
    .history-list>li>button{grid-template-columns:2.5rem minmax(0,1fr);align-items:start;min-height:0;column-gap:.75rem;row-gap:.2rem;padding-block:.55rem}
    .history-list :global(.track-art){grid-row:1/3}
    .history-meta{grid-column:2;display:flex;flex-wrap:wrap;justify-content:space-between;gap:.2rem .75rem}
    .history-route,.history-time{grid-column:auto;display:flex;align-items:baseline;gap:.35rem}
    .history-route strong,.history-route small,.history-time time,.history-time small{display:inline}
  }
  @media(max-width:420px){
    .history-list-card>header{align-items:flex-start}
    .history-list-card>header p{display:none}
  }
</style>
