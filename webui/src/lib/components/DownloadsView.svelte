<script lang="ts">
  import { onMount } from "svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    downloads,
    home,
    type DownloadsResponse,
    type ManagedDownload,
    type ProviderDefinition,
  } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import {
    filterDownloads,
    qualityDetails,
    type DownloadSort,
  } from "$lib/downloads";
  import { formatDuration } from "$lib/playlists";
  import { liveUpdates } from "$lib/live-updates.svelte";

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
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  const cached = $derived(storage === "cache");
  const label = $derived(cached ? "Cached" : "Kept");
  const visible = $derived(filterDownloads(data?.files ?? [], query, providerFilter, sort));
  const availableProviders = $derived(
    [...new Set((data?.files ?? [])
      .map((file) => file.provider)
      .filter((value): value is string => Boolean(value)))].toSorted(),
  );

  function provider(providerId?: string | null) {
    return providers.find((item) => item.id.toLowerCase() === providerId?.toLowerCase());
  }

  function providerName(providerId?: string | null) {
    return providerId ? (provider(providerId)?.name ?? providerId) : "Unknown source";
  }

  function relativeTime(value: string) {
    const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1_000);
    const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
    const minutes = Math.round(seconds / 60);
    if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
    return formatter.format(Math.round(hours / 24), "day");
  }

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const [downloadsResult, schemaResult] = await Promise.allSettled([
      downloads.list(storage),
      home.schema(),
    ]);
    if (downloadsResult.status === "fulfilled") data = downloadsResult.value;
    else error = downloadsResult.reason instanceof Error
      ? downloadsResult.reason.message
      : `${label} tracks are unavailable.`;
    if (schemaResult.status === "fulfilled") providers = schemaResult.value.providers;
    loading = false;
    refreshing = false;
  }

  function scheduleRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void refresh();
    }, 250);
  }

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
        feedback = `${result.deletedCount} ${label.toLowerCase()} track${result.deletedCount === 1 ? "" : "s"} removed.`;
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
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if loading}
  <section class="panel downloads-panel skeleton-panel" aria-label={`Loading ${label} tracks`} aria-busy="true"></section>
{:else if error && !data}
  <section class="panel route-error" role="alert">
    <span aria-hidden="true">!</span>
    <div>
      <p class="eyebrow">{label} tracks unavailable</p>
      <h2>Allstarr could not inspect managed audio.</h2>
      <p>{error}</p>
    </div>
    <button class="button-secondary" type="button" onclick={() => void refresh()}>Try again</button>
  </section>
{:else if data}
  {#if error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Managed audio may be stale.</strong> {error}</p>
      <button type="button" onclick={() => void refresh()}>Retry</button>
    </div>
  {/if}

  <section class="panel downloads-panel" aria-busy={refreshing}>
    <header class="playlist-toolbar downloads-heading">
      <div>
        <p class="eyebrow">Library storage</p>
        <h2>{label} tracks</h2>
        <p>{cached
          ? "Temporary playback files. Keep a track to move it into permanent storage."
          : "Permanent downloads retained across playback-cache cleanup."}</p>
      </div>
      <div class="downloads-summary" aria-label={`${label} totals`}>
        <span><small>Tracks</small><strong>{data.count}</strong></span>
        <span><small>Size</small><strong>{data.totalSizeFormatted}</strong></span>
      </div>
      <div class="downloads-heading-actions">
        <button class="button-secondary" type="button" onclick={() => void refresh()}>Refresh</button>
        {#if data.files.length}
          <button class="button-danger" type="button" onclick={() => confirm({ kind: "all" })}>Remove all</button>
        {/if}
      </div>
    </header>

    <div class="playlist-filters downloads-filters">
      <label>
        <span class="sr-only">Filter {label.toLowerCase()} tracks</span>
        <input bind:value={query} type="search" placeholder="Track, artist, album, or provider" />
      </label>
      <label>
        <span class="sr-only">Provider</span>
        <select bind:value={providerFilter}>
          <option value="">All providers</option>
          {#each availableProviders as value}<option value={value}>{providerName(value)}</option>{/each}
        </select>
      </label>
      <label>
        <span class="sr-only">Sort tracks</span>
        <select bind:value={sort}>
          <option value="track">Track</option>
          <option value="provider">Provider</option>
          <option value="quality">Quality</option>
          <option value="size">Size</option>
          <option value="updated">Newest</option>
        </select>
      </label>
    </div>

    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

    {#if data.files.length}
      <div class="download-table" role="table" aria-label={`${label} tracks`}>
        <div class="download-head" role="row">
          <span role="columnheader">Track</span>
          <span role="columnheader">Provider</span>
          <span role="columnheader">Format</span>
          <span role="columnheader">Size</span>
          <span role="columnheader">Updated</span>
          <span role="columnheader">Actions</span>
        </div>
        <div class="download-rows">
          {#each visible as file (file.storage + file.path)}
            <div class="download-row" role="row">
              <span class="download-track" role="cell">
                <span class="media-art download-art">
                  {#if file.artworkUrl}<img src={file.artworkUrl} alt="" loading="lazy" />{:else}
                    <ProviderMark id={file.provider || "file"} definition={provider(file.provider)} label={providerName(file.provider)} />
                  {/if}
                </span>
                <span>
                  <strong>{file.title || file.fileName}</strong>
                  <small>{file.artist || "Unknown artist"}{file.album ? ` · ${file.album}` : ""}</small>
                  <small>{formatDuration(file.durationMilliseconds)}</small>
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
              <span role="cell" aria-label={`Size ${file.sizeFormatted}`}>{file.sizeFormatted}</span>
              <span role="cell" aria-label={`Updated ${relativeTime(file.lastModified)}`}>
                <time datetime={file.lastModified}>{relativeTime(file.lastModified)}</time>
              </span>
              <span class="download-actions" role="cell">
                <a class="button-secondary" href={downloads.fileUrl(file.path, storage)}>Download</a>
                {#if cached}<button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void keep(file)}>Keep</button>{/if}
                <button class="button-danger" type="button" disabled={Boolean(action)} onclick={() => confirm({ kind: "one", file })}>Remove</button>
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
          ? "Streamed and temporary provider downloads will appear here."
          : "Use Keep on a cached track to retain it permanently."}</p>
      </div>
    {/if}
  </section>

  <ConfirmDialog
    bind:open={confirmOpen}
    title={removal?.kind === "all" ? `Remove all ${label.toLowerCase()} tracks?` : "Remove this track?"}
    description={removal?.kind === "all"
      ? `This deletes every ${label.toLowerCase()} audio file and its lyrics sidecar.`
      : "This deletes the managed audio file and its lyrics sidecar. This cannot be undone."}
    confirmLabel={removal?.kind === "all" ? "Remove all" : "Remove track"}
    onConfirm={remove}
  />
{/if}
