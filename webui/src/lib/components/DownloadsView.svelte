<script lang="ts">
  import { onMount } from "svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import {
    downloads,
    home,
    settings,
    type DownloadsResponse,
    type ManagedDownload,
    type ProviderDefinition,
  } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import {
    filterDownloads,
    qualityDetails,
    type DownloadSort,
  } from "$lib/downloads";
  import { formatDuration } from "$lib/playlists";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { pathValue } from "$lib/settings";
  import { relativeTime } from "$lib/activity";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  let { storage }: { storage: "cache" | "kept" } = $props();

  type Removal = { kind: "one"; file: ManagedDownload } | { kind: "all" };

  let data = $state<DownloadsResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let query = $state("");
  let providerFilter = $state("");
  let sort = $state<DownloadSort>("track");
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let feedback = $state("");
  let action = $state("");
  let removal = $state<Removal | null>(null);
  let confirmOpen = $state(false);
  let storageMode = $state("Cache");
  let cacheDurationHours = $state(24);
  let transcodeCacheMinutes = $state(60);
  let controlsReady = $state(false);
  let controlsError = $state("");

  const cached = $derived(storage === "cache");
  const label = $derived(cached ? "Cached" : "Kept");
  const visible = $derived(filterDownloads(data?.files ?? [], query, providerFilter, sort));
  const removableCount = $derived((data?.files ?? []).filter((file) => file.removable).length);
  const referencedCount = $derived((data?.files ?? []).filter((file) => file.publicationState === "Referenced").length);
  const availableProviders = $derived(
    [...new Set((data?.files ?? [])
      .map((file) => file.provider)
      .filter((value): value is string => Boolean(value)))].toSorted(),
  );

  const provider = (providerId?: string | null) => findProviderDefinition(providers, providerId);
  const providerName = (providerId?: string | null) => providerDisplayName(providers, providerId);

  function cacheType(storage: string) {
    return storage === "transcoded" ? "Quality override" : "Track cache";
  }

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const [downloadsResult, schemaResult, configResult] = await Promise.allSettled([
      downloads.list(storage),
      home.schema(),
      cached ? settings.config() : Promise.resolve(null),
    ]);
    if (downloadsResult.status === "fulfilled") data = downloadsResult.value;
    else error = downloadsResult.reason instanceof Error
      ? downloadsResult.reason.message
      : `${label} tracks are unavailable.`;
    if (schemaResult.status === "fulfilled") providers = schemaResult.value.providers;
    if (cached && configResult.status === "fulfilled" && configResult.value) {
      storageMode = String(pathValue(configResult.value, "library.storageMode") ?? "Cache");
      cacheDurationHours = Number(pathValue(configResult.value, "library.cacheDurationHours") ?? 24);
      transcodeCacheMinutes = Number(pathValue(configResult.value, "cache.transcodeCacheMinutes") ?? 60);
      controlsReady = true;
      controlsError = "";
    } else if (cached && configResult.status === "rejected") {
      controlsError = configResult.reason instanceof Error
        ? configResult.reason.message
        : "Track cache settings are unavailable.";
    }
    loading = false;
    refreshing = false;
  }

  const refreshScheduler = createRefreshScheduler(refresh);
  const scheduleRefresh = refreshScheduler.schedule;

  async function keep(file: ManagedDownload) {
    if (action) return;
    action = file.path;
    try {
      await downloads.keep(file.path);
      feedback = `${file.title || file.fileName} moved to Kept.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The track could not be kept.";
    } finally {
      action = "";
    }
  }

  async function saveCacheControls(event: SubmitEvent) {
    event.preventDefault();
    if (action) return;
    action = "cache-settings";
    try {
      await settings.save({
        STORAGE_MODE: storageMode,
        CACHE_DURATION_HOURS: String(cacheDurationHours),
        CACHE_TRANSCODE_MINUTES: String(transcodeCacheMinutes),
      });
      feedback = "Track cache settings saved.";
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Track cache settings could not be saved.";
    } finally {
      action = "";
    }
  }

  function confirm(next: Removal) {
    removal = next;
    confirmOpen = true;
  }

  async function remove() {
    if (!removal || action) return;
    action = "remove";
    try {
      if (removal.kind === "all") {
        const result = await downloads.removeAll(storage);
        feedback = `${result.deletedCount} indexed ${label.toLowerCase()} track${result.deletedCount === 1 ? "" : "s"} removed${result.skippedUnknown ? `; ${result.skippedUnknown} diagnostic file${result.skippedUnknown === 1 ? "" : "s"} skipped` : ""}${result.skippedReferenced ? `; ${result.skippedReferenced} referenced file${result.skippedReferenced === 1 ? "" : "s"} protected` : ""}.`;
      } else {
        await downloads.remove(removal.file.path, storage);
        feedback = `${removal.file.title || removal.file.fileName} removed.`;
      }
      confirmOpen = false;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Removal failed.";
    } finally {
      action = "";
    }
  }

  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
    };
  });
</script>

{#if loading}
  <Skeleton class="panel downloads-panel skeleton-panel" aria-label={`Loading ${label} tracks`} aria-busy="true" />
{:else if error && !data}
  <RouteError
    eyebrow={`${label} tracks unavailable`}
    title="Allstarr could not inspect managed audio."
    message={error}
    onRetry={refresh}
  />
{:else if data}
  {#if error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Managed audio may be stale.</strong> {error}</p>
      <Button variant="secondary" size="sm" onclick={() => void refresh()}>Retry</Button>
    </div>
  {/if}

  <section class="panel downloads-panel" aria-busy={refreshing}>
    <header class="playlist-toolbar downloads-heading">
      <div>
        <p class="eyebrow">Library storage</p>
        <h2>{label} tracks</h2>
        <p>{cached
          ? storageMode === "Cache"
            ? "Completed provider streams and temporary quality overrides. Keep a track to retain it permanently."
            : "Automatic track caching is off. Managed downloads are retained permanently instead."
          : "Permanent downloads retained across playback-cache cleanup."}</p>
      </div>
      <div class="downloads-summary" aria-label={`${label} totals`}>
        <span><small>Indexed</small><strong>{data.managedCount}</strong></span>
        <span><small>Diagnostics</small><strong>{data.diagnosticCount}</strong></span>
        <span><small>Size</small><strong>{data.totalSizeFormatted}</strong></span>
      </div>
      <div class="downloads-heading-actions">
        <Button variant="secondary" onclick={() => void refresh()}>Refresh</Button>
        {#if removableCount}
          <Button variant="destructive" onclick={() => confirm({ kind: "all" })}>Remove all</Button>
        {/if}
      </div>
    </header>

    <div class="playlist-filters downloads-filters">
      <SearchField bind:value={query} label={`Filter ${label.toLowerCase()} tracks`} placeholder="Track, artist, album, or provider" hiddenLabel />
      <SelectField bind:value={providerFilter} label="Provider" options={[
        { value: "", label: "All providers" },
        ...availableProviders.map((value) => ({ value, label: providerName(value) })),
      ]} />
      <SelectField bind:value={sort} label="Sort tracks" options={[
        { value: "track", label: "Track" }, { value: "provider", label: "Provider" },
        { value: "quality", label: "Quality" }, { value: "size", label: "Size" },
        { value: "updated", label: "Newest" },
      ]} />
    </div>

    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

    {#if data.files.length}
      <div class="download-table" role="table" aria-label={`${label} tracks`}>
        <div class="download-head" role="row">
          <span role="columnheader">Track</span>
          <span role="columnheader">Provider</span>
          <span role="columnheader">Format</span>
          <span role="columnheader">Lifecycle</span>
          <span role="columnheader">Size</span>
          <span role="columnheader">Updated</span>
          <span role="columnheader">Actions</span>
        </div>
        <div class="download-rows">
          {#each visible as file (file.storage + file.path)}
            <div class="download-row" role="row">
              <span class="download-track" role="cell">
                <span class="media-art download-art">
                  <ProviderMark id={file.provider || "file"} definition={provider(file.provider)} label={providerName(file.provider)} />
                  {#if file.artworkUrl}
                    <img src={file.artworkUrl} alt="" loading="lazy" onerror={(event) => event.currentTarget.remove()} />
                  {/if}
                </span>
                <span>
                  <strong>{file.title || file.fileName}</strong>
                  <small>{file.artist || "Unknown artist"}{file.album ? ` · ${file.album}` : ""}</small>
                  <small>{formatDuration(file.durationMilliseconds)}{cached ? ` · ${cacheType(file.storage)}` : ""}</small>
                </span>
              </span>
              <span class="download-provider" role="cell">
                <ProviderMark id={file.provider || "file"} definition={provider(file.provider)} label={providerName(file.provider)} />
                <span><strong>{providerName(file.provider)}</strong>{#if file.externalId}<small>{file.externalId}</small>{/if}</span>
              </span>
              <span class="download-quality" role="cell">
                <strong>{file.quality || file.codec}</strong>
                <small>{qualityDetails(file).join(" · ")}</small>
              </span>
              <span class="download-lifecycle" role="cell">
                <Badge state={file.removable ? "healthy" : "suggested"}>{file.publicationState}</Badge>
                <small>Last access {relativeTime(file.lastAccessedAt)}</small>
                {#if file.expiresAt}<small>Expires {relativeTime(file.expiresAt)}</small>{/if}
                {#if file.referenceCount != null}<small>{file.referenceCount} reference{file.referenceCount === 1 ? "" : "s"}</small>{/if}
              </span>
              <span role="cell" aria-label={`Size ${file.sizeFormatted}`}>{file.sizeFormatted}</span>
              <span role="cell" aria-label={`Updated ${relativeTime(file.lastModified)}`}>
                <time datetime={file.lastModified}>{relativeTime(file.lastModified)}</time>
              </span>
              <span class="download-actions" role="cell">
                <Button variant="secondary" href={downloads.fileUrl(file.path, storage)}>Download</Button>
                {#if file.removable}
                  {#if cached}<Button disabled={Boolean(action)} onclick={() => void keep(file)}>Keep</Button>{/if}
                  <Button variant="destructive" disabled={Boolean(action)} onclick={() => confirm({ kind: "one", file })}>Remove</Button>
                {/if}
              </span>
            </div>
          {:else}
            <div class="compact-empty"><strong>No tracks match these filters</strong></div>
          {/each}
        </div>
      </div>
    {:else}
      <div class="compact-empty downloads-empty">
        <strong>No {label.toLowerCase()} tracks</strong>
        <p>{cached
          ? storageMode === "Cache"
            ? "A track appears after an external stream reaches the end. Interrupted and partial streams are not retained."
            : "Storage mode is Permanent, so managed downloads appear under Kept instead."
          : "Use Keep on a cached track to retain it permanently."}</p>
      </div>
    {/if}

    {#if cached}
      <details class="track-cache-controls">
        <summary>
          <span>
            <strong id="track-cache-controls-title">Track cache behavior</strong>
            <small>Playback storage and retention, next to the files they control.</small>
          </span>
          <Badge state={storageMode === "Cache" ? "healthy" : "suggested"}>{storageMode} mode</Badge>
        </summary>
        {#if controlsReady}
          <form class="settings-fields track-cache-settings" onsubmit={(event) => void saveCacheControls(event)}>
            <label class="setting-field">
              <span><strong>Storage mode</strong></span>
              <SelectField bind:value={storageMode} name="STORAGE_MODE" label="Storage mode" options={["Cache", "Permanent"]} />
              <small>Cache retains completed provider streams. Permanent sends managed downloads to Kept.</small>
            </label>
            <label class="setting-field">
              <span><strong>Track retention</strong><small>Hours</small></span>
              <input bind:value={cacheDurationHours} name="CACHE_DURATION_HOURS" type="number" min="1" max="8760" />
              <small>Completed cached tracks older than this may be removed.</small>
            </label>
            <label class="setting-field">
              <span><strong>Quality override retention</strong><small>Minutes</small></span>
              <input bind:value={transcodeCacheMinutes} name="CACHE_TRANSCODE_MINUTES" type="number" min="1" max="10080" />
              <small>Temporary lower-bandwidth versions use this shorter window.</small>
            </label>
            <footer><Button type="submit" disabled={Boolean(action)}>{action === "cache-settings" ? "Saving…" : "Save track cache"}</Button></footer>
          </form>
        {:else}
          <p class="action-feedback" role="status">{controlsError || "Loading track cache settings…"}</p>
        {/if}
      </details>
    {/if}
  </section>

  <ConfirmDialog
    bind:open={confirmOpen}
    title={removal?.kind === "all" ? `Remove all ${label.toLowerCase()} tracks?` : "Remove this track?"}
    description={removal?.kind === "all"
      ? `This deletes ${removableCount} unreferenced ${label.toLowerCase()} track${removableCount === 1 ? "" : "s"} and their lyrics sidecars. ${data.diagnosticCount} diagnostic and ${referencedCount} referenced file${data.diagnosticCount + referencedCount === 1 ? " is" : "s are"} left untouched.`
      : "This deletes the indexed audio file and its lyrics sidecar. This cannot be undone."}
    confirmLabel={removal?.kind === "all" ? "Remove all" : "Remove track"}
    onConfirm={remove}
  />
{/if}
