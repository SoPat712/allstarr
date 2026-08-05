<script lang="ts">
  import { onMount } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Popover } from "$lib/components/ui/popover";
  import { ArrowRight, ChevronDown, MoreHorizontal, X } from "lucide-svelte";
  import AddPlaylistDialog from "$lib/components/AddPlaylistDialog.svelte";
  import CoverageBar from "$lib/components/CoverageBar.svelte";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import OperationConsole from "$lib/components/OperationConsole.svelte";
  import PlaylistSettingsDialog from "$lib/components/PlaylistSettingsDialog.svelte";
  import PlaylistRematchDialog from "$lib/components/PlaylistRematchDialog.svelte";
  import PlaylistSourceUpdateDialog from "$lib/components/PlaylistSourceUpdateDialog.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import {
    home,
    matchReview,
    playlistLinks,
    type MatchReviewItem,
    type PlaylistDetails,
    type PlaylistLink,
    type PlaylistTrack,
    type ProviderDefinition,
  } from "$lib/api";
  import { humanize } from "$lib/activity";
  import {
    confirmationCoverage,
    filterPlaylists,
    filterTracks,
    formatDuration,
    isReviewTrack,
    playlistOutcomeLabel,
    playlistProjectionOptions,
    providerColor,
    runBounded,
    scheduleCadence,
    type PlaylistSort,
    type TrackRouteFilter,
    type TrackSort,
  } from "$lib/playlists";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let { initialId = "" }: { initialId?: string } = $props();

  let playlists = $state<PlaylistLink[]>([]);
  let providers = $state<ProviderDefinition[]>([]);
  let details = $state<PlaylistDetails | null>(null);
  let detailOpen = $state(false);
  let selectedId = $state("");
  let query = $state("");
  let stateFilter = $state<"all" | "ready" | "attention" | "paused">("all");
  let sort = $state<PlaylistSort>("name");
  let trackQuery = $state("");
  let routeFilter = $state<TrackRouteFilter>("all");
  let trackSort = $state<TrackSort>("position");
  let loading = $state(true);
  let detailLoading = $state(false);
  let detailError = $state("");
  let refreshing = $state(false);
  let error = $state("");
  let degraded = $state("");
  let feedback = $state("");
  let action = $state("");
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let refreshQueued = false;
  let detailRequest = 0;
  let detailWasOpen = false;
  let detailReturnFocus: HTMLElement | null = null;
  let matchWasOpen = false;
  let matchReturnFocus: HTMLElement | null = null;
  let page = $state(1);
  let addOpen = $state(false);
  let operationJobId = $state("");
  let bulkProgress = $state("");
  let matchOpen = $state(false);
  let selectedMatch = $state<MatchReviewItem | null>(null);
  let matchLoading = $state("");
  const trackColumnOptions = [
    { id: "position", label: "Playlist number" },
    { id: "artist", label: "Artist" },
    { id: "album", label: "Album" },
    { id: "route", label: "Route" },
    { id: "duration", label: "Duration" },
  ] as const;
  type TrackColumn = (typeof trackColumnOptions)[number]["id"];
  const defaultTrackColumns: Record<TrackColumn, boolean> = {
    position: true,
    artist: true,
    album: true,
    route: true,
    duration: true,
  };
  let trackColumns = $state<Record<TrackColumn, boolean>>({ ...defaultTrackColumns });
  let scheduleEditorOpen = $state(false);
  let scheduleCron = $state("");
  let scheduleTimeZone = $state("");
  let scheduleEnabled = $state(true);
  let scheduleSaving = $state(false);
  let scheduleError = $state("");
  let viewMode = $state<"resolved" | "source" | "target">("resolved");
  let settingsOpen = $state(false);
  let sourceUpdateOpen = $state(false);
  let rematchOpen = $state(false);

  const visiblePlaylists = $derived(filterPlaylists(playlists, query, stateFilter, sort));
  const pageCount = $derived(Math.max(1, Math.ceil(visiblePlaylists.length / 20)));
  const currentPage = $derived(Math.min(page, pageCount));
  const pagePlaylists = $derived(
    visiblePlaylists.slice((currentPage - 1) * 20, currentPage * 20),
  );
  const projectedTracks = $derived.by((): PlaylistTrack[] => {
    if (!details?.clientProjection) return [];
    return details.clientProjection.tracks.map((track) => {
      const source = viewMode === "target"
        ? undefined
        : details?.tracks.find((item) => item.sourcePosition === track.sourcePosition);
      return {
        sourcePosition: track.sourcePosition,
        position: track.position,
        externalSnapshotId: source?.externalSnapshotId ?? "",
        title: track.title,
        artists: track.artists,
        album: track.album,
        isrc: source?.isrc,
        durationMs: track.durationMs,
        artworkUrl: source?.artworkUrl,
        backendItemId: track.itemId,
        routeKind: track.routeKind === "unresolved" ? "unmatched" : track.routeKind,
        routeProviderId: track.routeProviderId,
        matchState: source?.matchState,
        targetEligible: source?.targetEligible,
        outcomeCode: source?.outcomeCode,
        targetStatus: source?.targetStatus,
        providerRoutes: source?.providerRoutes ?? [],
      };
    });
  });
  const visibleTracks = $derived(
    filterTracks(projectedTracks, trackQuery, routeFilter, trackSort),
  );
  const selected = $derived(playlists.find((playlist) => playlist.id === selectedId));
  const detailProjectionOptions = $derived(playlistProjectionOptions(
    details ? providerName(details.sourceProviderId) : "Source",
    details ? providerName(details.targetProtocol) : "Media server",
    details ? `the selected ${providerName(details.targetProtocol)} playlist` : "the selected media-server playlist",
  ));
  const selectedProjectionLabel = $derived(
    detailProjectionOptions.find((option) => option.id === viewMode)?.label ?? "Selected songs",
  );
  const savedProjectionLabel = $derived(
    detailProjectionOptions.find((option) => option.id === selected?.projectionMode)?.label ?? "Media server when available",
  );
  const visibleTrackColumnCount = $derived(
    trackColumnOptions.filter((column) => trackColumns[column.id]).length,
  );
  const trackTableMinWidth = $derived(
    20 +
      (trackColumns.position ? 3 : 0) +
      (trackColumns.artist ? 12 : 0) +
      (trackColumns.album ? 14 : 0) +
      (trackColumns.route ? 9 : 0) +
      (trackColumns.duration ? 4.5 : 0),
  );

  $effect(() => {
    if (detailOpen) {
      detailWasOpen = true;
      return;
    }
    if (!detailWasOpen) return;
    detailWasOpen = false;
    selectedId = "";
    details = null;
    detailLoading = false;
    detailError = "";
    detailRequest++;
    const returnFocus = detailReturnFocus;
    detailReturnFocus = null;
    queueMicrotask(() => {
      if (returnFocus?.isConnected) returnFocus.focus();
    });
  });

  $effect(() => {
    if (matchOpen) {
      matchWasOpen = true;
      return;
    }
    if (!matchWasOpen) return;
    matchWasOpen = false;
    const returnFocus = matchReturnFocus;
    matchReturnFocus = null;
    queueMicrotask(() => {
      if (returnFocus?.isConnected) returnFocus.focus();
    });
  });

  function provider(providerId: string) {
    return providers.find((item) => item.id.toLowerCase() === providerId.toLowerCase());
  }

  function providerName(providerId?: string | null) {
    return providerId ? humanize(provider(providerId)?.name ?? providerId) : "Unresolved";
  }

  function relativeTime(value?: string | null) {
    if (!value) return "Not yet";
    const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1_000);
    const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
    const minutes = Math.round(seconds / 60);
    if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
    return formatter.format(Math.round(hours / 24), "day");
  }

  function editSchedule() {
    if (!details) return;
    scheduleCron = details.schedule?.cronExpression ?? "0 3 * * *";
    scheduleTimeZone =
      details.schedule?.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
    scheduleEnabled = details.schedule?.enabled ?? true;
    scheduleError = "";
  }

  async function saveSchedule(event: SubmitEvent) {
    event.preventDefault();
    if (!details || scheduleSaving) return;
    scheduleSaving = true;
    scheduleError = "";
    try {
      const next = details.schedule
        ? await playlistLinks.updateSchedule(details.schedule, {
            cronExpression: scheduleCron,
            timeZoneId: scheduleTimeZone,
            enabled: scheduleEnabled,
          })
        : await playlistLinks.createSchedule(details.id, {
            cronExpression: scheduleCron,
            timeZoneId: scheduleTimeZone,
            overlapPolicy: "skip",
            misfirePolicy: "runOnce",
            enabled: scheduleEnabled,
          });
      details = { ...details, schedule: next };
      scheduleEditorOpen = false;
      feedback = "Automatic update schedule saved.";
      await refresh();
    } catch (cause) {
      scheduleError = cause instanceof Error ? cause.message : "The schedule could not be saved.";
    } finally {
      scheduleSaving = false;
    }
  }

  async function loadDetails(
    id: string,
    returnFocus?: HTMLElement,
    projectionMode?: "resolved" | "source" | "target",
  ) {
    const changingPlaylist = id !== selectedId;
    const playlist = playlists.find((item) => item.id === id);
    const requestedMode = projectionMode ?? (changingPlaylist
      ? playlist?.projectionMode ?? "resolved"
      : viewMode);
    if (returnFocus) detailReturnFocus = returnFocus;
    selectedId = id;
    viewMode = requestedMode;
    if (changingPlaylist) {
      details = null;
      trackQuery = "";
      routeFilter = "all";
    } else if (details && details.clientProjection?.projectionMode !== requestedMode) {
      details = { ...details, clientProjection: null };
    }
    detailOpen = true;
    detailLoading = true;
    detailError = "";
    const request = ++detailRequest;
    try {
      const next = await playlistLinks.details(id, requestedMode);
      if (request === detailRequest && selectedId === id) details = next;
    } catch (cause) {
      if (request === detailRequest)
        detailError = cause instanceof Error ? cause.message : "Playlist details are unavailable.";
    } finally {
      if (request === detailRequest) detailLoading = false;
    }
  }

  async function changeView(mode: string) {
    if (!selectedId || detailLoading || !["resolved", "source", "target"].includes(mode)) return;
    await loadDetails(selectedId, undefined, mode as typeof viewMode);
  }

  async function refresh() {
    if (refreshing) {
      refreshQueued = true;
      return;
    }
    refreshing = true;
    error = "";
    degraded = "";
    const [linksResult, schemaResult] = await Promise.allSettled([
      playlistLinks.list(),
      home.schema(),
    ]);
    if (linksResult.status === "rejected") {
      error =
        linksResult.reason instanceof Error
          ? linksResult.reason.message
          : "Playlists are unavailable.";
    } else {
      playlists = linksResult.value.playlistLinks;
      const nextId = playlists.some((playlist) => playlist.id === selectedId) ? selectedId : "";
      if (nextId && (detailOpen || initialId === nextId)) await loadDetails(nextId);
      else {
        selectedId = "";
        details = null;
      }
    }
    if (schemaResult.status === "fulfilled") providers = schemaResult.value.providers;
    else degraded = "Provider names and artwork are temporarily unavailable.";
    loading = false;
    refreshing = false;
    if (refreshQueued) {
      refreshQueued = false;
      void refresh();
    }
  }

  function scheduleRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void refresh();
    }, 250);
  }

  async function run(name: "sync" | "toggle") {
    if (!selected || action) return;
    action = name;
    feedback = "";
    try {
      if (name === "sync") {
        if (!details) return;
        const result = await playlistLinks.run(selected.id, details.snapshotId);
        operationJobId = result.jobId;
        feedback = result.created ? "Playlist update queued." : "A playlist update is already queued.";
      } else {
        await playlistLinks.setEnabled(selected.id, selected.revision, !selected.enabled);
        feedback = selected.enabled ? "Playlist paused." : "Playlist resumed.";
      }
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The action failed.";
    } finally {
      action = "";
    }
  }

  async function refreshSources(ids = playlists.map((playlist) => playlist.id)) {
    if (action || refreshing || !ids.length) return;
    action = "refresh-sources";
    feedback = "";
    const started = performance.now();
    const results = await runBounded(
      ids,
      3,
      async (id) => { await playlistLinks.refresh(id); },
      (completed, total) => bulkProgress = `${completed}/${total}`,
    );
    const failed = results.filter((result) => result.status === "rejected").length;
    const elapsed = ((performance.now() - started) / 1_000).toFixed(1);
    feedback = failed
      ? `${ids.length - failed} playlists refreshed; ${failed} failed in ${elapsed}s.`
      : `${ids.length} playlists refreshed in ${elapsed}s.`;
    bulkProgress = "";
    action = "";
    await refresh();
  }

  async function playlistAdded(message: string) {
    feedback = message;
    await refresh();
  }

  async function sourceUpdateQueued(jobId: string, message: string) {
    operationJobId = jobId;
    feedback = message;
    await refresh();
  }

  async function openTrackMatch(externalSnapshotId: string, returnFocus?: HTMLElement) {
    if (matchLoading) return;
    if (returnFocus) matchReturnFocus = returnFocus;
    matchLoading = externalSnapshotId;
    try {
      selectedMatch = await matchReview.get(externalSnapshotId);
      if (!selectedMatch) throw new Error("This track no longer has a current match snapshot.");
      matchOpen = true;
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Track details are unavailable.";
    } finally {
      matchLoading = "";
    }
  }

  async function matchSaved(message: string) {
    feedback = message;
    await refresh();
  }

  function setTrackColumn(column: TrackColumn, visible: boolean) {
    trackColumns = { ...trackColumns, [column]: visible };
    localStorage.setItem("allstarr.playlist-track-columns", JSON.stringify(trackColumns));
  }

  onMount(() => {
    try {
      const saved = JSON.parse(
        localStorage.getItem("allstarr.playlist-track-columns") ?? "{}",
      ) as Partial<Record<TrackColumn, boolean>>;
      trackColumns = Object.fromEntries(
        trackColumnOptions.map((column) => [
          column.id,
          typeof saved[column.id] === "boolean"
            ? saved[column.id]
            : defaultTrackColumns[column.id],
        ]),
      ) as Record<TrackColumn, boolean>;
    } catch {
      trackColumns = { ...defaultTrackColumns };
    }
    selectedId = initialId;
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if loading}
  <section class="playlist-layout" aria-label="Loading playlists" aria-busy="true">
    <div class="panel playlist-list skeleton-panel"></div>
    <div class="panel playlist-detail skeleton-panel"></div>
  </section>
{:else if error}
  <RouteError
    eyebrow="Playlists unavailable"
    title="Allstarr could not load the canonical playlist list."
    message={error}
    onRetry={refresh}
  />
{:else if !playlists.length}
  <section class="panel empty-state">
    <span class="empty-orbit" aria-hidden="true">♫</span>
    <p class="eyebrow">Library playlists</p>
    <h2>No linked playlists yet.</h2>
    <p>Add a playlist from a connected Source. Allstarr will show which songs can play and whether the playlist needs attention.</p>
    <button class="button-primary empty-action" type="button" onclick={() => addOpen = true}>Add playlist</button>
  </section>
{:else}
  {#if degraded}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Some playlist data is unavailable.</strong> {degraded}</p>
      <button type="button" onclick={() => void refresh()}>Retry</button>
    </div>
  {/if}

  <section class="playlist-layout" aria-busy={refreshing}>
    <article class="panel playlist-list">
      <header class="playlist-toolbar">
        <div>
          <p class="eyebrow">Linked playlists</p>
          <h2>{playlists.length} linked</h2>
        </div>
        <div class="playlist-toolbar-actions">
          <button class="button-primary" type="button" onclick={() => addOpen = true}>Add playlist</button>
          <button
            class="button-secondary"
            disabled={Boolean(action) || refreshing}
            type="button"
            onclick={() => rematchOpen = true}
          >
            Review rematch
          </button>
          <button
            class="button-secondary"
            disabled={Boolean(action) || refreshing}
            type="button"
            onclick={() => void refreshSources()}
          >
            {action === "refresh-sources" ? `Refreshing ${bulkProgress}` : "Refresh playlists"}
          </button>
        </div>
      </header>
      {#if feedback}<p class="playlist-feedback" role="status">{feedback}</p>{/if}

      <div class="playlist-filters">
        <SearchField bind:value={query} label="Filter playlists" placeholder="Filter playlists" hiddenLabel />
        <SelectField bind:value={stateFilter} label="Playlist status" options={[
          { value: "all", label: "All states" }, { value: "ready", label: "Ready" },
          { value: "attention", label: "Needs attention" }, { value: "paused", label: "Paused" },
        ]} />
        <SelectField bind:value={sort} label="Sort playlists" options={[
          { value: "name", label: "Name" }, { value: "tracks", label: "Tracks" },
          { value: "coverage", label: "Coverage" }, { value: "updated", label: "Updated" },
        ]} />
      </div>

      <div class="playlist-rows" aria-label="Playlists">
        {#each pagePlaylists as playlist}
          {@const confirmedPercent = Math.round(confirmationCoverage(playlist) * 100)}
          <div
            class:active={playlist.id === selectedId}
            class="playlist-row"
          >
            <button
              class="playlist-open-button"
              type="button"
              aria-label={`Open ${playlist.name} playlist details`}
              onclick={(event) => void loadDetails(playlist.id, event.currentTarget)}
            ></button>
            <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
            <span class="playlist-copy">
              <span class="playlist-title-line">
                <strong>{playlist.name}</strong>
                <small class="route-pair">
                  <ProviderMark id={playlist.sourceProviderId} definition={provider(playlist.sourceProviderId)} />
                  <span>{providerName(playlist.sourceProviderId)}</span>
                  <ArrowRight size={18} aria-hidden="true" />
                  <ProviderMark id={playlist.targetProtocol} definition={provider(playlist.targetProtocol)} />
                  <span>{providerName(playlist.targetProtocol)}</span>
                </small>
              </span>
              <small class="playlist-metrics">
                <span>{playlist.matchedCount} confirmed</span>
                <span>{playlist.metrics.review} to review</span>
                <span>{playlist.unmatchedCount} unresolved</span>
                <span>{playlist.lastRunAt
                  ? playlist.mode === "virtual"
                    ? "Updated in Allstarr"
                    : `${playlist.materializedCount} in ${providerName(playlist.targetProtocol)}`
                  : "Not updated yet"}</span>
              </small>
            </span>
            <span
              class="playlist-summary"
              role="group"
              aria-label={`${confirmedPercent}% confirmed, ${playlist.playableCount} of ${playlist.trackCount} playable${playlist.enabled ? "" : ", paused"}`}
            >
              <span class="playlist-coverage">
                <strong>{confirmedPercent}%</strong>
                <small>confirmed</small>
              </span>
              <small>{playlist.playableCount} of {playlist.trackCount} playable</small>
              {#if !playlist.enabled}<small class="status-pill suggested">Paused</small>{/if}
            </span>
            <CoverageBar
              routes={playlist.routeCoverage}
              total={playlist.trackCount}
              unresolved={playlist.unmatchedCount}
              {providerName}
              compact
            />
          </div>
        {:else}
          <div class="compact-empty">
            <strong>No playlists match these filters</strong>
            <button type="button" onclick={() => { query = ""; stateFilter = "all"; }}>Clear filters</button>
          </div>
        {/each}
      </div>
      {#if pageCount > 1}
        <nav class="playlist-pagination" aria-label="Playlist pages">
          <button type="button" disabled={currentPage === 1} onclick={() => { page = Math.max(1, currentPage - 1); }}>Previous</button>
          <span>Page {currentPage} of {pageCount}</span>
          <button type="button" disabled={currentPage === pageCount} onclick={() => { page = Math.min(pageCount, currentPage + 1); }}>Next</button>
        </nav>
      {/if}
    </article>

    <Dialog.Root bind:open={detailOpen}>
      <Dialog.Portal>
        <Dialog.Overlay class="dialog-overlay" />
        <Dialog.Content class="panel playlist-detail playlist-detail-dialog">
      {#if detailLoading && !details}
        <div class="detail-loading" aria-busy="true">Loading playlist tracks…</div>
      {:else if detailError && !details}
        <div class="compact-empty playlist-projection-empty" role="alert">
          <strong>Playlist details are unavailable</strong>
          <p>{detailError}</p>
          <button class="button-secondary" type="button" onclick={() => void loadDetails(selectedId, undefined, viewMode)}>Try again</button>
        </div>
      {:else if details && selected}
        <header class="playlist-hero">
          <MediaArtwork class="hero-art" url={details.artworkUrl} fallback="♫" loading="eager" />
          <div class="playlist-hero-copy">
            <p class="eyebrow">{providerName(details.sourceProviderId)} playlist</p>
            <Dialog.Title>{details.name}</Dialog.Title>
            <p>
              {details.clientProjection?.trackCount ?? 0} {(details.clientProjection?.trackCount ?? 0) === 1 ? "song" : "songs"} shown · {details.trackCount} {details.trackCount === 1 ? "song" : "songs"} in the source
              · {formatDuration(details.durationMs)}
              {#if details.unknownDurationCount} · {details.unknownDurationCount} unknown duration{/if}
              {#if details.hasNewerSourceGeneration}
                · New source refresh waiting
              {/if}
            </p>
            <div class="hero-route">
              <ProviderMark id={details.sourceProviderId} definition={provider(details.sourceProviderId)} />
              <span>{providerName(details.sourceProviderId)}</span>
              <ArrowRight size={18} aria-hidden="true" />
              <ProviderMark id={details.targetProtocol} definition={provider(details.targetProtocol)} />
              <span>{providerName(details.targetProtocol)}</span>
            </div>
          </div>
          <Dialog.Close class="icon-button playlist-dialog-close" aria-label="Close playlist details"><X size={18} aria-hidden="true" /></Dialog.Close>
          <DropdownMenu.Root>
            <DropdownMenu.Trigger class="button-secondary playlist-actions-trigger">
              Actions <ChevronDown size={16} aria-hidden="true" />
            </DropdownMenu.Trigger>
            <DropdownMenu.Portal>
              <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !selected.enabled} onSelect={() => void run("sync")}>Update playlist now</DropdownMenu.Item>
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => { detailOpen = false; rematchOpen = true; }}>Review account rematch</DropdownMenu.Item>
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void refreshSources([selected.id])}>Refresh source</DropdownMenu.Item>
                {#if selected.sourceUpdateAvailable}
                  <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => sourceUpdateOpen = true}>
                    Preview changes to {providerName(selected.sourceProviderId)}
                  </DropdownMenu.Item>
                {/if}
                <DropdownMenu.Separator />
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => settingsOpen = true}>Edit settings</DropdownMenu.Item>
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void run("toggle")}>{selected.enabled ? "Pause" : "Resume"}</DropdownMenu.Item>
              </DropdownMenu.Content>
            </DropdownMenu.Portal>
          </DropdownMenu.Root>
        </header>

        <div class="playlist-view-switcher">
          <span><strong>What listeners see</strong><small>Preview only. Saved choice: {savedProjectionLabel}.</small></span>
          <SegmentedNav
            items={detailProjectionOptions}
            active={viewMode}
            label="What listeners see"
            onchange={(mode) => void changeView(mode)}
          />
        </div>

        <div class="playlist-detail-coverage">
          <CoverageBar
            routes={details.routeCoverage}
            total={details.trackCount}
            unresolved={details.unresolvedCount}
            {providerName}
          />
        </div>

        {#if details.reconciliation && (
          details.reconciliation.addedPositions.length ||
          details.reconciliation.removedPositions.length ||
          details.reconciliation.movedPositions.length ||
          details.reconciliation.duplicatedPositions.length ||
          details.reconciliation.changedPositions.length
        )}
          <p class="action-feedback">
            Changes since the previous source refresh:
            {details.reconciliation.addedPositions.length} added,
            {details.reconciliation.removedPositions.length} removed,
            {details.reconciliation.movedPositions.length} moved,
            {details.reconciliation.duplicatedPositions.length} duplicated,
            {details.reconciliation.changedPositions.length} changed.
          </p>
        {/if}

        <div class="playlist-meta-strip" aria-label="Playlist status">
          <span><strong>{details.matchedCount}</strong> confirmed</span>
          <button
            type="button"
            class="meta-filter-button"
            class:attention={details.reviewCount > 0}
            class:active={routeFilter === "review"}
            disabled={details.reviewCount === 0}
            aria-label={`Show ${details.reviewCount} ${details.reviewCount === 1 ? "track" : "tracks"} needing review`}
            onclick={() => routeFilter = "review"}
          ><strong>{details.reviewCount}</strong> to review</button>
          <span><strong>{details.localCount}</strong> local</span>
          <span><strong>{details.externalCount}</strong> external</span>
          <span class:attention={details.unresolvedCount > 0}><strong>{details.unresolvedCount}</strong> unresolved</span>
          <span>Refreshed <strong>{relativeTime(details.retrievedAt)}</strong></span>
          <span>Rematched <strong>{relativeTime(details.lastRematchedAt)}</strong></span>
          <span>
            {#if details.schedule?.enabled}
              <strong>{scheduleCadence(details.schedule.cronExpression)}</strong>
              · next {relativeTime(details.schedule.nextRunAt)}
              · {details.schedule.timeZoneId}
            {:else if details.schedule}
              <strong>Automatic updates paused</strong>
              · {scheduleCadence(details.schedule.cronExpression)}
              · {details.schedule.timeZoneId}
            {:else}
              <strong>No automatic updates</strong>
            {/if}
          </span>
          <Popover.Root bind:open={scheduleEditorOpen}>
            <Popover.Trigger class="schedule-edit-button" onclick={editSchedule}>
              {details.schedule ? "Edit schedule" : "Set schedule"}
            </Popover.Trigger>
            <Popover.Portal>
              <Popover.Content class="bits-menu schedule-editor" sideOffset={6} align="end">
                <form onsubmit={saveSchedule}>
                  <label class="field">
                    <span>Cron schedule</span>
                    <input bind:value={scheduleCron} required spellcheck="false" />
                  </label>
                  <label class="field">
                    <span>Time zone</span>
                    <input bind:value={scheduleTimeZone} required spellcheck="false" />
                  </label>
                  <label class="schedule-enabled">
                    <input type="checkbox" bind:checked={scheduleEnabled} />
                    Automatic updates enabled
                  </label>
                  {#if scheduleError}<p class="field-error" role="alert">{scheduleError}</p>{/if}
                  <button class="button-primary" type="submit" disabled={scheduleSaving}>
                    {scheduleSaving ? "Saving…" : "Save schedule"}
                  </button>
                </form>
              </Popover.Content>
            </Popover.Portal>
          </Popover.Root>
          <OperationConsole
            playlistName={details.name}
            requestedJobId={operationJobId}
            onTerminal={refresh}
          />
        </div>

        {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

        {#if detailError}
          <div class="compact-empty playlist-projection-empty" role="alert">
            <strong>{selectedProjectionLabel} could not be loaded</strong>
            <p>{detailError}</p>
            <button class="button-secondary" type="button" onclick={() => void loadDetails(selectedId, undefined, viewMode)}>Try again</button>
          </div>
        {:else if details.clientProjection}
        <div class="track-toolbar">
          <SearchField bind:value={trackQuery} label="Filter tracks" placeholder="Filter tracks" hiddenLabel />
          <SelectField bind:value={routeFilter} label="Track route" options={[
            { value: "all", label: "All routes" }, { value: "local", label: providerName(details.targetProtocol) },
            { value: "external", label: "External" }, { value: "unmatched", label: "Unresolved" },
            { value: "review", label: `To review (${details.reviewCount})` },
          ]} />
          <SelectField bind:value={trackSort} label="Sort tracks" options={[
            { value: "position", label: "Playlist order" }, { value: "title", label: "Title" },
            { value: "duration", label: "Duration" }, { value: "route", label: "Route" },
          ]} />
          <Popover.Root>
            <Popover.Trigger
              class="button-secondary track-column-trigger"
              aria-label={`Choose track columns, ${visibleTrackColumnCount} optional columns shown`}
            >
              Columns <ChevronDown size={16} aria-hidden="true" />
            </Popover.Trigger>
            <Popover.Portal>
              <Popover.Content class="bits-menu track-column-picker" sideOffset={6} align="end">
                <strong>Show columns</strong>
                {#each trackColumnOptions as column}
                  <label>
                    <input
                      type="checkbox"
                      checked={trackColumns[column.id]}
                      onchange={(event) => setTrackColumn(column.id, event.currentTarget.checked)}
                    />
                    {column.label}
                  </label>
                {/each}
              </Popover.Content>
            </Popover.Portal>
          </Popover.Root>
        </div>

        <div class="track-table" aria-busy={detailLoading}>
          <div class="track-scroll">
            <table
              class="track-data-table"
              aria-label={`${details.name} tracks`}
              style={`--track-table-min:${trackTableMinWidth}rem`}
            >
              <colgroup>
                {#if trackColumns.position}<col class="track-position-column" />{/if}
                <col class="track-title-column" />
                {#if trackColumns.artist}<col class="track-artist-column" />{/if}
                {#if trackColumns.album}<col class="track-album-column" />{/if}
                {#if trackColumns.route}<col class="track-route-column" />{/if}
                {#if trackColumns.duration}<col class="track-duration-column" />{/if}
                <col class="track-actions-column" />
              </colgroup>
              <thead>
                <tr>
                  {#if trackColumns.position}<th scope="col">#</th>{/if}
                  <th scope="col">Track</th>
                  {#if trackColumns.artist}<th scope="col">Artist</th>{/if}
                  {#if trackColumns.album}<th scope="col">Album</th>{/if}
                  {#if trackColumns.route}<th scope="col">Route</th>{/if}
                  {#if trackColumns.duration}<th scope="col">Time</th>{/if}
                  <th scope="col"><span class="sr-only">Details</span></th>
                </tr>
              </thead>
              <tbody>
                {#each visibleTracks as track}
                  <tr>
                    {#if trackColumns.position}
                      <td class="track-index">{track.position}</td>
                    {/if}
                    <th scope="row" class="track-identity-cell">
                      <span class="track-identity">
                        <MediaArtwork class="track-art" url={track.artworkUrl} />
                        {#if track.externalSnapshotId}
                          <button
                            type="button"
                            class="track-title-button"
                            aria-label={`Open mapping details for ${track.title}`}
                            disabled={matchLoading === track.externalSnapshotId}
                            onclick={(event) => void openTrackMatch(track.externalSnapshotId, event.currentTarget)}
                          >
                            {track.title}
                          </button>
                        {:else}
                          <strong class="track-title-static">{track.title}</strong>
                        {/if}
                        {#if isReviewTrack(track)}
                          <small class="track-review-badge">Needs review</small>
                        {/if}
                        {#if viewMode !== "target" && track.outcomeCode}
                          <small
                            class="track-eligibility"
                            class:included={track.targetEligible}
                            title={playlistOutcomeLabel(track.outcomeCode, `${providerName(details.targetProtocol)} playlist`)}
                          >{playlistOutcomeLabel(track.outcomeCode, `${providerName(details.targetProtocol)} playlist`)}</small>
                        {/if}
                      </span>
                    </th>
                    {#if trackColumns.artist}
                      <td class="track-text-cell">{track.artists.join(", ") || "Unknown artist"}</td>
                    {/if}
                    {#if trackColumns.album}
                      <td class="track-text-cell">{track.album || "—"}</td>
                    {/if}
                    {#if trackColumns.route}
                      <td>
                        <span class="route-cell">
                          <i style={`--route-color:${providerColor(track.routeProviderId ?? track.routeKind)}`}></i>
                          <span>
                            <strong>{providerName(track.routeProviderId ?? (track.routeKind === "local" ? details.targetProtocol : null))}</strong>
                            <small>{track.routeKind}</small>
                          </span>
                        </span>
                      </td>
                    {/if}
                    {#if trackColumns.duration}
                      <td class="track-duration">{formatDuration(track.durationMs)}</td>
                    {/if}
                    <td class="track-menu">
                      <Popover.Root>
                        <Popover.Trigger class="track-menu-trigger" aria-label={`Technical details for ${track.title}`}><MoreHorizontal size={18} aria-hidden="true" /></Popover.Trigger>
                        <Popover.Portal>
                          <Popover.Content class="bits-menu track-details-menu" sideOffset={4} align="end">
                            <div class="track-technical">
                              <strong>{track.matchState ?? "unmatched"}</strong>
                              {#if track.isrc}<small>ISRC {track.isrc}</small>{/if}
                              {#if track.backendItemId}<small>Media server ID {track.backendItemId}</small>{/if}
                              {#each track.providerRoutes as route}
                                <small>{providerName(route.providerId)} · {route.externalId}{route.pinned ? " · pinned" : ""}</small>
                              {/each}
                              {#if track.outcomeCode}<small>{playlistOutcomeLabel(track.outcomeCode, `${providerName(details.targetProtocol)} playlist`)}</small>{/if}
                              {#if track.externalSnapshotId}
                                <button
                                  type="button"
                                  class="button-secondary"
                                  onclick={(event) => void openTrackMatch(track.externalSnapshotId, event.currentTarget)}
                                >Review match</button>
                              {/if}
                            </div>
                          </Popover.Content>
                        </Popover.Portal>
                      </Popover.Root>
                    </td>
                  </tr>
                {:else}
                  <tr>
                    <td class="track-empty" colspan={2 + visibleTrackColumnCount}>
                      <strong>No tracks match these filters</strong>
                    </td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
        </div>
        {:else}
          <div class="compact-empty playlist-projection-empty">
            <strong>{detailLoading ? `Loading ${selectedProjectionLabel}…` : viewMode === "target" ? `No ${providerName(details.targetProtocol)} playlist is selected` : `${selectedProjectionLabel} is unavailable`}</strong>
            {#if !detailLoading}<p>{viewMode === "target" ? `Choose the playlist Allstarr should use in ${providerName(details.targetProtocol)}, then try again.` : "Refresh the original playlist or check that this media server is still connected."}</p>{/if}
          </div>
        {/if}
      {:else}
        <div class="compact-empty"><strong>Select a playlist to inspect its tracks</strong></div>
      {/if}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  </section>
{/if}

<PlaylistSettingsDialog
  bind:open={settingsOpen}
  playlist={selected ?? null}
  {details}
  sourceName={providerName(selected?.sourceProviderId)}
  targetName={providerName(selected?.targetProtocol)}
  onSaved={playlistAdded}
  onEditSchedule={() => {
    editSchedule();
    scheduleEditorOpen = true;
  }}
/>

<PlaylistSourceUpdateDialog
  bind:open={sourceUpdateOpen}
  playlist={selected ?? null}
  providerName={providerName(selected?.sourceProviderId)}
  targetName={providerName(selected?.targetProtocol)}
  onQueued={sourceUpdateQueued}
/>
<PlaylistRematchDialog bind:open={rematchOpen} onQueued={sourceUpdateQueued} />

<AddPlaylistDialog bind:open={addOpen} {providers} onSaved={playlistAdded} />
<MatchDialog
  bind:open={matchOpen}
  match={selectedMatch}
  {providers}
  backend={details?.targetProtocol ?? "Local library"}
  autoSearch
  showReject={false}
  onSaved={matchSaved}
/>
