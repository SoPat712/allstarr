<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { X } from "lucide-svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
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

  let { scope, section }: { scope: IntelligenceScope; section: HistorySection } = $props();

  let period = $state("30");
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
  let importFile = $state<File | null>(null);
  let historyImport = $state<ListeningHistoryImport | null>(null);
  let importError = $state("");
  let loadedKey = "";
  let previousScopeKey = "";

  const scopeKey = $derived(`${scope.protocol}\0${scope.backendInstanceId}\0${scope.libraryScopeId}`);
  const selectedTop = $derived(top[topKind]);
  const topTrack = $derived(top.track[0]);
  const periodName = $derived(period === "365" ? "Last year" : period === "custom" ? "Selected dates" : `Last ${period} days`);
  const exportUrl = $derived(intelligence.historyExportUrl(scope));
  const activeImport = $derived(historyImport?.state === "pending" || historyImport?.state === "running");
  const hasHistoryFilters = $derived(Boolean(search.trim() || source.trim() || client.trim() || artist.trim() || album.trim() || track.trim()));

  $effect(() => {
    if (scopeKey !== previousScopeKey) {
      previousScopeKey = scopeKey;
      detailOpen = false;
      detail = null;
      historyImport = null;
      importFile = null;
    }
    const key = `${scopeKey}\0${period}\0${fromDate}\0${toDate}\0${timeZoneId}`;
    if (key === loadedKey) return;
    loadedKey = key;
    void loadAll();
  });

  $effect(() => {
    if (!activeImport || !historyImport) return;
    const id = historyImport.importId;
    const timer = setTimeout(() => void refreshImport(id), 1500);
    return () => clearTimeout(timer);
  });

  function isoDate(value: Date) {
    return value.toISOString().slice(0, 10);
  }

  function bounds() {
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

  async function previewImport(event: SubmitEvent) {
    event.preventDefault();
    if (!importFile) return;
    action = "preview-import";
    importError = "";
    try {
      historyImport = await intelligence.previewHistoryImport(scope, importFile);
    } catch (cause) {
      importError = cause instanceof Error ? cause.message : "This history file could not be read.";
    } finally {
      action = "";
    }
  }

  async function changeImport(operation: "apply" | "resume" | "cancel") {
    if (!historyImport) return;
    action = operation;
    importError = "";
    try {
      historyImport = await intelligence.changeHistoryImport(scope, historyImport, operation);
    } catch (cause) {
      importError = cause instanceof Error ? cause.message : "The history import could not be changed.";
    } finally {
      action = "";
    }
  }

  async function refreshImport(id: string) {
    try {
      const next = await intelligence.historyImport(scope, id);
      const completed = next.state === "completed" && historyImport?.state !== "completed";
      historyImport = next;
      if (completed) await loadAll();
    } catch (cause) {
      importError = cause instanceof Error ? cause.message : "Import progress could not be loaded.";
    }
  }

  function listeningTime(milliseconds: number) {
    const minutes = Math.round(milliseconds / 60_000);
    return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} hr ${minutes % 60} min`;
  }

  function words(value?: string | null) {
    return value ? value.replaceAll("_", " ").replaceAll("-", " ") : "Unknown";
  }
</script>

<section class="history-workspace">
  {#if section !== "imports"}
    <header class="history-heading">
      <div><p class="eyebrow">Your listening</p><h3>{section === "overview" ? "Listening overview" : "Listening history"}</h3><p>Private activity for this account and library only.</p></div>
      {#if section === "history"}<div class="history-actions"><a class="button-secondary" href={exportUrl} download>Download my history</a></div>{/if}
    </header>

    <form class="history-toolbar panel" onsubmit={(event) => { event.preventDefault(); void (section === "overview" ? loadAll() : loadHistory()); }}>
      {#if section === "history"}<SearchField bind:value={search} label="Search listening history" placeholder="Search songs, artists, or albums" />{/if}
      <SelectField bind:value={period} label="History period" options={[
        { value: "30", label: "Last 30 days" }, { value: "90", label: "Last 90 days" },
        { value: "365", label: "Last year" }, { value: "custom", label: "Custom dates" },
      ]} />
      <button class="button-primary" type="submit" disabled={loading || historyLoading}>{loading || historyLoading ? "Updating…" : section === "overview" ? "Update overview" : "Apply filters"}</button>
      <details>
        <summary>{section === "overview" ? "Dates and time zone" : "More filters"}</summary>
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
      <div class="history-stats" role="status" aria-busy="true" aria-label="Loading listening history">{#each Array(5) as _}<div class="panel skeleton-panel"></div>{/each}</div>
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
        <header><div><p class="eyebrow">Activity</p><h3>Listening days</h3></div><span>{activity?.buckets.length ?? 0} active days</span></header>
        {#if activity?.buckets.length}
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
        {:else}<p class="muted">No activity in this period. Play music or import a history file to begin.</p>{/if}
      </section>

      <section class="panel top-card">
        <header><div><p class="eyebrow">Most played</p><h3>Top {topKind === "track" ? "songs" : `${topKind}s`}</h3></div><SegmentedNav items={[{ id: "artist", label: "Artists" }, { id: "album", label: "Albums" }, { id: "track", label: "Songs" }]} active={topKind} label="Top listening category" onchange={(value) => topKind = value as typeof topKind} /></header>
        <ol>{#each selectedTop.slice(0, 5) as item}<li><span><strong>{topKind === "artist" ? item.artist : topKind === "album" ? item.album : item.title}</strong>{#if topKind !== "artist"}<small>{item.artist}</small>{/if}</span><span>{item.listenCount} listens</span></li>{:else}<li class="muted">Nothing to rank yet. Play music or import a history file to begin.</li>{/each}</ol>
      </section>
    </div>
  {/if}

  {#if (section === "overview" || section === "history") && !loading}
    {@const shownItems = section === "overview" ? items.slice(0, 5) : items}
    <section class="panel history-list-card">
      <header><div><p class="eyebrow">Recent listens</p><h3>{section === "overview" ? "Recently played" : "History"}</h3></div><span>{shownItems.length} shown</span></header>
      <ul class="history-list">
        {#each shownItems as item}
          <li><button type="button" onclick={() => void openDetail(item)}>
            <span class="track-art">{#if item.artworkUrl}<img src={item.artworkUrl} alt="" loading="lazy" />{:else}<span aria-hidden="true">♪</span>{/if}</span>
            <span class="history-copy"><strong>{item.title ?? "Unknown song"}</strong><small>{item.artist ?? "Unknown artist"}{item.album ? ` · ${item.album}` : ""}</small><small>{item.listenedAt ? new Date(item.listenedAt).toLocaleString() : "Time unavailable"} · {formatDuration(item.durationMilliseconds)}</small></span>
            <span class="history-route"><strong>{item.provider ?? words(item.source)}</strong><small>{item.client ?? "Unknown client"}</small>{#if item.targetStatuses.length}<small>{item.targetStatuses.map((status) => `${words(status.target)}: ${words(status.state)}`).join(" · ")}</small>{/if}</span>
            <span class={`status-pill ${item.enrichmentState === "resolved" ? "healthy" : "suggested"}`}>{words(item.enrichmentState)}</span>
          </button></li>
        {:else}
          <li>{#if section === "history" && hasHistoryFilters}<div class="compact-empty"><strong>No listens match these filters</strong><p>Try a wider period or clear a filter.</p></div>
          {:else}<div class="compact-empty"><strong>No completed listens yet</strong><p>Turn on automatic history in Settings, then play music or import a history file.</p></div>{/if}</li>
        {/each}
      </ul>
      {#if section === "history" && nextCursor}<button class="button-secondary load-more" type="button" disabled={historyLoading} onclick={() => void loadHistory(false)}>{historyLoading ? "Loading…" : "Load older listens"}</button>{/if}
    </section>
  {/if}

  {#if section === "imports"}<section class="panel import-card">
    <header><div><p class="eyebrow">Bring your history</p><h3>Import listening history</h3><p>Preview a Spotify, Last.fm, ListenBrainz, Koito, or Maloja export before adding anything. Files stay private and expire automatically.</p></div></header>
    {#if importError}<p class="notice-error" role="alert">{importError}</p>{/if}
    <form onsubmit={previewImport}>
      <label class="field"><span>History export file</span><input type="file" accept="application/json,text/plain,application/zip,.json,.jsonl,.zip" onchange={(event) => importFile = event.currentTarget.files?.[0] ?? null} required /></label>
      <button class="button-secondary" type="submit" disabled={!importFile || Boolean(action)}>{action === "preview-import" ? "Reading file…" : "Preview import"}</button>
    </form>
    {#if historyImport?.preview}
      <div class="import-preview">
        <div><strong>{historyImport.preview.newRows.toLocaleString()}</strong><span>new listens</span></div><div><strong>{(historyImport.preview.duplicateExisting + historyImport.preview.duplicateInFile).toLocaleString()}</strong><span>already present</span></div><div><strong>{historyImport.preview.skipped.toLocaleString()}</strong><span>skipped</span></div><div><strong>{historyImport.preview.estimatedMusicBrainzLookups.toLocaleString()}</strong><span>songs to identify</span></div>
      </div>
      <p class="credential-safety">Allstarr will add these past listens to your private history. It will not send them to Last.fm or ListenBrainz.</p>
    {/if}
    {#if historyImport}
      <div class="import-status"><span role="status" aria-atomic="true"><strong>{historyImport.displayFileName ?? "History import"}</strong><small>{words(historyImport.state)}{historyImport.importedRows !== undefined ? ` · ${historyImport.importedRows.toLocaleString()} added` : ""}</small></span><div>
        {#if historyImport.state === "previewed"}<button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void changeImport("apply")}>{action === "apply" ? "Starting…" : "Add to my history"}</button>{/if}
        {#if activeImport}<button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void changeImport("cancel")}>{action === "cancel" ? "Cancelling…" : "Cancel import"}</button>{/if}
        {#if historyImport.state === "failed" || historyImport.state === "cancelled"}<button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void changeImport("resume")}>{action === "resume" ? "Resuming…" : "Resume import"}</button>{/if}
      </div></div>
      {#if historyImport.lastErrorMessage}<p class="notice-error" role="alert">{historyImport.lastErrorMessage}</p>{/if}
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
          <dl><div><dt>Listened</dt><dd>{detail.item.listenedAt ? new Date(detail.item.listenedAt).toLocaleString() : "Unknown"}</dd></div><div><dt>Duration</dt><dd>{formatDuration(detail.item.durationMilliseconds)}</dd></div><div><dt>Source</dt><dd>{words(detail.provenance.source)}</dd></div><div><dt>Client</dt><dd>{detail.provenance.client ?? "Unknown"}</dd></div><div><dt>Imported</dt><dd>{detail.provenance.imported ? "Yes" : "No"}</dd></div><div><dt>MusicBrainz</dt><dd>{words(detail.item.enrichmentState)}{detail.identity.musicBrainzEnrichmentConfidence != null ? ` · ${Math.round(detail.identity.musicBrainzEnrichmentConfidence * 100)}%` : ""}</dd></div></dl>
          {#if detail.item.targetStatuses.length}<section class="target-statuses"><strong>Listening services</strong><ul>{#each detail.item.targetStatuses as status}<li><span>{words(status.target)} · {words(status.state)}</span>{#if status.message}<small>{status.message}</small>{/if}</li>{/each}</ul></section>{/if}
          <footer><button class="button-danger" type="button" onclick={() => deleteOpen = true}>Delete listen</button><span><Dialog.Close class="button-secondary">Cancel</Dialog.Close><button class="button-primary" type="submit" disabled={Boolean(action)}>{action === "save-detail" ? "Saving…" : "Save changes"}</button></span></footer>
        </form>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<ConfirmDialog bind:open={deleteOpen} title="Delete this listen?" description="This removes the listen and its delivery status from this library. This cannot be undone." confirmLabel={action === "delete-detail" ? "Deleting…" : "Delete listen"} cancelLabel="Keep listen" disabled={Boolean(action)} onConfirm={removeDetail} />

<style>
  .history-workspace{min-width:0}.import-card input[type="file"]{width:100%;min-width:0;max-width:100%}
  .history-workspace{display:grid;gap:1rem}.history-heading,.history-list-card>header,.activity-card>header,.top-card>header,.import-card>header{display:flex;align-items:start;justify-content:space-between;gap:1rem}.history-heading h3,.history-list-card h3,.activity-card h3,.top-card h3,.import-card h3{margin:.2rem 0}.history-heading p:last-child,.import-card header p:last-child{margin:0;color:var(--color-ink-muted)}.history-toolbar{display:grid;grid-template-columns:minmax(16rem,1fr) minmax(11rem,.35fr) auto;align-items:end;gap:.75rem;padding:1rem}.history-toolbar details{grid-column:1/-1}.history-toolbar summary{cursor:pointer}.advanced-filters{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:.75rem;margin-top:.75rem}.history-stats{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:.75rem}.history-stats article{display:grid;gap:.2rem;padding:1rem}.history-stats strong{font-family:var(--font-display);font-size:1.35rem}.history-stats span{color:var(--color-ink-muted);font-size:.78rem}.now-playing{display:flex;align-items:center;gap:.8rem;width:100%;padding:1rem;text-align:left}.now-playing>span:first-child{display:grid;place-items:center;width:2.5rem;height:2.5rem;border-radius:50%;background:var(--color-signal);color:var(--color-canvas)}.now-playing small,.now-playing strong{display:block}.recap-card{display:grid;gap:.65rem;padding:1.15rem}.recap-card h3,.recap-card p{margin:0}.history-insights{display:grid;grid-template-columns:1fr 1fr;gap:1rem}.activity-card,.top-card,.history-list-card,.import-card{padding:1.15rem}.activity-range{margin:.75rem 0 0;color:var(--color-ink-muted);font-size:.78rem}.activity-range-mobile{display:none}.activity-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(13px,1fr));gap:.3rem;margin-top:.5rem}.activity-grid span{aspect-ratio:1;border-radius:3px;background:color-mix(in srgb,var(--color-signal) calc(var(--activity) * 100%),var(--color-panel-raised))}.top-card ol{margin:.75rem 0 0;padding:0;list-style:none}.top-card li{display:flex;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding:.65rem 0}.top-card li span:first-child strong,.top-card li span:first-child small{display:block}.top-card li small,.top-card li>span:last-child{color:var(--color-ink-muted)}.history-list{display:grid;margin:.6rem 0 0;padding:0;list-style:none}.history-list>li>button{display:grid;grid-template-columns:auto minmax(0,1fr) minmax(10rem,.45fr) auto;align-items:center;gap:.85rem;width:100%;border-top:1px solid var(--color-edge);padding:.8rem 0;text-align:left}.history-copy strong,.history-copy small,.history-route strong,.history-route small{display:block}.history-copy small,.history-route small{color:var(--color-ink-muted)}.history-route{text-align:right}.load-more{display:block;margin:1rem auto 0}.import-card{display:grid;gap:1rem}.import-card>form{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:end;gap:.75rem}.import-preview{display:grid;grid-template-columns:repeat(4,1fr);gap:.75rem}.import-preview div{display:grid;gap:.15rem;border:1px solid var(--color-edge);border-radius:var(--radius-md);padding:.75rem}.import-preview strong{font-size:1.2rem}.import-preview span{color:var(--color-ink-muted);font-size:.76rem}.import-status{display:flex;align-items:center;justify-content:space-between;gap:1rem}.import-status span>*{display:block}.import-status small{color:var(--color-ink-muted)}.import-status>div{display:flex;gap:.5rem}.history-detail-dialog{display:grid;grid-template-rows:auto minmax(0,1fr);width:min(44rem,calc(100vw - 2rem));max-height:min(48rem,calc(100dvh - 2rem));overflow:hidden}.history-detail-form{display:grid;grid-template-columns:1fr 1fr;gap:1rem;overflow:auto;padding:1rem}.history-detail-form dl,.target-statuses,.history-detail-form footer{grid-column:1/-1}.history-detail-form dl{display:grid;grid-template-columns:repeat(3,1fr);gap:.75rem;margin:0}.history-detail-form dl div{border:1px solid var(--color-edge);border-radius:var(--radius-md);padding:.7rem}.history-detail-form dt{color:var(--color-ink-muted);font-size:.72rem}.history-detail-form dd{margin:.2rem 0 0}.target-statuses ul{margin:0;padding:0;list-style:none}.target-statuses li{display:flex;justify-content:space-between;gap:1rem}.target-statuses small{color:var(--color-ink-muted)}.history-detail-form footer{display:flex;justify-content:space-between;gap:1rem;border-top:1px solid var(--color-edge);padding-top:1rem}.history-detail-form footer>span{display:flex;gap:.5rem}
  @media(max-width:900px){.history-stats{grid-template-columns:repeat(3,1fr)}.advanced-filters{grid-template-columns:1fr 1fr}.history-list>li>button{grid-template-columns:auto minmax(0,1fr) auto}.history-route{grid-column:2;text-align:left}.import-preview{grid-template-columns:1fr 1fr}}
  @media(max-width:620px){.history-heading{flex-direction:column}.history-actions,.history-actions>*{width:100%}.history-toolbar{grid-template-columns:1fr}.history-toolbar details{grid-column:auto}.advanced-filters,.history-insights,.import-card>form,.import-preview,.history-detail-form,.history-detail-form dl{grid-template-columns:1fr}.history-stats{grid-template-columns:1fr 1fr}.history-stats article:last-child{grid-column:1/-1}.history-list>li>button{grid-template-columns:auto minmax(0,1fr)}.history-list>li>button>.status-pill{grid-column:2;justify-self:start}.history-route{grid-column:2}.activity-card>header,.top-card>header,.import-status,.history-detail-form footer{align-items:stretch;flex-direction:column}.activity-range-wide,.activity-grid span:nth-last-child(n+31){display:none}.activity-range-mobile{display:inline}.top-card>header{display:grid}.import-status>div,.import-status>div>*{width:100%}.history-detail-form dl,.target-statuses,.history-detail-form footer{grid-column:auto}.history-detail-form footer>span{display:grid}.history-detail-form footer button{width:100%}}
</style>
