<script lang="ts">
  import { onMount } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Popover } from "$lib/components/ui/popover";
  import { ArrowRight, ChevronDown, MoreHorizontal, X } from "lucide-svelte";
  import AddPlaylistDialog from "$lib/components/AddPlaylistDialog.svelte";
  import ColumnResizeHandle from "$lib/components/ColumnResizeHandle.svelte";
  import CoverageBar from "$lib/components/CoverageBar.svelte";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import OperationConsole from "$lib/components/OperationConsole.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import {
    home,
    matchReview,
    playlistLinks,
    type MatchReviewItem,
    type PlaylistDetails,
    type PlaylistLink,
    type ProviderDefinition,
  } from "$lib/api";
  import { humanize } from "$lib/activity";
  import {
    coverage,
    filterPlaylists,
    filterTracks,
    formatDuration,
    providerColor,
    runBounded,
    type PlaylistSort,
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
  let routeFilter = $state<"all" | "local" | "external" | "unmatched">("all");
  let trackSort = $state<TrackSort>("position");
  let loading = $state(true);
  let detailLoading = $state(false);
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
  let trackColumnWidth = $state(0);
  let routeColumnWidth = $state(0);
  let scheduleEditorOpen = $state(false);
  let scheduleCron = $state("");
  let scheduleTimeZone = $state("");
  let scheduleEnabled = $state(true);
  let scheduleSaving = $state(false);
  let scheduleError = $state("");

  const visiblePlaylists = $derived(filterPlaylists(playlists, query, stateFilter, sort));
  const pageCount = $derived(Math.max(1, Math.ceil(visiblePlaylists.length / 20)));
  const currentPage = $derived(Math.min(page, pageCount));
  const pagePlaylists = $derived(
    visiblePlaylists.slice((currentPage - 1) * 20, currentPage * 20),
  );
  const visibleTracks = $derived(
    details ? filterTracks(details.tracks, trackQuery, routeFilter, trackSort) : [],
  );
  const selected = $derived(playlists.find((playlist) => playlist.id === selectedId));

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
      feedback = "Automatic sync schedule saved.";
      await refresh();
    } catch (cause) {
      scheduleError = cause instanceof Error ? cause.message : "The schedule could not be saved.";
    } finally {
      scheduleSaving = false;
    }
  }

  async function loadDetails(id: string, returnFocus?: HTMLElement) {
    const changingPlaylist = id !== selectedId;
    if (returnFocus) detailReturnFocus = returnFocus;
    selectedId = id;
    if (changingPlaylist) details = null;
    detailOpen = true;
    detailLoading = true;
    const request = ++detailRequest;
    try {
      const next = await playlistLinks.details(id);
      if (request === detailRequest && selectedId === id) details = next;
    } catch (cause) {
      if (request === detailRequest)
        degraded = cause instanceof Error ? cause.message : "Playlist details are unavailable.";
    } finally {
      if (request === detailRequest) detailLoading = false;
    }
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

  async function run(name: "sync" | "rematch" | "toggle") {
    if (!selected || action) return;
    action = name;
    feedback = "";
    try {
      if (name === "sync") {
        if (!details) return;
        const result = await playlistLinks.run(selected.id, details.snapshotId);
        operationJobId = result.jobId;
        feedback = result.created ? "Sync queued." : "Sync is already queued.";
      } else if (name === "rematch") {
        const result = await playlistLinks.run(selected.id);
        operationJobId = result.jobId;
        feedback = result.created ? "Rematch queued." : "Rematch is already queued.";
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

  async function rematchAll() {
    if (action || refreshing || !playlists.length) return;
    action = "rematch-all";
    feedback = "";
    const started = performance.now();
    let queued = 0;
    const results = await runBounded(
      playlists,
      3,
      async (playlist) => {
        if ((await playlistLinks.run(playlist.id)).created) queued++;
      },
      (completed, total) => bulkProgress = `${completed}/${total}`,
    );
    const failed = results.filter((result) => result.status === "rejected").length;
    const elapsed = ((performance.now() - started) / 1_000).toFixed(1);
    feedback = `${queued} rematches queued${failed ? `; ${failed} failed` : ""} in ${elapsed}s.`;
    bulkProgress = "";
    action = "";
    await refresh();
  }

  async function playlistAdded(message: string) {
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
    if (selectedId) await loadDetails(selectedId);
  }

  onMount(() => {
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
    <h2>No managed playlists yet.</h2>
    <p>Add a playlist from an installed Source. Its matches, routes, and sync state will appear here.</p>
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
          <p class="eyebrow">Managed playlists</p>
          <h2>{playlists.length} linked</h2>
        </div>
        <div class="playlist-toolbar-actions">
          <button class="button-primary" type="button" onclick={() => addOpen = true}>Add playlist</button>
          <button
            class="button-secondary"
            disabled={Boolean(action) || refreshing}
            type="button"
            onclick={() => void rematchAll()}
          >
            {action === "rematch-all" ? `Queueing ${bulkProgress}` : "Rematch all"}
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
          {@const playablePercent = Math.round(coverage(playlist) * 100)}
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
                <span>{playlist.matchedCount} matched</span>
                <span>{playlist.metrics.review} {playlist.metrics.review === 1 ? "needs" : "need"} attention</span>
                <span>{playlist.unmatchedCount} unresolved</span>
                <span>{playlist.lastRunAt ? `${playlist.materializedCount} synced` : "Not yet synced"}</span>
              </small>
            </span>
            <span
              class="playlist-summary"
              role="group"
              aria-label={`${playablePercent}% playable, ${playlist.playableCount} of ${playlist.trackCount} tracks${playlist.enabled ? "" : ", paused"}`}
            >
              <span class="playlist-coverage">
                <strong>{playablePercent}%</strong>
                <small>playable</small>
              </span>
              <small>{playlist.playableCount} of {playlist.trackCount} tracks</small>
              {#if !playlist.enabled}<small class="attention">Paused</small>{/if}
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
      {:else if details && selected}
        <header class="playlist-hero">
          <MediaArtwork class="hero-art" url={details.artworkUrl} fallback="♫" loading="eager" />
          <div class="playlist-hero-copy">
            <p class="eyebrow">{providerName(details.sourceProviderId)} playlist</p>
            <Dialog.Title>{details.name}</Dialog.Title>
            <p>
              {details.trackCount} tracks · {formatDuration(details.durationMs)}
              {#if details.unknownDurationCount} · {details.unknownDurationCount} unknown duration{/if}
              · Snapshot v{details.snapshotVersion}
              {#if details.hasNewerSourceGeneration}
                · newer source generation pending
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
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !selected.enabled} onSelect={() => void run("sync")}>Sync</DropdownMenu.Item>
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !selected.enabled} onSelect={() => void run("rematch")}>Rematch</DropdownMenu.Item>
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void refreshSources([selected.id])}>Refresh source</DropdownMenu.Item>
                <DropdownMenu.Separator />
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void run("toggle")}>{selected.enabled ? "Pause" : "Resume"}</DropdownMenu.Item>
              </DropdownMenu.Content>
            </DropdownMenu.Portal>
          </DropdownMenu.Root>
        </header>

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
            Source changes from snapshot v{Math.max(1, details.snapshotVersion - 1)}:
            {details.reconciliation.addedPositions.length} added,
            {details.reconciliation.removedPositions.length} removed,
            {details.reconciliation.movedPositions.length} moved,
            {details.reconciliation.duplicatedPositions.length} duplicated,
            {details.reconciliation.changedPositions.length} changed.
          </p>
        {/if}

        <div class="playlist-meta-strip" aria-label="Playlist status">
          <span><strong>{details.localCount}</strong> local</span>
          <span><strong>{details.externalCount}</strong> external</span>
          <span class:attention={details.unresolvedCount > 0}><strong>{details.unresolvedCount}</strong> unmatched</span>
          <span>Refreshed <strong>{relativeTime(details.retrievedAt)}</strong></span>
          <span>Rematched <strong>{relativeTime(details.lastRematchedAt)}</strong></span>
          <span>
            <strong>{details.schedule?.enabled ? relativeTime(details.schedule.nextRunAt) : details.schedule ? "Paused" : "Manual"}</strong>
            schedule
          </span>
          <Popover.Root bind:open={scheduleEditorOpen}>
            <Popover.Trigger class="schedule-edit-button" onclick={editSchedule}>Edit schedule</Popover.Trigger>
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
                    Automatic sync enabled
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

        <div class="track-toolbar">
          <SearchField bind:value={trackQuery} label="Filter tracks" placeholder="Filter tracks" hiddenLabel />
          <SelectField bind:value={routeFilter} label="Track route" options={[
            { value: "all", label: "All routes" }, { value: "local", label: providerName(details.targetProtocol) },
            { value: "external", label: "External" }, { value: "unmatched", label: "Unresolved" },
          ]} />
          <SelectField bind:value={trackSort} label="Sort tracks" options={[
            { value: "position", label: "Playlist order" }, { value: "title", label: "Title" },
            { value: "duration", label: "Duration" }, { value: "route", label: "Route" },
          ]} />
        </div>

        <div
          class="track-table"
          aria-label={`${details.name} tracks`}
          aria-busy={detailLoading}
          style={`--track-name-width:${trackColumnWidth ? `${trackColumnWidth}px` : "1fr"};--track-route-width:${routeColumnWidth ? `${routeColumnWidth}px` : "0.55fr"}`}
        >
          <div class="track-head">
            <span>#</span>
            <span class="track-column-heading">Track<ColumnResizeHandle bind:value={trackColumnWidth} label="track" min={220} max={520} /></span>
            <span class="track-column-heading">Route<ColumnResizeHandle bind:value={routeColumnWidth} label="route" min={120} max={300} /></span>
            <span>Time</span>
            <span><span class="sr-only">Details</span></span>
          </div>
          <div class="track-scroll">
            {#each visibleTracks as track}
              <div class="track-row">
                <button
                  type="button"
                  class="track-row-link"
                  aria-label={`Open mapping details for ${track.title}`}
                  disabled={matchLoading === track.externalSnapshotId}
                  onclick={(event) => void openTrackMatch(track.externalSnapshotId, event.currentTarget)}
                ></button>
                <span class="track-index">{track.position}</span>
                <span class="track-identity">
                  <MediaArtwork class="track-art" url={track.artworkUrl} />
                  <span><strong>{track.title}</strong><small>{track.artists.join(", ") || "Unknown artist"}{track.album ? ` · ${track.album}` : ""}</small></span>
                </span>
                <span class="route-cell">
                  <i style={`--route-color:${providerColor(track.routeProviderId ?? track.routeKind)}`}></i>
                  <span><strong>{providerName(track.routeProviderId ?? (track.routeKind === "local" ? details.targetProtocol : null))}</strong><small>{track.routeKind}</small></span>
                </span>
                <span class="track-duration">{formatDuration(track.durationMs)}</span>
                <span class="track-menu">
                  <Popover.Root>
                    <Popover.Trigger class="track-menu-trigger" aria-label={`Technical details for ${track.title}`}><MoreHorizontal size={18} aria-hidden="true" /></Popover.Trigger>
                    <Popover.Portal>
                      <Popover.Content class="bits-menu track-details-menu" sideOffset={4} align="end">
                        <div class="track-technical">
                          <strong>{track.matchState ?? "unmatched"}</strong>
                          {#if track.isrc}<small>ISRC {track.isrc}</small>{/if}
                          {#if track.backendItemId}<small>Backend {track.backendItemId}</small>{/if}
                          {#each track.providerRoutes as route}
                            <small>{providerName(route.providerId)} · {route.externalId}{route.pinned ? " · pinned" : ""}</small>
                          {/each}
                          <button
                            type="button"
                            class="button-secondary"
                            onclick={(event) => void openTrackMatch(track.externalSnapshotId, event.currentTarget)}
                          >Review match</button>
                        </div>
                      </Popover.Content>
                    </Popover.Portal>
                  </Popover.Root>
                </span>
              </div>
            {:else}
              <div class="compact-empty"><strong>No tracks match these filters</strong></div>
            {/each}
          </div>
        </div>
      {:else}
        <div class="compact-empty"><strong>Select a playlist to inspect its tracks</strong></div>
      {/if}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  </section>
{/if}

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
