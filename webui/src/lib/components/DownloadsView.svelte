<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { ChevronDown } from "@lucide/svelte";
  import { adminUrl } from "$lib/admin-url";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import {
    downloads,
    home,
    settings,
    type DownloadsResponse,
    type ManagedDownload,
    type KeptCopyDeletionPreview,
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
  import { newReportRequestId as newRequestId } from "$lib/support";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  let { storage }: { storage: "cache" | "kept" } = $props();

  type Removal = { kind: "one"; file: ManagedDownload } | { kind: "release"; file: ManagedDownload } | { kind: "all" }
    | { kind: "delete-kept"; file: ManagedDownload; preview: KeptCopyDeletionPreview; requestId: string };

  let data = $state<DownloadsResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let query = $state("");
  let providerFilter = $state("");
  let sort = $state<DownloadSort>("track");
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let action = $state("");
  let removal = $state<Removal | null>(null);
  let confirmOpen = $state(false);
  let removalError = $state("");
  let storageMode = $state("Cache");
  let cacheDurationHours = $state(72);
  let transcodeCacheMinutes = $state(60);
  let controlsReady = $state(false);
  let controlsError = $state("");
  let cacheControlsDirty = $state(false);

  const cached = $derived(storage === "cache");
  const label = $derived(cached ? "Cached" : "Kept");
  const visible = $derived(filterDownloads(data?.files ?? [], query, providerFilter, sort));
  const explicitKeepCount = $derived((data?.files ?? []).filter((file) => file.removable && file.explicitlyKept).length);
  const legacyRemovalCount = $derived((data?.files ?? []).filter((file) => file.removable && !file.explicitlyKept).length);
  const bulkActionCount = $derived(explicitKeepCount + legacyRemovalCount);
  const bulkDescription = $derived.by(() => {
    const actions: string[] = [];
    if (explicitKeepCount) actions.push(`release ${explicitKeepCount} explicit Keep${explicitKeepCount === 1 ? "" : "s"}`);
    if (legacyRemovalCount) actions.push(`delete ${legacyRemovalCount} unreferenced ${label.toLowerCase()} track${legacyRemovalCount === 1 ? "" : "s"}`);
    if (!actions.length) actions.push("make no changes");
    const diagnosticCount = data?.diagnosticCount ?? 0;
    return `This ${actions.join(" and ")}. Other household interests remain protected. If none remain, the file enters a 24-hour grace period before cleanup. ${diagnosticCount} diagnostic file${diagnosticCount === 1 ? "" : "s"} remain${diagnosticCount === 1 ? "s" : ""} untouched.`;
  });
  const availableProviders = $derived(
    [...new Set((data?.files ?? [])
      .map((file) => file.provider)
      .filter((value): value is string => Boolean(value)))].toSorted(),
  );

  function qualityWarning(file: ManagedDownload) {
    const check = (file as ManagedDownload & { qualityCheck?: { verdict: string; likelySourceRate?: number | null } }).qualityCheck;
    if (check?.verdict === "upsampled") return { label: "Likely upscaled", detail: `The measured bandwidth suggests a ${check.likelySourceRate === 48000 ? "48" : "44.1"} kHz source in this higher-rate file.` };
    if (check?.verdict === "padded-bit-depth") return { label: "Padded", detail: "The low sample bits suggest 16-bit audio padded to 24-bit." };
    if (check?.verdict === "lossy-transcode-suspected") return { label: "Lossy source?", detail: "A sharp high-frequency cutoff suggests a lossy source inside this lossless file." };
    return null;
  }

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
      if (!cacheControlsDirty) {
        storageMode = String(pathValue(configResult.value, "library.storageMode") ?? "Cache");
        cacheDurationHours = Number(pathValue(configResult.value, "library.cacheDurationHours") ?? 72);
        transcodeCacheMinutes = Number(pathValue(configResult.value, "cache.transcodeCacheMinutes") ?? 60);
      }
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
      const result = await downloads.keep(file.path);
      toast.success(result.message || `${file.title || file.fileName} queued for Kept.`);
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The track could not be kept.");
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
      cacheControlsDirty = false;
      toast.success("Track cache settings saved.");
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Track cache settings could not be saved.");
    } finally {
      action = "";
    }
  }

  function confirm(next: Removal) {
    removalError = "";
    removal = next;
    confirmOpen = true;
  }

  async function previewDeletion(file: ManagedDownload) {
    if (action) return;
    action = "preview-delete";
    try {
      const preview = await downloads.previewKeptDeletion(file.path);
      confirm({ kind: "delete-kept", file, preview, requestId: newRequestId() });
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The kept copy could not be checked. Refresh and retry.");
    } finally {
      action = "";
    }
  }

  async function remove() {
    if (!removal || action) return;
    action = "remove";
    removalError = "";
    try {
      if (removal.kind === "delete-kept") {
        await downloads.deleteKeptCopy(removal.preview, removal.requestId);
        toast.success("Kept copy deletion queued.");
      } else if (removal.kind === "all") {
        const result = await downloads.removeAll(storage);
        toast.success(result.message || `${result.deletedCount} indexed ${label.toLowerCase()} track${result.deletedCount === 1 ? "" : "s"} removed${result.skippedUnknown ? `; ${result.skippedUnknown} diagnostic file${result.skippedUnknown === 1 ? "" : "s"} skipped` : ""}${result.skippedReferenced ? `; ${result.skippedReferenced} referenced file${result.skippedReferenced === 1 ? "" : "s"} protected` : ""}.`);
      } else {
        const result = await downloads.remove(removal.file.path, storage);
        toast.success(result.message || (removal.kind === "release"
          ? "Keep released. Other interests remain protected."
          : `${removal.file.title || removal.file.fileName} removed.`));
      }
      confirmOpen = false;
      await refresh();
    } catch (cause) {
      removalError = cause instanceof Error ? cause.message : "Removal failed. Refresh and retry.";
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
      <Button variant="secondary" size="sm" disabled={refreshing} onclick={() => void refresh()}>{refreshing ? "Trying again…" : "Retry"}</Button>
    </div>
  {/if}

  <section class="panel downloads-panel" aria-busy={refreshing}>
    <header class="panel-heading playlist-toolbar downloads-heading">
      <div>
        <h2>{label} songs</h2>
        <p>{cached
          ? storageMode === "Cache"
            ? "Songs saved after streaming so they start faster next time. Keep a song to hold on to it permanently."
            : "Playback caching is off. Hearts and Keep still save permanent copies."
          : "Songs Allstarr keeps permanently, even when the cache is cleaned."}</p>
      </div>
      <div class="downloads-summary" aria-label={`${label} totals`}>
        <span><small>Indexed</small><strong>{data.managedCount}</strong></span>
        <span><small>Diagnostics</small><strong>{data.diagnosticCount}</strong></span>
        <span><small>Size</small><strong>{data.totalSizeFormatted}</strong></span>
      </div>
      <div class="panel-heading-actions downloads-heading-actions">
        <RefreshButton variant="secondary" onrefresh={refresh} />
        {#if bulkActionCount}
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



    {#if data.files.length}
      <div class="download-table" role="table" aria-label={`${label} tracks`}>
        <div class="download-head" role="row">
          <span role="columnheader">Track</span>
          <span role="columnheader">Provider</span>
          <span role="columnheader">Quality</span>
          <span role="columnheader">Lifecycle</span>
          <span role="columnheader">File</span>
          <span role="columnheader">Actions</span>
        </div>
        <div class="download-rows">
          {#each visible as file (file.storage + file.path)}
            {@const warning = qualityWarning(file)}
            <div class="download-row" role="row">
              <span class="download-track" role="cell">
                <span class="media-art download-art">
                  <ProviderMark id={file.provider || "file"} definition={provider(file.provider)} label={providerName(file.provider)} />
                  {#if file.artworkUrl}
                    <img src={adminUrl(file.artworkUrl)} alt="" loading="lazy" onerror={(event) => event.currentTarget.remove()} />
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
                <span class="download-quality-summary">
                  <strong>{file.quality || file.codec}</strong>
                  {#if warning}<span class="quality-check-badge" title={warning.detail}>{warning.label}</span>{/if}
                </span>
                <small>{qualityDetails(file).join(" · ")}</small>
              </span>
              <span class="download-lifecycle" role="cell">
                <Badge state={file.removable ? "healthy" : "suggested"}>{file.publicationState === "Imported" ? "Imported original · read-only" : file.publicationState}</Badge>
                <small>Last access {relativeTime(file.lastAccessedAt)}</small>
                {#if file.expiresAt}<small>Expires {relativeTime(file.expiresAt)}</small>{/if}
                {#if file.referenceCount != null}<small>{file.referenceCount} reference{file.referenceCount === 1 ? "" : "s"}</small>{/if}
              </span>
              <span class="download-file" role="cell" aria-label={`Size ${file.sizeFormatted}. Updated ${relativeTime(file.lastModified)}`}>
                <strong>{file.sizeFormatted}</strong>
                <small>Updated <time datetime={file.lastModified}>{relativeTime(file.lastModified)}</time></small>
              </span>
              <span class="download-actions" role="cell">
                <Button variant="secondary" href={downloads.fileUrl(file.path, storage)}>Download</Button>
                {#if !cached && file.canDeleteKeptCopy}
                  <Button variant="destructive" disabled={Boolean(action)} onclick={() => void previewDeletion(file)}>Delete kept copy…</Button>
                {/if}
                {#if file.removable}
                  {#if file.explicitlyKept}
                    <Button disabled={Boolean(action)} onclick={() => confirm({ kind: "release", file })}>Release Keep</Button>
                  {:else}
                    {#if cached}<Button disabled={Boolean(action)} onclick={() => void keep(file)}>Keep</Button>{/if}
                    <Button variant="destructive" disabled={Boolean(action)} onclick={() => confirm({ kind: "one", file })}>Remove</Button>
                  {/if}
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
            ? "Tracks appear when their background download finishes, even if you skip the song. Incomplete or failed files are not listed."
            : "New playback is not being added to Cached. Enable playback caching below to save temporary copies."
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
          <Badge state={storageMode === "Cache" ? "healthy" : "suggested"}>Playback cache {storageMode === "Cache" ? "on" : "off"}</Badge>
          <ChevronDown class="disclosure-chevron" size={16} aria-hidden="true" />
        </summary>
        {#if controlsReady}
          <form class="settings-fields track-cache-settings" onsubmit={(event) => void saveCacheControls(event)}>
            <label class="setting-field">
              <span><strong>Playback caching</strong></span>
              <SelectField bind:value={storageMode} name="STORAGE_MODE" label="Playback caching" options={[{ value: "Cache", label: "Cache played songs" }, { value: "Permanent", label: "Do not cache playback" }]} onchange={() => cacheControlsDirty = true} />
              <small>Hearts, Keep and retained playlists protect their copies in either mode.</small>
            </label>
            <label class="setting-field">
              <span><strong>Track retention</strong><small>Hours</small></span>
              <input bind:value={cacheDurationHours} name="CACHE_DURATION_HOURS" type="number" min="1" max="8760" oninput={() => cacheControlsDirty = true} />
              <small>Hours since last use. The size limit can evict cached tracks sooner.</small>
            </label>
            <label class="setting-field">
              <span><strong>Quality override retention</strong><small>Minutes</small></span>
              <input bind:value={transcodeCacheMinutes} name="CACHE_TRANSCODE_MINUTES" type="number" min="1" max="10080" oninput={() => cacheControlsDirty = true} />
              <small>Temporary lower-bandwidth versions use this shorter window.</small>
            </label>
            <footer><Button type="submit" disabled={Boolean(action) || !cacheControlsDirty}>{action === "cache-settings" ? "Saving…" : "Save track cache"}</Button></footer>
          </form>
        {:else}
          <p class="inline-status" role="status">{controlsError || "Loading track cache settings…"}</p>
        {/if}
      </details>
    {/if}
  </section>

  <ConfirmDialog
    bind:open={confirmOpen}
    title={removal?.kind === "delete-kept" ? `Delete the kept copy of ${removal.preview.title}?` : removal?.kind === "all"
      ? `Remove all ${label.toLowerCase()} tracks?`
      : removal?.kind === "release" ? "Release this Keep?" : "Remove this track?"}
    description={removal?.kind === "delete-kept"
      ? `This removes the audio and lyrics for everyone in this household. ${removal.preview.favoriteReferences} favorite${removal.preview.favoriteReferences === 1 ? "" : "s"}, ${removal.preview.playlistReferences} playlist reference${removal.preview.playlistReferences === 1 ? "" : "s"}, and ${removal.preview.keepReferences} explicit Keep${removal.preview.keepReferences === 1 ? "" : "s"}${removal.preview.otherReferences ? `, and ${removal.preview.otherReferences} other reference${removal.preview.otherReferences === 1 ? "" : "s"}` : ""} currently protect it. Hearts and playlist entries stay. This ends offline playback from this copy; the song will stream or download again when played online. A new Keep can save it again.`
      : removal?.kind === "all"
      ? bulkDescription
      : removal?.kind === "release"
        ? "This releases your Keep. Other household interests stay protected. If none remain, the file enters a 24-hour grace period before cleanup."
        : "This deletes the indexed audio file and its lyrics sidecar. This cannot be undone."}
    confirmLabel={removal?.kind === "delete-kept" ? "Delete kept copy" : removal?.kind === "all" ? "Remove all" : removal?.kind === "release" ? "Release Keep" : "Remove track"}
    disabled={Boolean(action)}
    closeOnConfirm={false}
    error={removalError}
    onConfirm={remove}
  />
{/if}

<style>
  .download-quality-summary {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 0.375rem;
    min-width: 0;
  }

  .quality-check-badge {
    display: inline-flex;
    align-self: start;
    max-width: 100%;
    padding: 0.125rem 0.5rem;
    border: 1px solid var(--md-sys-color-outline-variant);
    border-radius: 0.5rem;
    background: var(--md-sys-color-surface-container);
    color: var(--md-sys-color-on-surface-variant);
    font-size: 0.6875rem;
    line-height: 1.25rem;
    font-weight: 500;
  }
</style>
