<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { Download } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import LibraryDownloadRow from "$lib/components/LibraryDownloadRow.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import { writeFilter } from "$lib/components/patterns/helpers";
  import {
    downloads, home, settings, type DownloadsResponse, type ManagedDownload,
    type KeptCopyDeletionPreview, type ProviderDefinition,
  } from "$lib/api";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { filterDownloads, type DownloadSort } from "$lib/downloads";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { pathValue } from "$lib/settings";
  import { relativeTime } from "$lib/activity";
  import { newReportRequestId as newRequestId } from "$lib/support";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  let { storage }: { storage: "cache" | "kept" } = $props();
  type Removal = { kind: "one" | "release"; file: ManagedDownload } | { kind: "all" }
    | { kind: "delete-kept"; file: ManagedDownload; preview: KeptCopyDeletionPreview; requestId: string };
  type Sort = DownloadSort | "lifecycle";
  type SongGroup = { key: string; files: ManagedDownload[] };
  const id = $props.id();

  let data = $state<DownloadsResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let query = $state("");
  let providerFilter = $state("");
  let segment = $state("all");
  let sort = $state<Sort>("track");
  let reversed = $state(false);
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let action = $state("");
  let removal = $state<Removal | null>(null);
  let confirmOpen = $state(false);
  let removalError = $state("");
  let storageMode = $state("Cache");
  let cacheDurationHours = $state<number | null>(null);
  let movingPaths = $state<string[]>([]);
  let expanded = $state<string[]>([]);
  let panelOpen = $state(false);
  let selected = $state<ManagedDownload | null>(null);
  let SongPanel = $state<typeof import("./LibrarySongPanel.svelte").default | null>(null);

  const cached = $derived(storage === "cache");
  const label = $derived(cached ? "Cached" : "Kept");
  const allFiles = $derived(data?.files ?? []);
  const allGroups = $derived(groupSongs(allFiles));
  const leavingSoonCount = $derived(allFiles.filter(leavingSoon).length);
  const lowerQualityCount = $derived(allFiles.filter(lowerQuality).length);
  const yourKeepsCount = $derived(allFiles.filter(file => file.explicitlyKept).length);
  const visible = $derived.by(() => {
    let files = filterDownloads(allFiles, query, providerFilter, sort === "lifecycle" ? "track" : sort)
      .filter(file => segment === "all" || (segment === "leaving" && leavingSoon(file)) ||
        (segment === "lower" && lowerQuality(file)) || (segment === "explicit" && file.explicitlyKept));
    if (sort === "lifecycle") files = files.toSorted((left, right) => cached
      ? expiry(left) - expiry(right)
      : Number(right.explicitlyKept) - Number(left.explicitlyKept) || (right.referenceCount ?? 0) - (left.referenceCount ?? 0));
    return reversed ? files.toReversed() : files;
  });
  const visibleGroups = $derived.by(() => {
    const groups = groupSongs(visible);
    if (sort !== "size") return groups;
    return groups.toSorted((left, right) => {
      const difference = right.files.reduce((sum, file) => sum + file.size, 0) - left.files.reduce((sum, file) => sum + file.size, 0);
      return reversed ? -difference : difference;
    });
  });
  const navigationFiles = $derived(visibleGroups.flatMap(group => group.files));
  const selectedFile = $derived(allFiles.find(file => file.path === selected?.path) ?? selected);
  const selectedIndex = $derived(navigationFiles.findIndex(file => file.path === selectedFile?.path));
  const removableFiles = $derived(allFiles.filter(file => file.removable));
  const explicitKeepCount = $derived(removableFiles.filter(file => file.explicitlyKept).length);
  const legacyRemovalCount = $derived(removableFiles.length - explicitKeepCount);
  const bulkActionCount = $derived(removableFiles.length);
  const bulkSize = $derived(removableFiles.length === allFiles.length
    ? data?.totalSizeFormatted ?? "0 B" : formatSize(removableFiles.reduce((sum, file) => sum + file.size, 0)));
  const bulkDescription = $derived.by(() => {
    const actions: string[] = [];
    if (explicitKeepCount) actions.push(`release ${explicitKeepCount} explicit Keep${explicitKeepCount === 1 ? "" : "s"}`);
    if (legacyRemovalCount) actions.push(`remove ${legacyRemovalCount} ${cached ? "cached" : "unreferenced kept"} ${legacyRemovalCount === 1 ? "copy" : "copies"}`);
    const diagnostics = data?.diagnosticCount ?? 0;
    return `This will ${actions.join(" and ") || "make no changes"} (${bulkSize} in the affected copies). ${cached ? "The songs can stream or download again next time they play online. Kept songs and original library files stay. " : ""}Other household interests remain protected.${explicitKeepCount ? " If none remain after releasing a Keep, the file enters a 24-hour grace period before cleanup." : " Removed audio and lyrics cannot be restored from this copy."}${diagnostics ? ` ${diagnostics} diagnostic file${diagnostics === 1 ? " stays" : "s stay"} untouched.` : ""}`;
  });
  const availableProviders = $derived([...new Set(allFiles.map(file => file.provider).filter((value): value is string => Boolean(value)))].toSorted());

  function groupSongs(files: ManagedDownload[]): SongGroup[] {
    const groups = new Map<string, SongGroup>();
    for (const file of files) {
      const metadata = [file.artist, file.title, file.album].map(value => value?.trim().toLocaleLowerCase() || "").join("\u0001");
      // Include metadata even with a source ID: distinct versions must not collapse by an incomplete ID alone.
      const key = file.title && file.artist ? `${file.provider || ""}\u0001${file.externalId || ""}\u0001${metadata}` : file.path;
      const group = groups.get(key);
      if (group) group.files.push(file);
      else groups.set(key, { key, files: [file] });
    }
    return [...groups.values()].map(group => ({ ...group, files: group.files.toSorted((left, right) => copyQuality(right) - copyQuality(left) || left.path.localeCompare(right.path)) }));
  }
  function copyQuality(file: ManagedDownload) {
    return (/^(flac|alac|wav|aiff)$/i.test(file.codec) ? 1e12 : 0) +
      (file.bitDepth ?? 0) * 1e9 + (file.sampleRateHz ?? 0) * 1e3 + (file.bitrateKbps ?? 0);
  }

  function expiry(file: ManagedDownload) {
    const timestamp = file.expiresAt ? Date.parse(file.expiresAt) : NaN;
    return Number.isFinite(timestamp) ? timestamp : Number.MAX_SAFE_INTEGER;
  }
  function leavingSoon(file: ManagedDownload) { return Boolean(file.expiresAt) && expiry(file) <= Date.now() + 24 * 60 * 60 * 1000; }
  function lowerQuality(file: ManagedDownload) {
    return /^(mp3|aac|opus|ogg|vorbis|wma)$/i.test(file.codec) ||
      Boolean(file.sampleRateHz && file.sampleRateHz < 44100) || Boolean(file.bitDepth && file.bitDepth < 16);
  }
  function qualityWarning(file: ManagedDownload) {
    const check = (file as ManagedDownload & { qualityCheck?: { verdict: string; likelySourceRate?: number | null } }).qualityCheck;
    if (check?.verdict === "upsampled") return { label: "Likely upscaled", detail: `The measured bandwidth suggests a ${check.likelySourceRate === 48000 ? "48" : "44.1"} kHz source in this higher-rate file.` };
    if (check?.verdict === "padded-bit-depth") return { label: "Padded", detail: "The low sample bits suggest 16-bit audio padded to 24-bit." };
    if (check?.verdict === "lossy-transcode-suspected") return { label: "Lossy source?", detail: "A sharp high-frequency cutoff suggests a lossy source inside this lossless file." };
    return null;
  }
  function warnings(files: ManagedDownload[]) {
    return [...new Map(files.map(qualityWarning).filter(value => value !== null).map(value => [value.label, value])).values()];
  }
  function qualityLine(file: ManagedDownload) {
    return [file.codec, file.bitDepth && file.sampleRateHz ? `${file.bitDepth}/${file.sampleRateHz / 1000}` : file.bitrateKbps ? `${file.bitrateKbps} kbps` : file.quality].filter(Boolean).join(" ");
  }
  function lifecycle(files: ManagedDownload[]) {
    if (cached) {
      const next = files.toSorted((left, right) => expiry(left) - expiry(right))[0];
      return next.expiresAt ? relativeTime(next.expiresAt) : "No expiry set";
    }
    const references = Math.max(...files.map(file => file.referenceCount ?? 0));
    if (!files.some(file => file.explicitlyKept) && !references) return files[0].publicationState === "Imported" ? "Imported original" : "No active references";
    return `${files.some(file => file.explicitlyKept) ? "Your Keep" : "Household"}${references ? ` · ${references} reference${references === 1 ? "" : "s"}` : ""}`;
  }
  function formatSize(bytes: number) {
    if (bytes < 1024) return `${bytes} B`;
    const unit = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), 4);
    return `${(bytes / 1024 ** unit).toLocaleString(undefined, { maximumFractionDigits: 2 })} ${["B", "KiB", "MiB", "GiB", "TiB"][unit]}`;
  }
  function groupSize(files: ManagedDownload[]) { return files.length === 1 ? files[0].sizeFormatted : formatSize(files.reduce((sum, file) => sum + file.size, 0)); }
  function toggleSort(next: Sort) { if (sort === next) reversed = !reversed; else { sort = next; reversed = false; } }
  function sortOrder(column: Sort): "none" | "ascending" | "descending" {
    if (sort !== column) return "none";
    const descending = column === "quality" || column === "size" || column === "updated" || (!cached && column === "lifecycle");
    return descending !== reversed ? "descending" : "ascending";
  }
  const provider = (providerId?: string | null) => findProviderDefinition(providers, providerId);
  const providerName = (providerId?: string | null) => providerDisplayName(providers, providerId);

  async function openSong(file: ManagedDownload) {
    selected = file;
    try {
      if (!SongPanel) SongPanel = (await import("./LibrarySongPanel.svelte")).default;
      panelOpen = true;
    } catch { toast.error("Song details could not be opened. Refresh and retry."); }
  }
  function resetFilters() {
    query = "";
    providerFilter = "";
    segment = "all";
    writeFilter(`library.${storage}.filter`, "all");
  }
  function toggleCopies(key: string) { expanded = expanded.includes(key) ? expanded.filter(value => value !== key) : [...expanded, key]; }

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const [downloadsResult, schemaResult, configResult] = await Promise.allSettled([
      downloads.list(storage), home.schema(), cached ? settings.config() : Promise.resolve(null),
    ]);
    if (downloadsResult.status === "fulfilled") {
      data = downloadsResult.value;
      movingPaths = movingPaths.filter(path => data?.files.some(file => file.path === path));
      if (selected && !data.files.some(file => file.path === selected?.path)) panelOpen = false;
    } else error = downloadsResult.reason instanceof Error ? downloadsResult.reason.message : `${label} songs are unavailable.`;
    if (schemaResult.status === "fulfilled") providers = schemaResult.value.providers;
    if (cached && configResult.status === "fulfilled" && configResult.value) {
      storageMode = String(pathValue(configResult.value, "library.storageMode") ?? "Cache");
      const duration = Number(pathValue(configResult.value, "library.cacheDurationHours"));
      cacheDurationHours = Number.isFinite(duration) && duration > 0 ? duration : null;
    }
    loading = false;
    refreshing = false;
  }
  const refreshScheduler = createRefreshScheduler(refresh);

  async function keep(file: ManagedDownload) {
    if (action) return;
    action = file.path;
    try {
      const result = await downloads.keep(file.path);
      toast.success(result.queued ? "Moving to Kept…" : "Moved to Kept.");
      if (result.queued) movingPaths = [...movingPaths, file.path];
      await refresh();
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "The song could not be kept."); }
    finally { action = ""; }
  }
  function confirm(next: Removal) { removalError = ""; removal = next; confirmOpen = true; }
  async function previewDeletion(file: ManagedDownload) {
    if (action) return;
    action = "preview-delete";
    try {
      const preview = await downloads.previewKeptDeletion(file.path);
      confirm({ kind: "delete-kept", file, preview, requestId: newRequestId() });
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "The kept copy could not be checked. Refresh and retry."); }
    finally { action = ""; }
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
        toast.success(result.message || (removal.kind === "release" ? "Keep released. Other interests remain protected." : `${removal.file.title || removal.file.fileName} removed.`));
      }
      confirmOpen = false;
      await refresh();
    } catch (cause) { removalError = cause instanceof Error ? cause.message : "Removal failed. Refresh and retry."; }
    finally { action = ""; }
  }
  onMount(() => {
    sort = storage === "cache" ? "lifecycle" : "track";
    void refresh();
    const unsubscribe = liveUpdates.subscribe(refreshScheduler.schedule);
    return () => { unsubscribe(); refreshScheduler.cancel(); };
  });
</script>

{#snippet tableHead()}
  <thead><tr>
    <th class="song-heading" scope="col" aria-sort={sortOrder("track")}><button type="button" onclick={() => toggleSort("track")}>Song</button></th>
    <th class="source-heading" scope="col" aria-sort={sortOrder("provider")}><button type="button" onclick={() => toggleSort("provider")}>Source</button></th>
    <th class="quality-heading" scope="col" aria-sort={sortOrder("quality")}><button type="button" onclick={() => toggleSort("quality")}>Quality</button></th>
    <th class="lifecycle-heading" scope="col" aria-sort={sortOrder("lifecycle")}><button type="button" onclick={() => toggleSort("lifecycle")}>{cached ? "Leaves cache" : "Kept by"}</button></th>
    <th class="size-heading" scope="col" aria-sort={sortOrder("size")}><button type="button" onclick={() => toggleSort("size")}>Size</button></th>
    <th class="actions-heading" scope="col"><span class="sr-only">Actions</span></th>
  </tr></thead>
{/snippet}

{#snippet songRow(file: ManagedDownload, copies: ManagedDownload[], key: string, copiesId: string)}
  <LibraryDownloadRow {file} {copies} {storage} provider={provider(file.provider)} providerName={providerName(file.provider)}
    quality={[...new Set(copies.map(qualityLine))].join(" · ")} warnings={warnings(copies)} lowerQuality={copies.some(lowerQuality)}
    size={groupSize(copies)} lifecycle={lifecycle(copies)} expanded={expanded.includes(key)} {copiesId}
    selected={panelOpen && selectedFile?.path === file.path} busy={Boolean(action)} moving={movingPaths.includes(file.path)}
    onopen={() => openSong(file)} ontoggle={copies.length > 1 ? () => toggleCopies(key) : undefined}
    onkeep={() => void keep(file)} onrelease={() => confirm({ kind: "release", file })}
    onremove={() => confirm({ kind: "one", file })} ondeletion={() => void previewDeletion(file)} />
{/snippet}

<section class="library-downloads" class:panel-open={panelOpen} aria-busy={refreshing} aria-label={`${label} songs`}>
  <header class="library-downloads-heading">
    <h2>{label} songs</h2>
    <div class="library-downloads-heading-actions">
      <RefreshButton variant="ghost" size="icon" class="downloads-refresh" disabled={Boolean(action)} onrefresh={refresh} />
      {#if bulkActionCount}<Button class="danger-text" variant="text" disabled={Boolean(action)} onclick={() => confirm({ kind: "all" })}>Remove all</Button>{/if}
    </div>
  </header>
  {#if loading}
    <div class="downloads-loading" role="region" aria-label={`Loading ${label} tracks`}><RowsSkeleton count={6} /></div>
  {:else if error && !data}
    <div class="downloads-state"><ErrorState title={`${label} songs are unavailable`} detail={error} onretry={refresh} /></div>
  {:else if data}
    <div class="downloads-status">
      <PageStatus label={`${label} totals`} items={[
        { label: `${allGroups.length.toLocaleString()} song${allGroups.length === 1 ? "" : "s"}${allGroups.length !== data.managedCount ? ` · ${data.managedCount} copies` : ""} · ${data.totalSizeFormatted}`, emphasis: true },
        { label: cached ? storageMode !== "Cache" ? "Playback caching is off" : cacheDurationHours ? `Cached for ${cacheDurationHours % 24 === 0 ? `${cacheDurationHours / 24} days` : `${cacheDurationHours} hours`} after the last access` : "Temporary copies saved after streaming" : "Never removed by cache cleanup" },
        ...(cached && leavingSoonCount ? [{ label: `${leavingSoonCount} ${leavingSoonCount === 1 ? "copy leaves" : "copies leave"} in the next day`, tone: "warn" as const }] : []),
        ...(data.diagnosticCount ? [{ label: `${data.diagnosticCount} need review`, tone: "warn" as const }] : []),
      ]} />
    </div>
    {#if error}<div class="downloads-state"><ErrorState title="Managed audio may be stale" detail={error} onretry={refresh} /></div>{/if}
    <div class="downloads-controls">
      <FilterSegments bind:value={segment} storageKey={`library.${storage}.filter`} label={`${label} song filters`} options={[
        { id: "all", label: "All", count: allFiles.length },
        cached ? { id: "leaving", label: "Leaving soon", count: leavingSoonCount } : { id: "explicit", label: "Your Keeps", count: yourKeepsCount },
        { id: "lower", label: "Lower quality", count: lowerQualityCount },
      ]} />
      <div class="downloads-search-sort">
        <SearchField bind:value={query} label={`Filter ${label.toLowerCase()} songs`} placeholder={`Search ${label.toLowerCase()} songs`} hiddenLabel />
        <SelectField bind:value={providerFilter} label="Source" options={[{ value: "", label: "All sources" }, ...availableProviders.map(value => ({ value, label: providerName(value) }))]} />
        <SelectField bind:value={sort} label="Sort songs" options={[
          { value: "lifecycle", label: cached ? "Leaves soonest" : "Kept by" },
          { value: "track", label: "Song" }, { value: "provider", label: "Source" },
          { value: "quality", label: "Quality" }, { value: "size", label: "Size" }, { value: "updated", label: "Newest" },
        ]} onchange={() => reversed = false} />
      </div>
    </div>
    {#if visibleGroups.length}
      <div class="downloads-table-scroll">
        <table class="library-downloads-table" aria-label={`${label} songs`}>
          {@render tableHead()}
          <tbody>
            {#each visibleGroups as group, index (group.key)}
              {@const copiesId = `${id}-copies-${index}`}
              {@render songRow(group.files[0], group.files, group.key, copiesId)}
              {#if group.files.length > 1}
                <tr class="copies-row" id={copiesId} hidden={!expanded.includes(group.key)}><td colspan={panelOpen ? 4 : 6}>
                  {#if expanded.includes(group.key)}
                    <p class="copies-explanation">The row action uses the highest-quality copy. Each copy also has its own actions.</p>
                    <table class="library-downloads-table copies-table" aria-label={`Copies of ${group.files[0].title || group.files[0].fileName}`}>
                      {@render tableHead()}
                      <tbody>{#each group.files as file (file.path)}{@render songRow(file, [file], "", "")}{/each}</tbody>
                    </table>
                  {/if}
                </td></tr>
              {/if}
            {/each}
          </tbody>
        </table>
      </div>
    {:else if allFiles.length}
      <EmptyState title="No songs match these filters" description="Try another source, search, or filter.">
        {#snippet action()}<Button variant="outline" onclick={resetFilters}>Show all songs</Button>{/snippet}
      </EmptyState>
    {:else}
      <EmptyState title={`No ${label.toLowerCase()} songs`} description={cached ? storageMode === "Cache"
        ? "Songs appear when their background download finishes, even if you skip the song. Incomplete or failed files are not listed."
        : "New playback is not being added to Cached. Enable playback caching to save temporary copies."
        : "Use Keep on a cached song to save it permanently."}>
        {#snippet icon()}<Download size={24} />{/snippet}
        {#snippet action()}{#if !cached}<Button variant="outline" href="#/library/cached">Browse cached songs</Button>{/if}{/snippet}
      </EmptyState>
    {/if}
    {#if cached}<footer class="track-cache-link"><Badge state={storageMode === "Cache" ? "healthy" : "suggested"}>Playback cache {storageMode === "Cache" ? "on" : "off"}</Badge><Button variant="text" href="#/settings/storage">Cache settings</Button></footer>{/if}
  {/if}
</section>

{#if selectedFile && SongPanel}
  {@const file = selectedFile}
  <SongPanel bind:open={panelOpen} {file} {storage} provider={provider(file.provider)} providerName={providerName(file.provider)}
    warning={qualityWarning(file)} lowerQuality={lowerQuality(file)} busy={Boolean(action)} moving={movingPaths.includes(file.path)}
    onPrevious={selectedIndex > 0 ? () => openSong(navigationFiles[selectedIndex - 1]) : undefined}
    onNext={selectedIndex >= 0 && selectedIndex < navigationFiles.length - 1 ? () => openSong(navigationFiles[selectedIndex + 1]) : undefined}
    onkeep={() => void keep(file)} onrelease={() => confirm({ kind: "release", file })}
    onremove={() => confirm({ kind: "one", file })} ondeletion={() => void previewDeletion(file)} />
{/if}

<ConfirmDialog bind:open={confirmOpen}
  title={removal?.kind === "delete-kept" ? `Delete the kept copy of ${removal.preview.title}?` : removal?.kind === "all" ? `Remove all ${label.toLowerCase()} songs?` : removal?.kind === "release" ? "Stop keeping this song?" : "Remove this song?"}
  description={removal?.kind === "delete-kept"
    ? `This removes the audio and lyrics for everyone in this household. ${removal.preview.favoriteReferences} favorite${removal.preview.favoriteReferences === 1 ? "" : "s"}, ${removal.preview.playlistReferences} playlist reference${removal.preview.playlistReferences === 1 ? "" : "s"}, and ${removal.preview.keepReferences} explicit Keep${removal.preview.keepReferences === 1 ? "" : "s"}${removal.preview.otherReferences ? `, and ${removal.preview.otherReferences} other reference${removal.preview.otherReferences === 1 ? "" : "s"}` : ""} currently protect it. Hearts and playlist entries stay. This ends offline playback from this copy; the song will stream or download again when played online. A new Keep can save it again.`
    : removal?.kind === "all" ? bulkDescription
    : removal?.kind === "release" ? `This releases your Keep on ${removal.file.title || removal.file.fileName}. Other household interests stay protected. If none remain, the file enters a 24-hour grace period before cleanup.`
    : `This deletes the indexed audio file and its lyrics sidecar for ${removal?.file.title || removal?.file.fileName || "this song"} (${removal?.file.sizeFormatted || ""}). This cannot be undone.`}
  confirmLabel={removal?.kind === "delete-kept" ? "Delete kept copy" : removal?.kind === "all" ? `Remove ${bulkActionCount} ${bulkActionCount === 1 ? "copy" : "copies"}` : removal?.kind === "release" ? "Stop keeping" : "Remove song"}
  disabled={Boolean(action)} closeOnConfirm={false} error={removalError} onConfirm={remove} />

<style>
  .library-downloads { min-width: 0; padding: var(--space-5); }
  .library-downloads-heading { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); padding-bottom: var(--space-3); }
  h2 { margin: 0; font-size: 1.5rem; font-weight: 600; }
  .library-downloads-heading-actions { display: flex; flex: 0 0 auto; align-items: center; gap: var(--space-2); }
  .library-downloads-heading-actions :global(.danger-text) { color: var(--danger); }
  :global(.downloads-refresh span) { display: none; }
  .downloads-status { padding-bottom: var(--space-4); }
  .downloads-state, .downloads-loading { padding-block: var(--space-4); }
  .downloads-controls { display: grid; min-width: 0; gap: var(--space-3); padding-bottom: var(--space-4); }
  .downloads-search-sort { display: flex; min-width: 0; align-items: center; gap: var(--space-2); }
  .downloads-search-sort :global(.search-field) { flex: 1 1 0; min-width: 0; }
  .downloads-search-sort :global(.search-field input) { width: 100%; min-width: 0; height: var(--control-md); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-panel); color: var(--color-ink); font-size: .8125rem; }
  .downloads-search-sort :global(.select-root) { flex: 0 0 auto; }
  .downloads-search-sort :global(.select-trigger) { width: auto; gap: var(--space-2); padding-inline: var(--space-2); background: var(--color-panel); color: var(--color-ink); font-size: .8125rem; white-space: nowrap; }
  .downloads-table-scroll { min-width: 0; overflow-x: auto; }
  .library-downloads-table { width: 100%; min-width: 800px; table-layout: fixed; border-collapse: collapse; text-align: left; }
  th { border-bottom: 1px solid var(--color-edge); padding: var(--space-2) var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 500; }
  th button { min-height: var(--control-sm); border: 0; padding: 0; background: transparent; color: inherit; cursor: pointer; font: inherit; text-align: left; white-space: nowrap; }
  th[aria-sort="ascending"] button, th[aria-sort="descending"] button { color: var(--color-signal-text); font-weight: 600; }
  .song-heading { width: 32%; padding-left: var(--space-4); }
  .size-heading { width: 84px; text-align: right; }
  .actions-heading { width: 150px; }
  .copies-row[hidden] { display: none; }
  .copies-row > td { border-bottom: 1px solid var(--color-edge); padding: var(--space-3); background: var(--color-panel-raised); }
  .copies-explanation { margin: 0 0 var(--space-2); padding-inline: var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .copies-table { min-width: 0; border: 1px solid var(--color-edge); }
  .track-cache-link { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); border-top: 1px solid var(--color-edge); padding-block: var(--space-3); }
  @media (min-width: 900px) {
    .library-downloads.panel-open { margin-right: min(480px, 40vw); }
    .panel-open .library-downloads-table { min-width: 0; }
    .panel-open .song-heading { width: auto; padding-left: var(--space-2); }
    .panel-open .source-heading { width: 100px; }
    .panel-open .quality-heading { width: 90px; }
    .panel-open .actions-heading { width: 128px; }
    .panel-open .lifecycle-heading, .panel-open .size-heading, .panel-open :global(.lifecycle-cell), .panel-open :global(.size-cell) { display: none; }
    .panel-open th, .panel-open :global(.library-download-row > td) { padding-inline: var(--space-2); }
    .panel-open :global(.library-download-row > td), .panel-open :global(.song-description > strong) { font-size: var(--text-xs); }
    .panel-open :global(.song-copy) { gap: var(--space-2); }
    .panel-open :global(.library-download-art) { width: 36px; height: 36px; }
    .panel-open :global(.source-name) { flex-wrap: wrap; gap: var(--space-1); overflow-wrap: normal; }
    .panel-open :global(.library-download-actions [data-slot="button"]) { padding-inline: var(--space-2); font-size: var(--text-xs); }
    .panel-open :global(.library-download-actions [data-slot="button"] svg) { display: none; }
    .panel-open :global(.library-download-menu) { width: 32px; min-width: 32px; height: 32px; min-height: 32px; }
  }
  @media (max-width: 899px) {
    .library-downloads { padding: var(--space-3); }
    .library-downloads-heading { flex-wrap: wrap; padding-bottom: var(--space-3); }
    h2 { font-size: var(--text-lg); }
    .library-downloads-heading-actions { margin-left: auto; gap: var(--space-1); }
    .library-downloads-heading-actions :global([data-slot="button"]) { min-height: 44px; }
    .library-downloads-heading-actions :global(.downloads-refresh) { min-width: 44px; }
    .downloads-controls { padding-bottom: var(--space-3); }
    .downloads-search-sort { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); justify-content: stretch; }
    .downloads-search-sort :global(.search-field) { grid-column: 1 / -1; min-width: 0; }
    .downloads-search-sort :global(.select-root) { min-width: 0; }
    .downloads-search-sort :global(.select-trigger) { width: 100%; }
    .library-downloads-table, .library-downloads-table tbody { display: block; width: 100%; min-width: 0; }
    .library-downloads-table thead { display: none; }
    .copies-row, .copies-row > td { display: block; }
    .copies-row > td { padding: var(--space-2); }
    .track-cache-link :global([data-slot="button"]) { min-height: 44px; }
  }
</style>
