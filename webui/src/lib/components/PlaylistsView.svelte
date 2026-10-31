<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Popover } from "$lib/components/ui/popover";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Badge } from "$lib/components/ui/badge";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { ArrowRight, ChevronDown, MoreHorizontal, Server, X } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import AddPlaylistDialog from "$lib/components/AddPlaylistDialog.svelte";
  import CoverageBar from "$lib/components/CoverageBar.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import OperationConsole from "$lib/components/OperationConsole.svelte";
  import PlaylistSettingsDialog from "$lib/components/PlaylistSettingsDialog.svelte";
  import BulkRematchDialog from "$lib/components/BulkRematchDialog.svelte";
  import PlaylistSourceUpdateDialog from "$lib/shelved/PlaylistSourceUpdateDialog.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import TrackRoutes from "$lib/components/TrackRoutes.svelte";
  import { reportHref } from "$lib/support";
  import {
    home,
    matchReview,
    mixes,
    playlistLinks,
    type MatchReviewItem,
    type PlaylistCopy,
    type PlaylistDetails,
    type PlaylistLink,
    type PlaylistTrack,
    type ProviderDefinition,
  } from "$lib/api";
  import { relativeTime } from "$lib/activity";
  import {
    confirmationCoverage,
    filterPlaylists,
    filterTracks,
    formatDuration,
    listeningTime,
    mediaServerName,
    isReviewTrack,
    isServiceMix,
    playlistOutcomeLabel,
    runBounded,
    providerColor,
    scheduleCadence,
    scheduleZoneLabel,
    type PlaylistMix,
    type PlaylistSort,
    type TrackRouteFilter,
    type TrackSort,
  } from "$lib/playlists";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  let { initialId = "", currentUserId = "" }: { initialId?: string; currentUserId?: string } = $props();

  let playlists = $state<PlaylistLink[]>([]);
  let removingMix = $state<{ id: string; name: string } | null>(null);
  let removing = $state(false), removeError = $state("");
  let keptMixes = $state<PlaylistMix[]>([]);
  let copies = $state<PlaylistCopy[]>([]);
  let copiesError = $state("");
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
  let exportError = $state("");
  let refreshing = $state(false);
  let passiveRefreshing = $state(false);
  let error = $state("");
  let degraded = $state("");
  let action = $state("");
  let refreshQueued = false;
  let detailRequest = 0;
  let detailWasOpen = false;
  let detailReturnFocus: HTMLElement | null = null;
  let matchWasOpen = false;
  let matchReturnFocus: HTMLElement | null = null;
  let refreshInFlight = false;
  let addOpen = $state(false);
  let operationJobId = $state("");
  let bulkProgress = $state("");
  let matchOpen = $state(false);
  let LoadedMatchDialog = $state<typeof import("$lib/components/MatchDialog.svelte").default | null>(null);
  let selectedMatch = $state<MatchReviewItem | null>(null);
  let matchLoading = $state("");
  let trackMenuOpen = $state<number | null>(null);
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
  let settingsOpen = $state(false);
  let sourceUpdateOpen = $state(false);
  let rematchOpen = $state(false);

  const playlistCount = $derived(playlists.length + keptMixes.length);
  const visiblePlaylists = $derived(filterPlaylists<PlaylistLink | PlaylistMix>([...playlists, ...keptMixes], query, stateFilter, sort));
  const refreshablePlaylists = $derived(playlists.filter((playlist) => playlist.importMode === "linked"));
  const projectedTracks = $derived.by((): PlaylistTrack[] => {
    if (!details) return [];
    // Playlists written into the media server have no separate listener projection; show their source rows.
    if (!details.clientProjection)
      return details.tracks.map((track) => ({
        ...track,
        routeKind: track.routeKind === "unresolved" ? "unmatched" : track.routeKind,
      }));
    const sourceTracks = new Map(details.tracks.map((track) => [track.sourcePosition, track]));
    return details.clientProjection.tracks.map((track) => {
      const source = sourceTracks.get(track.sourcePosition);
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
  const targetName = $derived(details ? providerName(details.targetProtocol) : "");
  const compositionLegend = $derived.by(() => {
    if (!details) return [];
    const routes = details.routeCoverage
      .filter((route) => route.providerId !== "unresolved" && route.count > 0)
      .map((route) => ({ id: route.providerId, name: providerName(route.providerId), count: route.count }));
    return details.unresolvedCount
      ? [...routes, { id: "unresolved", name: "Not found", count: details.unresolvedCount }]
      : routes;
  });
  const sourceChanges = $derived.by(() => {
    const changes = details?.reconciliation;
    if (!changes) return "";
    return [
      [changes.addedPositions.length, "added"],
      [changes.removedPositions.length, "removed"],
      [changes.movedPositions.length, "moved"],
      [changes.duplicatedPositions.length, "duplicated"],
      [changes.changedPositions.length, "changed"],
    ].filter(([count]) => count).map(([count, label]) => `${count} ${label}`).join(", ") || "No changes";
  });
  const visibleTracks = $derived(
    filterTracks(projectedTracks, trackQuery, routeFilter, trackSort),
  );
  const selected = $derived(playlists.find((playlist) => playlist.id === selectedId));
  const visibleTrackColumnCount = $derived(
    trackColumnOptions.filter((column) => trackColumns[column.id]).length,
  );
  const trackTableMinWidth = $derived(
    20 +
      (trackColumns.position ? 3 : 0) +
      (trackColumns.artist ? 12 : 0) +
      (trackColumns.album ? 14 : 0) +
      (trackColumns.route ? 12 : 0) +
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
    operationJobId = "";
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

  const provider = (providerId: string) => findProviderDefinition(providers, providerId);

  function providerName(providerId?: string | null) {
    return mediaServerName(providerId) ?? providerDisplayName(providers, providerId, "Unresolved");
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
      toast.success("Automatic update schedule saved.");
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
  ) {
    const changingPlaylist = id !== selectedId;
    if (returnFocus) detailReturnFocus = returnFocus;
    selectedId = id;
    if (changingPlaylist) {
      details = null;
      operationJobId = "";
      exportError = "";
      trackQuery = "";
      routeFilter = "all";
    }
    detailOpen = true;
    detailLoading = changingPlaylist || !details;
    detailError = "";
    const request = ++detailRequest;
    try {
      const next = await playlistLinks.details(id, "resolved");
      if (request === detailRequest && selectedId === id) details = next;
    } catch (cause) {
      if (request === detailRequest)
        detailError = cause instanceof Error ? cause.message : "Playlist details are unavailable.";
    } finally {
      if (request === detailRequest) detailLoading = false;
    }
  }

  async function refresh(passive = false) {
    if (refreshInFlight) {
      if (!passive) refreshQueued = true;
      return;
    }
    refreshInFlight = true;
    if (passive) passiveRefreshing = true;
    else refreshing = true;
    error = "";
    degraded = "";
    try {
      const mixesRequest = Promise.allSettled([currentUserId ? mixes.list() : Promise.resolve([])]);
      const [linksResult, schemaResult, copiesResult] = await Promise.allSettled([
        playlistLinks.list(),
        home.schema(),
        currentUserId ? playlistLinks.copies(currentUserId) : Promise.resolve([]),
      ]);
      if (copiesResult.status === "fulfilled") { copies = copiesResult.value; copiesError = ""; }
      else copiesError = "Your playlist copies could not be checked. Refresh to try again.";
      if (linksResult.status === "rejected") {
        error =
          linksResult.reason instanceof Error
            ? linksResult.reason.message
            : "Playlists are unavailable.";
      } else {
        playlists = linksResult.value.playlistLinks.filter(link => link.enabled || !isServiceMix(link));
        const nextId = playlists.some((playlist) => playlist.id === selectedId) ? selectedId : "";
        if (nextId && (detailOpen || initialId === nextId)) await loadDetails(nextId);
        else {
          selectedId = "";
          details = null;
        }
      }
      if (schemaResult.status === "fulfilled") providers = schemaResult.value.providers;
      else degraded = "Provider names and artwork are temporarily unavailable.";
      if (playlists.length || error) loading = false;
      const [mixesResult] = await mixesRequest;
      if (mixesResult.status === "fulfilled") {
        const nextMixes: PlaylistMix[] = mixesResult.value
          .filter((mix) => mix.inApps && mix.kind !== "external" && !playlists.some((playlist) => playlist.id === mix.id))
          .map((mix) => ({ ...mix, trackCount: null }));
        const results = await runBounded(nextMixes, 3, async (mix) => {
          mix.trackCount = (await mixes.get(mix.id)).songs.length;
        });
        keptMixes = nextMixes;
        if (results.some((result) => result.status === "rejected"))
          degraded = [degraded, "Some mix song counts are temporarily unavailable."].filter(Boolean).join(" ");
      } else degraded = [degraded, "Your mixes are temporarily unavailable."].filter(Boolean).join(" ");
      loading = false;
    } finally {
      refreshInFlight = false;
      if (passive) passiveRefreshing = false;
      else refreshing = false;
      if (refreshQueued) {
        refreshQueued = false;
        void refresh();
      }
    }
  }

  const refreshScheduler = createRefreshScheduler(() => refresh(true));
  const scheduleRefresh = refreshScheduler.schedule;

  function hasCopy(playlist: PlaylistLink) {
    const same = (left?: string | null, right?: string | null) => Boolean(left && right && left.replaceAll("-", "").toLowerCase() === right.replaceAll("-", "").toLowerCase());
    return copies.some(copy => copy.backendInstanceId === playlist.targetBackendInstanceId &&
      (same(copy.originalPlaylistId, playlist.virtualPlaylistId) || same(copy.id, playlist.virtualPlaylistId) ||
        same(copy.originalTargetPlaylistId, playlist.targetPlaylistId)));
  }

  async function run(name: "sync" | "toggle") {
    if (!selected || action) return;
    action = name;
    try {
      if (name === "sync") {
        if (!details) return;
        const result = await playlistLinks.run(selected.id, details.snapshotId);
        operationJobId = result.jobId;
        toast.success(result.created ? "Playlist update queued." : "A playlist update is already queued.");
      } else {
        await playlistLinks.setEnabled(selected.id, selected.revision, !selected.enabled);
        toast.success(selected.enabled ? "Playlist paused." : "Playlist resumed.");
      }
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The action failed.");
    } finally {
      action = "";
    }
  }

  async function exportMissingSongs(format: "csv" | "json") {
    if (!selectedId || action) return;
    const id = selectedId;
    action = "export";
    exportError = "";
    try {
      const blob = await playlistLinks.missingSongs(id, format);
      const url = URL.createObjectURL(blob);
      try {
        const link = document.createElement("a");
        link.href = url;
        link.download = `missing-songs.${format}`;
        document.body.appendChild(link);
        link.click();
        link.remove();
      } finally { URL.revokeObjectURL(url); }
    } catch (cause) {
      if (selectedId === id)
        exportError = cause instanceof Error ? cause.message : "Missing songs could not be exported.";
    } finally { action = ""; }
  }

  async function refreshSources(ids = refreshablePlaylists.map((playlist) => playlist.id)) {
    if (action || refreshing || !ids.length) return;
    action = "refresh-sources";
    const started = performance.now();
    const results = await runBounded(
      ids,
      3,
      async (id) => { await playlistLinks.refresh(id); },
      (completed, total) => bulkProgress = `${completed}/${total}`,
    );
    const failed = results.filter((result) => result.status === "rejected").length;
    const elapsed = ((performance.now() - started) / 1_000).toFixed(1);
    toast.show(failed
      ? `${ids.length - failed} playlists refreshed; ${failed} failed in ${elapsed}s.`
      : `${ids.length} playlists refreshed in ${elapsed}s.`, failed ? "error" : "success");
    bulkProgress = "";
    action = "";
    await refresh();
  }

  async function playlistAdded(message: string) {
    toast.success(message);
    await refresh();
  }

  async function sourceUpdateQueued(jobId: string, message: string) {
    operationJobId = jobId;
    toast.success(message);
    await refresh();
  }

  async function openTrackMatch(externalSnapshotId: string, returnFocus?: HTMLElement) {
    if (matchLoading) return;
    if (returnFocus) matchReturnFocus = returnFocus;
    matchLoading = externalSnapshotId;
    try {
      const [match, dialog] = await Promise.all([
        matchReview.get(externalSnapshotId),
        LoadedMatchDialog ?? import("$lib/components/MatchDialog.svelte").then((module) => module.default),
      ]);
      if (!match) throw new Error("This track no longer has a current match snapshot.");
      selectedMatch = match;
      LoadedMatchDialog = dialog;
      matchOpen = true;
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Track details are unavailable.");
    } finally {
      matchLoading = "";
    }
  }

  async function removeMix() {
    if (!removingMix || removing) return;
    const target = removingMix;
    removing = true; removeError = "";
    try {
      await mixes.remove(target.id);
      keptMixes = keptMixes.filter(mix => mix.id !== target.id);
      playlists = playlists.filter(playlist => playlist.id !== target.id);
      removingMix = null;
    } catch { removeError = "This mix could not be removed. Try again."; }
    finally { removing = false; }
  }

  async function matchSaved(message: string) {
    toast.success(message);
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
      refreshScheduler.cancel();
    };
  });
</script>

{#if loading}
  <section class="playlist-layout" aria-label="Loading playlists" aria-busy="true">
    <Skeleton class="panel playlist-list skeleton-panel" />
    <Skeleton class="panel playlist-detail skeleton-panel" />
  </section>
{:else if error}
  <RouteError
    eyebrow="Playlists unavailable"
    title="Allstarr could not load the canonical playlist list."
    message={error}
    onRetry={refresh}
  />
{:else if !playlistCount}
  <section class="panel empty-state">
    <span class="empty-orbit" aria-hidden="true">♫</span>
    <h2>No playlists yet</h2>
    <p>Import a playlist from Spotify or another connected account. Allstarr fills it with songs from your library and streams the rest.</p>
    <Button class="empty-action" onclick={() => addOpen = true}>Import playlist</Button>
  </section>
{:else}
  {#if degraded}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Some playlist data is unavailable.</strong> {degraded}</p>
      <Button variant="secondary" size="sm" disabled={refreshing} onclick={() => void refresh()}>{refreshing ? "Trying again…" : "Retry"}</Button>
    </div>
  {/if}

  <section class="playlist-layout" aria-busy={refreshing || passiveRefreshing}>
    {#if copiesError}<p class="notice-error" role="status">{copiesError}</p>{/if}
    <article class="panel playlist-list">
      <header class="panel-heading playlist-toolbar">
        <div>
          <h2>Playlists</h2>
          <p>{playlistCount} {playlistCount === 1 ? "playlist" : "playlists"}</p>
        </div>
        <div class="panel-heading-actions playlist-toolbar-actions">
          <Button onclick={() => addOpen = true}>Import playlist</Button>
          <Button
            variant="secondary"
            disabled={Boolean(action) || refreshing || refreshablePlaylists.length === 0}
            type="button"
            onclick={() => rematchOpen = true}
          >
            Review rematch
          </Button>
          <Button
            variant="secondary"
            disabled={Boolean(action) || refreshing}
            type="button"
            onclick={() => void refreshSources()}
          >
            {action === "refresh-sources" ? `Refreshing ${bulkProgress}` : "Refresh playlists"}
          </Button>
        </div>
      </header>


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
        {#each visiblePlaylists as playlist}
          {#if "inApps" in playlist}
            <div class="playlist-row playlist-row-mix">
              <a
                class="playlist-open-button"
                href={`#/for-you?mix=${encodeURIComponent(playlist.id)}`}
                aria-label={`Open ${playlist.name} mix`}
              ></a>
              <MediaArtwork class="playlist-art" url={`/api/admin/mixes/${encodeURIComponent(playlist.id)}/cover?v=${encodeURIComponent(playlist.updatedAt ?? playlist.recipeVersion ?? "")}`} fallback="♫" />
              <span class="playlist-copy">
                <span class="playlist-title-line"><strong>{playlist.name}</strong></span>
                <small class="playlist-metrics">Mix · in your apps</small>
              </span>
              <span class="playlist-summary">
                <small>{playlist.trackCount === null ? "Song count unavailable" : `${playlist.trackCount} ${playlist.trackCount === 1 ? "song" : "songs"}`}</small>
                <span class="mix-remove"><Button variant="ghost" aria-label={`Remove ${playlist.name} from my apps`} onclick={() => { removeError = ""; removingMix = playlist; }}>Remove from my apps</Button></span>
              </span>
            </div>
          {:else}
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
                  {#if hasCopy(playlist)}<Badge tone="neutral">Your copy</Badge>{/if}
                  {#if playlist.importMode === "oneTime"}<Badge tone="neutral">Once</Badge>{/if}
                  {#if playlist.trackRetention === "keepAll"}<Badge state="healthy">Kept</Badge>{/if}
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
                  <span>{playlist.lastRunCode === "backend_access_missing" ? "Media-server access needed" : playlist.lastRunAt
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
                {#if !playlist.enabled}<Badge state="suggested">Paused</Badge>{/if}
                {#if isServiceMix(playlist)}<span class="mix-remove"><Button variant="ghost" aria-label={`Remove ${playlist.name} from my apps`} onclick={() => { removeError = ""; removingMix = playlist; }}>Remove from my apps</Button></span>{/if}
              </span>
              <CoverageBar
                routes={playlist.routeCoverage}
                total={playlist.trackCount}
                unresolved={playlist.unmatchedCount}
                {providerName}
                compact
              />
            </div>
          {/if}
        {:else}
          <div class="compact-empty">
            <strong>No playlists match these filters</strong>
            <Button variant="secondary" size="sm" onclick={() => { query = ""; stateFilter = "all"; }}>Clear filters</Button>
          </div>
        {/each}
      </div>
    </article>

    <Dialog.Root bind:open={detailOpen}>
      <Dialog.Portal>
        <Dialog.Overlay class="dialog-overlay" />
        <Dialog.Content class="source-dialog playlist-detail playlist-detail-dialog" aria-label={selected?.name ?? "Playlist details"}>
      {#if detailLoading && !details}
        <div class="detail-loading" aria-busy="true">Loading playlist tracks…</div>
      {:else if detailError && !details}
        <div class="compact-empty playlist-projection-empty" role="alert">
          <strong>Playlist details are unavailable</strong>
          <p>{detailError}</p>
          <Button variant="secondary" onclick={() => void loadDetails(selectedId)}>Try again</Button>
        </div>
      {:else if details && selected}
        <header class="playlist-hero">
          <MediaArtwork class="hero-art" url={details.artworkUrl} fallback="♫" loading="eager" />
          <div class="playlist-hero-copy">
            <Dialog.Title>{details.name}</Dialog.Title>
            {#if hasCopy(selected)}<Badge tone="neutral">Your copy</Badge><p class="playlist-hero-summary">You edited this playlist, so you have your own copy. The original keeps syncing for everyone else.</p>{/if}
            <p class="playlist-hero-summary">
              {details.trackCount} {details.trackCount === 1 ? "song" : "songs"}, {listeningTime(details.durationMs)}
              {#if details.unknownDurationCount}<span>({details.unknownDurationCount} without a length)</span>{/if}
            </p>
            <p class="hero-route">
              <ProviderMark id={details.sourceProviderId} definition={provider(details.sourceProviderId)} />
              <span>{providerName(details.sourceProviderId)}</span>
              <ArrowRight size={16} aria-hidden="true" />
              <Server size={16} aria-hidden="true" />
              <span>{targetName}</span>
              {#if details.hasNewerSourceGeneration}<Badge tone="accent">Source update waiting</Badge>{/if}
            </p>
          </div>
          <div class="playlist-hero-actions">
            <DropdownMenu.Root>
              <DropdownMenu.Trigger class={`${buttonVariants({ variant: "secondary" })} playlist-actions-trigger`}>
                Actions <ChevronDown size={16} aria-hidden="true" />
              </DropdownMenu.Trigger>
              <DropdownMenu.Portal>
                <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                  <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !selected.enabled} onSelect={() => void run("sync")}>{selected.importMode === "oneTime" ? "Rebuild from imported copy" : "Update playlist now"}</DropdownMenu.Item>
                  <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => { detailOpen = false; rematchOpen = true; }}>Review account rematch</DropdownMenu.Item>
                  {#if selected.importMode === "linked"}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void refreshSources([selected.id])}>Refresh source</DropdownMenu.Item>{/if}
                  {#if selected.sourceUpdateAvailable}
                    <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => sourceUpdateOpen = true}>
                      Preview changes to {providerName(selected.sourceProviderId)}
                    </DropdownMenu.Item>
                  {/if}
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !details.unresolvedCount} onSelect={() => void exportMissingSongs("csv")}>Export missing songs as CSV</DropdownMenu.Item>
                  <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !details.unresolvedCount} onSelect={() => void exportMissingSongs("json")}>Export missing songs as JSON</DropdownMenu.Item>
                  <DropdownMenu.Separator />
                  <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => settingsOpen = true}>Edit settings</DropdownMenu.Item>
                  <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void run("toggle")}>{selected.enabled ? "Pause" : "Resume"}</DropdownMenu.Item>
                </DropdownMenu.Content>
              </DropdownMenu.Portal>
            </DropdownMenu.Root>
            <Dialog.Close class="icon-button playlist-dialog-close" aria-label="Close playlist details"><X size={18} aria-hidden="true" /></Dialog.Close>
          </div>
        </header>

        {#if details.lastRunCode === "backend_access_missing"}
          <div class="degraded-banner" role="status"><span aria-hidden="true">!</span><p>Restore media-server access to resume playlist sync and library scans.</p><Button variant="secondary" href="#/settings/access">Open media-server access</Button></div>
        {/if}
        {#if exportError}<p class="field-error" role="alert">{exportError}</p>{/if}

        <section class="playlist-overview" aria-label="Playlist summary">
          <div class="playlist-composition">
            <div class="composition-heading">
              <h3>Where songs play from</h3>
              <span>{Math.max(0, details.trackCount - details.unresolvedCount)} of {details.trackCount} play</span>
            </div>
            <CoverageBar
              routes={details.routeCoverage}
              total={details.trackCount}
              unresolved={details.unresolvedCount}
              {providerName}
            />
            <ul class="composition-legend">
              {#each compositionLegend as item (item.id)}
                <li class:attention={item.id === "unresolved"}>
                  <span class="legend-swatch" style={`--route-color:${providerColor(item.id)}`} aria-hidden="true"></span>
                  {item.name}<strong>{item.count}</strong>
                </li>
              {/each}
              {#if details.reviewCount > 0}
                <li>
                  <button
                    type="button"
                    class="review-filter-button"
                    class:active={routeFilter === "review"}
                    aria-label={`Show ${details.reviewCount} ${details.reviewCount === 1 ? "track" : "tracks"} needing review`}
                    onclick={() => routeFilter = "review"}
                  >{details.reviewCount} to review</button>
                </li>
              {/if}
            </ul>
            <p class="composition-note">Songs keep their original order. Each one plays from {targetName} when it's in your library and streams through Allstarr when it isn't.</p>
          </div>

          <dl class="playlist-facts">
            <div>
              <dt>Updates</dt>
              <dd>
                {#if selected.importMode === "oneTime"}
                  <strong>Imported once</strong>
                  <span class="fact-detail">Later changes to the original are ignored</span>
                {:else if details.schedule?.enabled}
                  <strong>{scheduleCadence(details.schedule.cronExpression)}{scheduleZoneLabel(details.schedule.timeZoneId) ? ` ${scheduleZoneLabel(details.schedule.timeZoneId)}` : ""}</strong>
                  <span class="fact-detail">Next {relativeTime(details.schedule.nextRunAt, "not scheduled yet")}</span>
                {:else if details.schedule}
                  <strong>Automatic updates paused</strong>
                  <span class="fact-detail">{scheduleCadence(details.schedule.cronExpression)} when resumed</span>
                {:else}
                  <strong>No automatic updates</strong>
                {/if}
                {#if selected.importMode === "linked"}<Popover.Root bind:open={scheduleEditorOpen}>
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
                    <Checkbox bind:checked={scheduleEnabled} />
                    Automatic updates enabled
                  </label>
                  {#if scheduleError}<p class="field-error" role="alert">{scheduleError}</p>{/if}
                  <Button type="submit" disabled={scheduleSaving}>
                    {scheduleSaving ? "Saving…" : "Save schedule"}
                  </Button>
                </form>
              </Popover.Content>
            </Popover.Portal>
          </Popover.Root>{/if}
              </dd>
            </div>
            <div>
              <dt>Original checked</dt>
              <dd>
                <strong>{relativeTime(details.retrievedAt, "Not yet")}</strong>
                {#if sourceChanges}<span class="fact-detail">{sourceChanges}</span>{/if}
              </dd>
            </div>
            <div>
              <dt>Songs rematched</dt>
              <dd><strong>{relativeTime(details.lastRematchedAt, "Not yet")}</strong></dd>
            </div>
            <div class="playlist-fact-run">
              <dt>Latest run</dt>
              <dd>
                <OperationConsole
                  playlistName={details.name}
                  requestedJobId={operationJobId}
                  onTerminal={refresh}
                />
              </dd>
            </div>
          </dl>
        </section>

        {#if detailError}
          <div class="compact-empty playlist-projection-empty" role="alert">
            <strong>Songs could not be loaded</strong>
            <p>{detailError}</p>
            <Button variant="secondary" onclick={() => void loadDetails(selectedId)}>Try again</Button>
          </div>
        {:else if projectedTracks.length || details.clientProjection}
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
              class={`${buttonVariants({ variant: "secondary" })} track-column-trigger`}
              aria-label={`Choose track columns, ${visibleTrackColumnCount} optional columns shown`}
            >
              Columns <ChevronDown size={16} aria-hidden="true" />
            </Popover.Trigger>
            <Popover.Portal>
              <Popover.Content class="bits-menu track-column-picker" sideOffset={6} align="end">
                <strong>Show columns</strong>
                {#each trackColumnOptions as column}
                  <label>
                    <Checkbox
                      checked={trackColumns[column.id]}
                      onCheckedChange={(checked) => setTrackColumn(column.id, checked)}
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
                  {#if trackColumns.position}<th scope="col" class="track-index">#</th>{/if}
                  <th scope="col">Track</th>
                  {#if trackColumns.artist}<th scope="col">Artist</th>{/if}
                  {#if trackColumns.album}<th scope="col">Album</th>{/if}
                  {#if trackColumns.route}<th scope="col">Playback routes</th>{/if}
                  {#if trackColumns.duration}<th scope="col" class="track-duration">Time</th>{/if}
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
                            aria-label={matchLoading === track.externalSnapshotId
                              ? `Loading mapping details for ${track.title}`
                              : `Open mapping details for ${track.title}`}
                            disabled={Boolean(matchLoading)}
                            onclick={(event) => void openTrackMatch(track.externalSnapshotId, event.currentTarget)}
                          >
                            {matchLoading === track.externalSnapshotId ? `Loading ${track.title}…` : track.title}
                          </button>
                        {:else}
                          <strong class="track-title-static">{track.title}</strong>
                        {/if}
                        {#if isReviewTrack(track)}
                          <small class="track-review-badge">Needs review</small>
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
                        <TrackRoutes {track} {providers} backend={details.targetProtocol} />
                      </td>
                    {/if}
                    {#if trackColumns.duration}
                      <td class="track-duration">{formatDuration(track.durationMs)}</td>
                    {/if}
                    <td class="track-menu">
                      <Popover.Root
                        open={trackMenuOpen === track.sourcePosition}
                        onOpenChange={(open) => trackMenuOpen = open ? track.sourcePosition : null}
                      >
                        <Popover.Trigger id={`track-details-${track.sourcePosition}`} class="track-menu-trigger" aria-label={`Technical details for ${track.title}`}><MoreHorizontal size={18} aria-hidden="true" /></Popover.Trigger>
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
                                <Button
                                  variant="secondary"
                                  size="sm"
                                  disabled={Boolean(matchLoading)}
                                  onclick={() => {
                                    trackMenuOpen = null;
                                    void openTrackMatch(
                                      track.externalSnapshotId,
                                      document.getElementById(`track-details-${track.sourcePosition}`) ?? undefined,
                                    );
                                  }}
                                >{matchLoading === track.externalSnapshotId ? "Loading match…" : "Review match"}</Button>
                              {/if}
                              {#if track.externalSnapshotId || track.backendItemId}
                                <Button variant="secondary" size="sm" href={reportHref(track.externalSnapshotId
                                  ? { externalSnapshotId: track.externalSnapshotId } : { itemId: track.backendItemId! }, "/library/playlists")} onclick={() => trackMenuOpen = null}>Report a problem</Button>
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
            <strong>{detailLoading ? "Loading songs…" : "No songs yet"}</strong>
            {#if !detailLoading}<p>Update the playlist to fetch its songs from {providerName(details.sourceProviderId)}.</p>{/if}
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
<BulkRematchDialog kind="playlist" bind:open={rematchOpen} onQueued={sourceUpdateQueued} />

<AddPlaylistDialog bind:open={addOpen} {providers} onSaved={playlistAdded} />
{#if LoadedMatchDialog}
  <LoadedMatchDialog
    bind:open={matchOpen}
    match={selectedMatch}
    {providers}
    backend={details?.targetProtocol ?? "Local library"}
    autoSearch
    showReject={false}
    onSaved={matchSaved}
  />
{/if}

<ConfirmDialog bind:open={() => Boolean(removingMix), value => { if (!value) removingMix = null; }} title="Remove from my apps?" description={`${removingMix?.name ?? "This mix"} will be removed from your apps. You can add it again in For you.`} confirmLabel="Remove from my apps" disabled={removing} closeOnConfirm={false} error={removeError} onConfirm={removeMix} />
<style>
  .mix-remove { position: relative; z-index: calc(var(--layer-content) + 1); }
  @media (max-width: 650px) {
    .playlist-row-mix {
      row-gap: 0.18rem;
    }

    .playlist-row-mix :global(.playlist-art) {
      grid-row: span 2;
    }

    .playlist-row-mix .playlist-copy {
      align-self: end;
    }
  }
</style>
