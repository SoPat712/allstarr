<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { countLabel, countNoun } from "$lib/count-label";
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Popover } from "$lib/components/ui/popover";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import PlaylistSyncSettings from "$lib/components/PlaylistSyncSettings.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { ChevronDown, MoreHorizontal, Server } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import PlaylistRemovalDialog from "$lib/components/PlaylistRemovalDialog.svelte";

  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import TrackRoutes from "$lib/components/TrackRoutes.svelte";
  import { reportHref } from "$lib/support";
  import {
    auth,
    home,
    type PlaylistConsentStatus,
    matchReview,
    mixes,
    playlistLinks,
    activityWork,
    type MatchReviewItem,
    type PlaylistCopy,
    type PlaylistDetails,
    type PlaylistLink,
    type PlaylistRemovalPreview,
    type PlaylistTrack,
    type ProviderDefinition,
  } from "$lib/api";
  import { relativeTime } from "$lib/activity";
  import {
    canRebuildInJellyfin,
    canRemovePlaylistFromApps,
    filterPlaylists,
    filterTracks,
    formatDuration,
    listeningTime,
    mediaServerName,
    isReviewTrack,
    isServiceMix,
    isPlaylistMix,
    isPlaylistInApps,
    jellyfinPlaylistWarning,
    playlistOutcomeLabel,
    playlistRestoredMessage,
    runBounded,
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
  let consent = $state<PlaylistConsentStatus | null>(null);
  let removingMix = $state<{ id: string; name: string } | null>(null);
  let removingPlaylist = $state<PlaylistLink | null>(null);
  let removalPreview = $state<PlaylistRemovalPreview | null>(null);
  let removalPreviewLoading = $state(false), removalPreviewError = $state("");
  let removalPreviewRequest = 0;
  let removing = $state(false), removeError = $state("");
  let rebuildingPlaylist = $state<PlaylistLink | null>(null);
  let rebuilding = $state(false), rebuildError = $state("");
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
  let LoadedAddDialog = $state<typeof import("$lib/components/AddPlaylistDialog.svelte").default | null>(null);
  let sourceFilter = $state("");
  let songsOpen = $state(true);
  $effect(() => { if (addOpen && !LoadedAddDialog) void import("$lib/components/AddPlaylistDialog.svelte").then(module => LoadedAddDialog = module.default).catch(() => { addOpen = false; toast.error("The import dialog could not load. Try again."); }); });
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
  let nextSyncAt = $state<string | null>(null);
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
  const allPlaylists = $derived([...playlists, ...keptMixes]);
  const attentionCount = $derived(allPlaylists.filter(needsLook).length);
  const healthyCount = $derived(playlists.filter(item => item.enabled && !needsLook(item)).length + keptMixes.filter(item => item.publication?.status === "healthy").length);
  const orderCount = $derived(playlists.filter(item => Boolean(jellyfinPlaylistWarning(item, providerName(item.sourceProviderId)))).length);
  const totalSongs = $derived(allPlaylists.reduce((sum, item) => sum + (item.trackCount ?? 0), 0));
  const visiblePlaylists = $derived(filterPlaylists<PlaylistLink | PlaylistMix>(allPlaylists, query, "all", sort).filter(item =>
    (!sourceFilter || (!isPlaylistMix(item) && item.sourceProviderId === sourceFilter)) &&
    (stateFilter === "all" || (stateFilter === "attention" ? needsLook(item) : isPlaylistMix(item) ? stateFilter === "ready" && item.publication?.status === "healthy" : stateFilter === "paused" ? !item.enabled : item.enabled && !needsLook(item)))));

  // Only shown where a playlist differs from its group: a feed whose songs fill the library, or your own that doesn't.
  function libraryNote(item: PlaylistLink) {
    const own = (item.playlistKind ?? "yours") === "yours";
    const counts = item.countsTowardLibrary ?? own;
    return own === counts ? "" : counts ? " · Songs in your library" : " · Playlist only";
  }
  function needsLook(item: PlaylistLink | PlaylistMix) {
    if (isPlaylistMix(item)) return !item.publication || item.publication.status === "needsLook";
    return !item.enabled || item.metrics.review > 0 || item.unmatchedCount > 0 || item.metrics.rejected > 0 || Boolean(jellyfinPlaylistWarning(item, providerName(item.sourceProviderId))) || item.lastRunCode === "backend_access_missing";
  }
  function rowState(item: PlaylistLink | PlaylistMix) {
    if (isPlaylistMix(item)) return item.publication?.status === "updating" ? "Updating" : needsLook(item) ? "Needs a look" : "Healthy";
    if (!item.enabled) return "Paused";
    if (item.lastRunCode === "backend_access_missing") return "Access needed";
    if (jellyfinPlaylistWarning(item, providerName(item.sourceProviderId))) return consent?.granted && !consent.needsSignIn && item.lastRunCode?.split(",").some(code => code.trim() === "order") ? "Order fixing" : "Needs a look";
    return needsLook(item) ? "Needs a look" : "Healthy";
  }
  // Playlists you made first, then the ones a service makes for you, then ones followed from someone else.
  const playlistGroups = $derived([
    { id: "yours", label: "Your playlists", note: "", items: visiblePlaylists.filter(item => !isPlaylistMix(item) && (item.playlistKind ?? "yours") === "yours") },
    { id: "feed", label: "Feeds", note: "Made for you by a service", items: visiblePlaylists.filter(item => isPlaylistMix(item) || item.playlistKind === "feed") },
    { id: "followed", label: "Followed", note: "Made by someone else", items: visiblePlaylists.filter(item => !isPlaylistMix(item) && item.playlistKind === "followed") },
  ].filter(group => group.items.length));
  const panelPlaylists = $derived(playlistGroups.flatMap(group => group.items).filter((item): item is PlaylistLink => !isPlaylistMix(item)));
  const panelPosition = $derived(panelPlaylists.findIndex(item => item.id === selectedId));
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
      songsOpen = true;
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
        playlists = linksResult.value.playlistLinks.filter(isPlaylistInApps);
        const scheduled = await activityWork.tasks().catch(() => null);
        nextSyncAt = (scheduled?.items ?? []).filter(item => item.enabled && item.runPlaylistLinkId &&
          playlists.some(playlist => playlist.id === item.runPlaylistLinkId) && item.nextRunAt)
          .map(item => item.nextRunAt!).toSorted()[0] ?? null;
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

  function confirmRebuild(playlist: PlaylistLink) {
    if (action || rebuilding || !canRebuildInJellyfin(playlist)) return;
    rebuildError = "";
    rebuildingPlaylist = playlist;
  }

  async function rebuildPlaylist() {
    if (!rebuildingPlaylist || rebuilding || action) return;
    rebuilding = true;
    action = "rebuild";
    rebuildError = "";
    try {
      await playlistLinks.rebuild(rebuildingPlaylist.id);
      rebuildingPlaylist = null;
      toast.success("Playlist rebuilt in Jellyfin.");
      await refresh();
    } catch (cause) {
      rebuildError = cause instanceof Error ? cause.message : "The playlist could not be rebuilt.";
    } finally {
      rebuilding = false;
      action = "";
    }
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
      ? `${countLabel(ids.length - failed, "playlist")} refreshed; ${failed} failed in ${elapsed}s.`
      : `${countLabel(ids.length, "playlist")} refreshed in ${elapsed}s.`, failed ? "error" : "success");
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
      detailOpen = false;
    } catch { removeError = "This mix could not be removed. Try again."; }
    finally { removing = false; }
  }

  function confirmRemoval(playlist: PlaylistLink) {
    removeError = "";
    if (isServiceMix(playlist)) removingMix = playlist;
    else if (canRemovePlaylistFromApps(playlist)) {
      removingPlaylist = playlist;
      void loadRemovalPreview();
    }
  }

  async function loadRemovalPreview() {
    if (!removingPlaylist || removing) return;
    const target = removingPlaylist;
    const request = ++removalPreviewRequest;
    removalPreview = null;
    removalPreviewLoading = true;
    removalPreviewError = "";
    try {
      const preview = await playlistLinks.removalPreview(target.id);
      if (request === removalPreviewRequest && removingPlaylist?.id === target.id) removalPreview = preview;
    } catch (cause) {
      if (request === removalPreviewRequest && removingPlaylist?.id === target.id)
        removalPreviewError = cause instanceof Error ? cause.message : "Songs added in Jellyfin could not load. Try again.";
    } finally {
      if (request === removalPreviewRequest) removalPreviewLoading = false;
    }
  }

  async function removePlaylist() {
    if (!removingPlaylist || removing || !removalPreview || removalPreviewLoading || removalPreviewError) return;
    const target = removingPlaylist;
    removing = true;
    removeError = "";
    try {
      const removed = await playlistLinks.removeFromApps(target.id, target.revision);
      if (selectedId === target.id) detailOpen = false;
      playlists = playlists.filter(playlist => playlist.id !== target.id);
      removingPlaylist = null;
      toast.success(`${target.name} removed from your apps`, {
        label: "Undo",
        run: async () => {
          try {
            const restored = await playlistLinks.addToApps(target.id, removed.revision);
            await refresh();
            toast.success(playlistRestoredMessage(target.name, restored.skippedItemCount));
          } catch (cause) {
            toast.error(cause instanceof Error ? cause.message : "The playlist could not be restored. Try again.");
          }
        },
      });
    } catch (cause) {
      removeError = cause instanceof Error ? cause.message : "The playlist could not be removed. Try again.";
    } finally {
      removing = false;
    }
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
    void auth.playlistConsent().then(status => { consent = status; }).catch(() => {});
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
  <section aria-label="Loading playlists" aria-busy="true"><RowsSkeleton count={8} /></section>
{:else if error && !playlistCount}
  <ErrorState title="Playlists could not load" detail={error} onretry={() => void refresh()} />
{:else}
  <section class="library-playlists" class:panel-open={detailOpen} aria-busy={refreshing || passiveRefreshing}>
    <article class="library-list">
      <header class="library-heading"><h2>Playlists</h2><div class="library-actions">
        <RefreshButton label="Refresh playlists" disabled={Boolean(action) || refreshing} onrefresh={() => refreshSources()} />
        <Button onclick={() => addOpen = true}>Import playlist</Button>
      </div></header>
      <PageStatus label="Playlist status" items={[
        { label: countLabel(playlistCount, "playlist"), emphasis: true },
        { label: `${countLabel(totalSongs, "song")}${playlists.every(item => item.playableCount === item.trackCount) ? ", all playable" : ""}` },
        ...(orderCount ? [{ label: `${countLabel(orderCount, "order fixing", "orders fixing")} on the next sync`, tone: "warn" as const }] : []),
        ...(nextSyncAt ? [{ label: `Next sync ${new Date(nextSyncAt).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })}` }] : []),
      ]} />
      {#if error || degraded || copiesError}<div class="degraded-banner" role="status"><p>{error || degraded || copiesError}</p><Button variant="text" onclick={() => void refresh()}>Try again</Button></div>{/if}
      <div class="library-filters">
        <FilterSegments label="Playlist filters" value={stateFilter} storageKey="allstarr.library.playlists.filter" onchange={value => stateFilter = value as typeof stateFilter} options={[
          { id: "attention", label: "Needs a look", count: attentionCount }, { id: "ready", label: "Healthy", count: healthyCount },
          { id: "all", label: "All", count: playlistCount }, ...(playlists.some(item => !item.enabled) ? [{ id: "paused", label: "Paused", count: playlists.filter(item => !item.enabled).length }] : []),
        ]} />
        <div class="library-search"><SearchField bind:value={query} label="Filter playlists" placeholder="Search playlists" hiddenLabel />
          <SelectField bind:value={sourceFilter} label="Playlist source" options={[{ value: "", label: "All sources" }, ...[...new Set(playlists.map(item => item.sourceProviderId))].map(value => ({ value, label: providerName(value) }))]} />
          <SelectField bind:value={sort} label="Sort playlists" options={[{ value: "name", label: "Name" }, { value: "tracks", label: "Tracks" }, { value: "coverage", label: "Coverage" }, { value: "updated", label: "Updated" }]} />
        </div>
      </div>
      <div class="library-rows" aria-label="Playlists">
        {#snippet playlistRow(playlist: PlaylistLink | PlaylistMix)}
          <div class="playlist-row">
          {#if isPlaylistMix(playlist)}
            <ItemRow title={playlist.name} subtitle={`Mix · in your apps · ${playlist.trackCount ?? "?"} ${playlist.trackCount === 1 ? "song" : "songs"}`} href={`#/for-you?mix=${encodeURIComponent(playlist.id)}`} actionLabel={`Open ${playlist.name} mix`} attention={needsLook(playlist)}>
              {#snippet leading()}<MediaArtwork class="playlist-art" url={`/api/admin/mixes/${encodeURIComponent(playlist.id)}/cover?v=${encodeURIComponent(playlist.updatedAt ?? playlist.recipeVersion ?? "")}`} />{/snippet}
              {#snippet pill()}<StatePill tone={needsLook(playlist) ? "warn" : playlist.publication?.status === "updating" ? "info" : "ok"} text={rowState(playlist)} />{/snippet}
              {#snippet menu()}<DropdownMenu.Root><DropdownMenu.Trigger class={buttonVariants({ variant: "text", size: "icon" })} aria-label={`More options for ${playlist.name}`}><MoreHorizontal size={18} /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu" align="end"><DropdownMenu.Item class="bits-menu-item library-danger" onSelect={() => { removeError = ""; removingMix = playlist; }}>Remove from my apps</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>{/snippet}
            </ItemRow>
          {:else}
            <ItemRow title={playlist.name} subtitle={`${providerName(playlist.sourceProviderId)} → ${providerName(playlist.targetProtocol)} · ${countLabel(playlist.trackCount, "song")}${playlist.routeCoverage.some(route => ["local", playlist.targetProtocol].includes(route.providerId)) ? ` · ${playlist.routeCoverage.filter(route => ["local", playlist.targetProtocol].includes(route.providerId)).reduce((sum, route) => sum + route.count, 0)} from your library` : ""}${hasCopy(playlist) ? " · Your copy" : ""}${libraryNote(playlist)}`} actionLabel={`Open ${playlist.name} playlist details`} selected={playlist.id === selectedId} attention={needsLook(playlist)} onclick={event => void loadDetails(playlist.id, event.currentTarget as HTMLElement)}>
              {#snippet leading()}<MediaArtwork class="playlist-art" url={playlist.artworkUrl} />{/snippet}
              {#snippet pill()}<StatePill tone={needsLook(playlist) ? "warn" : "ok"} text={rowState(playlist)} />{/snippet}
              {#snippet menu()}{#if isServiceMix(playlist) || canRemovePlaylistFromApps(playlist)}
                <DropdownMenu.Root>
                  <DropdownMenu.Trigger class={buttonVariants({ variant: "text", size: "icon" })} aria-label={`More options for ${playlist.name}`}><MoreHorizontal size={18} /></DropdownMenu.Trigger>
                  <DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu" align="end">
                    <DropdownMenu.Item class="bits-menu-item library-danger" onSelect={() => confirmRemoval(playlist)}>Remove from my apps</DropdownMenu.Item>
                  </DropdownMenu.Content></DropdownMenu.Portal>
                </DropdownMenu.Root>
              {/if}{/snippet}
            </ItemRow>
          {/if}
          </div>
        {/snippet}
        {#each playlistGroups as group (group.id)}
          {#if playlistGroups.length > 1}<h3 class="playlist-group">{group.label}{#if group.note}<small>{group.note}</small>{/if}</h3>{/if}
          {#each group.items as playlist (playlist.id)}{@render playlistRow(playlist)}{/each}
        {:else}<EmptyState title={playlistCount ? "No playlists match these filters" : "No playlists yet"} description={playlistCount ? "Try another filter or search." : "Import a playlist. Songs from your library play alongside songs from connected services."}>{#snippet icon()}<Server size={24} />{/snippet}{#snippet action()}<Button variant="text" onclick={() => { query = ""; sourceFilter = ""; stateFilter = "all"; }}>Clear filters</Button>{/snippet}</EmptyState>{/each}
      </div>
    </article>
    <SidePanel bind:open={detailOpen} title={selected?.name ?? "Playlist details"} description={`${providerName(selected?.sourceProviderId)} playlist · in your apps`}
      onPrevious={panelPosition > 0 ? () => void loadDetails(panelPlaylists[panelPosition - 1].id) : undefined}
      onNext={panelPosition >= 0 && panelPosition < panelPlaylists.length - 1 ? () => void loadDetails(panelPlaylists[panelPosition + 1].id) : undefined}>
      {#snippet menu()}{#if selected}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger class={buttonVariants({ variant: "text", size: "icon" })} aria-label="Actions" disabled={!details}>
            <MoreHorizontal size={18} aria-hidden="true" />
          </DropdownMenu.Trigger>
          <DropdownMenu.Portal>
            <DropdownMenu.Content preventScroll={false} class="bits-menu" sideOffset={6} align="end">
              <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !selected.enabled} onSelect={() => void run("sync")}>{selected.importMode === "oneTime" ? "Rebuild from imported copy" : "Update playlist now"}</DropdownMenu.Item>
              {#if canRebuildInJellyfin(selected)}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || rebuilding} onSelect={() => confirmRebuild(selected)}>Rebuild in Jellyfin</DropdownMenu.Item>{/if}
              <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => { detailOpen = false; rematchOpen = true; }}>Review account rematch</DropdownMenu.Item>
              {#if selected.importMode === "linked"}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void refreshSources([selected.id])}>Refresh source</DropdownMenu.Item>{/if}
              {#if selected.sourceUpdateAvailable}
                <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => sourceUpdateOpen = true}>
                  Preview changes to {providerName(selected.sourceProviderId)}
                </DropdownMenu.Item>
              {/if}
              <DropdownMenu.Separator />
              <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !details?.unresolvedCount} onSelect={() => void exportMissingSongs("csv")}>Export missing songs as CSV</DropdownMenu.Item>
              <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !details?.unresolvedCount} onSelect={() => void exportMissingSongs("json")}>Export missing songs as JSON</DropdownMenu.Item>
              <DropdownMenu.Separator />
              <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => settingsOpen = true}>Edit settings</DropdownMenu.Item>
              <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void run("toggle")}>{selected.enabled ? "Pause" : "Resume"}</DropdownMenu.Item>
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu.Root>
      {/if}{/snippet}
      {#snippet artwork()}<MediaArtwork class="hero-art" url={details?.artworkUrl} loading="eager" />{/snippet}
      {#snippet footer()}{#if selected}<div class="playlist-panel-footer">{#if isServiceMix(selected) || canRemovePlaylistFromApps(selected)}<Button variant="destructive-text" onclick={() => confirmRemoval(selected)}>Remove from my apps</Button>{/if}<div>{#if canRebuildInJellyfin(selected)}<Button variant="outline" disabled={Boolean(action)} onclick={() => confirmRebuild(selected)}>Rebuild…</Button>{/if}<Button disabled={Boolean(action) || !selected.enabled || !details} onclick={() => void run("sync")}>Sync now</Button></div></div>{/if}{/snippet}
      {#if detailLoading && !details}
        <RowsSkeleton count={5} />
      {:else if detailError && !details}
        <div class="compact-empty playlist-projection-empty" role="alert">
          <strong>Playlist details are unavailable</strong>
          <p>{detailError}</p>
          <Button variant="secondary" onclick={() => void loadDetails(selectedId)}>Try again</Button>
        </div>
      {:else if details && selected}
        <div class="playlist-panel-tools">
          {#if hasCopy(selected)}<p class="playlist-copy-note">Your copy · You edited this playlist, so you have your own copy. The original keeps syncing for everyone else.</p>{/if}
          <p class="hero-route">{providerName(details.sourceProviderId)} → {targetName} · {listeningTime(details.durationMs)}</p>
        </div>
        {#if details.lastRunCode === "backend_access_missing"}
          <div class="degraded-banner" role="status"><span aria-hidden="true">!</span><p>Restore media-server access to resume playlist sync and library scans.</p><Button variant="secondary" href="#/settings/people">Open media-server access</Button></div>
        {/if}
        {@const editingWarning = jellyfinPlaylistWarning(selected, providerName(selected.sourceProviderId))}
        {#if editingWarning}<div class="playlist-warning" role="status"><strong>{consent?.needsSignIn ? "Saved Jellyfin sign-in expired" : editingWarning}.</strong> {#if consent?.granted && !consent.needsSignIn && selected.lastRunCode?.split(",").some(code => code.trim() === "order")}Nothing is missing; only the order is off. Fixing order on the next sync{#if details.schedule?.nextRunAt}{` · ${relativeTime(details.schedule.nextRunAt)}`}{/if}. <Button variant="text" size="sm" disabled={Boolean(action) || !selected.enabled} onclick={() => void run("sync")}>Fix now</Button>{:else}<Button variant="text" href="#/sources/services?section=playlist-editing">Allow editing</Button>{/if}</div>{/if}
        <div class="playlist-kpis"><div><strong>{details.trackCount}</strong><small>{countNoun(details.trackCount, "song")}</small></div><div><strong>{details.playableCount}</strong><small>playable</small></div><div><strong>{details.localCount ?? "—"}</strong><small>from your library</small></div></div>
        <div class="playlist-match-summary"><strong>{details.reviewCount + details.unresolvedCount ? countLabel(details.reviewCount + details.unresolvedCount, "song needs a look", "songs need a look") : `All ${countLabel(details.trackCount, "match is settled", "matches are settled")}`}</strong><span>{details.reviewCount + details.unresolvedCount ? "Review these songs in Matches." : "None of this playlist’s songs need a look."} <Button variant="text" size="sm" href="#/library/matches">Open in Matches</Button></span></div>
        {#key selected.id}<PlaylistSyncSettings playlist={selected} schedule={details.schedule ?? null} sourceName={providerName(selected.sourceProviderId)} {targetName} onPlaylist={value => playlists = playlists.map(item => item.id === value.id ? value : item)} onSchedule={(value, id) => { if (details?.id === id) details = { ...details, schedule: value }; }} />{/key}
        <dl class="playlist-panel-facts"><dt>Last synced</dt><dd>{relativeTime(selected.lastRunAt, "Not yet")}</dd><dt>Source</dt><dd>{providerName(selected.sourceProviderId)}</dd></dl>
        <AdvancedFold placement="flush" class="playlist-more" label={`Songs and activity · ${countLabel(details.trackCount, "song")}`} bind:open={songsOpen}>
        {#if exportError}<p class="field-error" role="alert">{exportError}</p>{/if}

        <section class="playlist-overview" aria-label="Playlist summary">
          {#if details.reviewCount > 0}
            <div>
              <button
                type="button"
                class="review-filter-button"
                class:active={routeFilter === "review"}
                aria-label={`Show ${countLabel(details.reviewCount, "track")} needing review`}
                onclick={() => routeFilter = "review"}
              >{details.reviewCount} to review</button>
            </div>
          {/if}

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
                {#if details}{#await import("$lib/components/OperationConsole.svelte") then module}<module.default
                  playlistName={details.name}
                  requestedJobId={operationJobId}
                  onTerminal={refresh}
                />{/await}{/if}
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
              class="select-trigger track-column-trigger"
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
        </AdvancedFold>
      {:else}<EmptyState title="Select a playlist" />{/if}
    </SidePanel>
  </section>
{/if}

{#if settingsOpen}{#await import("$lib/components/PlaylistSettingsDialog.svelte") then module}<module.default
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
/>{/await}{/if}

{#if sourceUpdateOpen}{#await import("$lib/shelved/PlaylistSourceUpdateDialog.svelte") then module}<module.default
  bind:open={sourceUpdateOpen}
  playlist={selected ?? null}
  providerName={providerName(selected?.sourceProviderId)}
  targetName={providerName(selected?.targetProtocol)}
  onQueued={sourceUpdateQueued}
/>{/await}{/if}
{#if rematchOpen}{#await import("$lib/components/BulkRematchDialog.svelte") then module}<module.default kind="playlist" bind:open={rematchOpen} onQueued={sourceUpdateQueued} />{/await}{/if}

{#if LoadedAddDialog}<LoadedAddDialog bind:open={addOpen} {providers} existingPlaylists={playlists} onSaved={playlistAdded} />{/if}
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

<ConfirmDialog bind:open={() => Boolean(removingMix), value => { if (!value) removingMix = null; }} title={`Remove ${removingMix?.name ?? "this playlist"} from your apps?`} description={`${removingMix?.name ?? "This playlist"} leaves your apps for you. Its source is unchanged. Saved matches stay, so adding it back is quick.`} confirmLabel="Remove from my apps" disabled={removing} closeOnConfirm={false} error={removeError} onConfirm={removeMix} />
<PlaylistRemovalDialog
  bind:open={() => Boolean(removingPlaylist), value => { if (!value) { removingPlaylist = null; removalPreview = null; removalPreviewError = ""; removalPreviewRequest++; removalPreviewLoading = false; } }}
  name={removingPlaylist?.name ?? "this playlist"}
  preview={removalPreview}
  loading={removalPreviewLoading}
  previewError={removalPreviewError}
  disabled={removing}
  error={removeError}
  onRetry={loadRemovalPreview}
  onConfirm={removePlaylist}
/>
<ConfirmDialog
  bind:open={() => Boolean(rebuildingPlaylist), value => { if (!value && !rebuilding) rebuildingPlaylist = null; }}
  title="Rebuild this playlist in Jellyfin?"
  description="Allstarr makes the playlist again in the right order and removes the old copy. Apps see it as a new playlist, so pins, favourites and shortcuts to the old one stop working."
  confirmLabel={rebuilding ? "Rebuilding…" : "Rebuild"}
  confirmVariant="default"
  disabled={rebuilding}
  closeOnConfirm={false}
  error={rebuildError}
  onConfirm={rebuildPlaylist}
/>
<style>
  .library-playlists { min-width: 0; }.library-list { min-width: 0; }
  .library-heading, .library-actions, .library-search { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
  .playlist-group { display: flex; align-items: baseline; flex-wrap: wrap; gap: var(--space-2); margin: 0; padding: var(--space-4) var(--space-4) var(--space-1); font-size: var(--text-sm); font-weight: 600; }
  .playlist-group:not(:first-child) { border-top: 1px solid var(--color-edge); }.playlist-group small { color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 400; }
  .library-heading { justify-content: space-between; margin-bottom: var(--space-3); }.library-heading h2 { margin: 0; font-size: var(--text-section); }
  .library-filters { display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-3); margin-block: var(--space-4); }.library-search { flex: 1 1 360px; min-width: 0; flex-wrap: nowrap; }.library-search > :global(:first-child) { min-width: 0; flex: 1 1 0; }.library-search :global(.select-root) { flex: 0 0 auto; }.library-search :global(.select-trigger) { width: auto; gap: var(--space-2); padding-inline: var(--space-2); font-size: .8125rem; white-space: nowrap; }
  .playlist-row { display: block; min-height: 0; min-width: 0; padding: 0; border: 0; }.playlist-panel-tools { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-2); }.hero-route, .playlist-copy-note { margin: 0; font-size: var(--text-xs); color: var(--color-ink-muted); }
  .playlist-warning { margin-block: var(--space-3); border-radius: var(--radius-md); padding: var(--space-3); background: color-mix(in srgb, var(--warning) 12%, var(--color-panel)); color: var(--warning); font-size: var(--text-sm); }
  .playlist-kpis { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--space-2); margin-block: var(--space-4); }.playlist-kpis > div { background: var(--color-panel-raised); border-radius: var(--radius-md); padding: var(--space-3); }.playlist-kpis strong { display: block; font-size: 1.35rem; }.playlist-kpis small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .playlist-match-summary { display: grid; justify-items: start; border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); margin-bottom: var(--space-5); font-size: var(--text-sm); }
  .playlist-panel-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--space-3); margin-block: var(--space-5); font-size: var(--text-xs); }.playlist-panel-facts dt { color: var(--color-ink-muted); }.playlist-panel-facts dd { text-align: right; margin: 0; }
  :global(.playlist-more) { --detail-gutter: 0px; min-width: 0; max-width: 100%; }
  :global(.playlist-more) .playlist-facts { display: grid; grid-template-columns: 1fr; }
  :global(.playlist-more) .track-toolbar { display: flex; min-width: 0; align-items: center; flex-wrap: wrap; }
  .track-toolbar :global(.search-field) { flex: 1 1 200px; }
  .track-toolbar :global(.select-root) { flex: none; }
  .track-toolbar :global(.select-trigger) { width: auto; flex: none; white-space: nowrap; }
  :global(.playlist-more) .track-table, :global(.playlist-more) .track-scroll { width: 100%; min-width: 0; max-width: 100%; }
  :global(.playlist-more) .track-scroll { max-height: min(60dvh, 32rem); overflow: auto; scrollbar-gutter: stable; }
  @container (min-width: 800px) {
    .track-data-table { min-width: 100%; }
  }
  .playlist-panel-footer { display: flex; justify-content: space-between; align-items: center; gap: var(--space-2); width: 100%; min-width: 0; }.playlist-panel-footer > div { display: flex; align-items: center; gap: var(--space-2); margin-left: auto; }.playlist-panel-footer :global([data-slot="button"]) { padding-inline: var(--space-2); font-size: .75rem; }
  :global(.library-danger) { color: var(--danger); }
  @media (max-width: 899px) { .playlist-group { padding-inline: var(--space-3); }.library-search { flex-wrap: wrap; }.library-search > :global(:first-child) { flex-basis: 100%; }:global(.playlist-more) :global(.review-filter-button), :global(.playlist-more) :global(.schedule-edit-button), :global(.playlist-more) :global(.track-menu-trigger) { min-width: 44px; min-height: 44px; } }
  @media (max-width: 380px) { .playlist-panel-footer :global([data-slot="button"]) { padding-inline: var(--space-1); font-size: .6875rem; } }

</style>
