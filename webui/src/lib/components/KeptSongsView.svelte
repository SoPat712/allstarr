<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { onMount } from "svelte";
  import { downloads, home, type DownloadsResponse, type ManagedDownload, type ProviderDefinition, type KeptCopyDeletionPreview } from "$lib/api";
  import { MoreHorizontal } from "@lucide/svelte";
  import { countLabel } from "$lib/count-label";
  import { relativeTime } from "$lib/activity";
  import { formatDuration } from "$lib/playlists";
  import { compareKeptBy, downloadQualityLabel, filterDownloads, formatDownloadSize, groupDownloadSongs, keptByText, lowerDownloadQuality, qualityDetails, qualityWarning, type DownloadSort } from "$lib/downloads";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";
  import { libraryPromotions, type KeptPromotionContext, type PromotionRequest } from "$lib/library-promotion-api";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { newReportRequestId as newRequestId } from "$lib/support";
  import LibraryDownloadRow from "./LibraryDownloadRow.svelte";
  import LibraryDownloadTable from "./LibraryDownloadTable.svelte";
  import PageStatus from "./patterns/PageStatus.svelte";
  import RowsSkeleton from "./patterns/RowsSkeleton.svelte";
  import EmptyState from "./patterns/EmptyState.svelte";
  import ErrorState from "./patterns/ErrorState.svelte";
  import LibraryPromotionAddPanel from "./LibraryPromotionAddPanel.svelte";
  import LibraryDownloadActions from "./LibraryDownloadActions.svelte";
  import SongPlaylistMembership from "./SongPlaylistMembership.svelte";
  import ConfirmDialog from "./ConfirmDialog.svelte";
  import { Button } from "./ui/button";
  import { DropdownMenu } from "./ui/dropdown-menu";
  import SearchField from "./SearchField.svelte";
  import SelectField from "./SelectField.svelte";
  import FilterSegments from "./patterns/FilterSegments.svelte";
  import { readFilter, writeFilter } from "./patterns/helpers";

  import { promotionSetupHint } from "$lib/promotion-setup";

  let { administrator = false }: { administrator?: boolean } = $props();
  type Removal = { kind: "one" | "release"; file: ManagedDownload } | { kind: "all" }
    | { kind: "delete-kept"; file: ManagedDownload; preview: KeptCopyDeletionPreview; requestId: string };

  let files = $state<ManagedDownload[]>([]);
  let data = $state<DownloadsResponse | null>(null);
  let context = $state<KeptPromotionContext | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true), error = $state("");
  let selected = $state<ManagedDownload | null>(null), open = $state(false);
  let request = $state<PromotionRequest | null>(null), busy = $state(false);
  let panelError = $state("");
  let selecting = $state(false), chosen = $state<string[]>([]), bulkOpen = $state(false), bulkError = $state("");
  let action = $state(""), removal = $state<Removal | null>(null), confirmOpen = $state(false), removalError = $state("");
  let deletionNeedsPreview = $state(false);
  let controlsOpen = $state(false), query = $state(""), providerFilter = $state(""), segment = $state("all"), sort = $state<DownloadSort | "lifecycle">("track"), reversed = $state(false);
  let expanded = $state<string[]>([]);
  const id = $props.id();
  const bulkRequests = new Map<string, PromotionRequest>();
  let refreshing = false;
  const allGroups = $derived(groupDownloadSongs(files));
  const songs = $derived.by(() => {
    const filtered = filterDownloads(files, query, providerFilter, sort === "lifecycle" ? "track" : sort)
      .filter(file => segment === "all" || segment === "explicit" && file.explicitlyKept || segment === "lower" && lowerDownloadQuality(file));
    const sorted = sort === "lifecycle" ? filtered.toSorted(compareKeptBy) : filtered;
    return reversed ? sorted.toReversed() : sorted;
  });
  const songGroups = $derived.by(() => {
    const groups = groupDownloadSongs(songs);
    if (sort !== "size") return groups;
    return groups.toSorted((left, right) => {
      const difference = right.files.reduce((sum, file) => sum + file.size, 0) - left.files.reduce((sum, file) => sum + file.size, 0);
      return reversed ? -difference : difference;
    });
  });
  const navigationFiles = $derived(songGroups.flatMap(group => group.files));
  const selectedFile = $derived(files.find(file => file.path === selected?.path) ?? selected);
  const removableFiles = $derived(files.filter(file => file.removable));
  const explicitKeepCount = $derived(removableFiles.filter(file => file.explicitlyKept).length);
  const legacyRemovalCount = $derived(removableFiles.length - explicitKeepCount);
  const bulkSize = $derived(removableFiles.length === files.length ? data?.totalSizeFormatted ?? "0 B" : formatDownloadSize(removableFiles.reduce((sum, file) => sum + file.size, 0)));
  const availableProviders = $derived([...new Set(files.map(file => file.provider).filter((value): value is string => Boolean(value)))].toSorted());
  const bulkDescription = $derived.by(() => {
    const actions: string[] = [];
    if (explicitKeepCount) actions.push(`release ${countLabel(explicitKeepCount, "explicit Keep")}`);
    if (legacyRemovalCount) actions.push(`remove ${legacyRemovalCount} unreferenced kept ${legacyRemovalCount === 1 ? "copy" : "copies"}`);
    const diagnostics = data?.diagnosticCount ?? 0;
    return `This will ${actions.join(" and ") || "make no changes"} (${bulkSize} in the affected copies). Other household interests remain protected.${explicitKeepCount ? " If none remain after releasing a Keep, the file enters a 24-hour grace period before cleanup." : " Removed audio and lyrics cannot be restored from this copy."}${diagnostics ? ` ${diagnostics} diagnostic file${diagnostics === 1 ? " stays" : "s stay"} untouched.` : ""}`;
  });
  const lossless = $derived(allGroups.filter(group => group.files.some(file => /^(flac|alac|wavpack|ape|pcm_)/i.test(file.codec))).length);
  const libraryCount = $derived(context && files.every(file => context?.items.some(item => item.path === file.path)) ? allGroups.filter(group => group.files.some(file => context?.items.some(item => item.path === file.path && item.inLibrary))).length : null);
  const setupRequired = $derived(context?.setup?.reason === "library-folder-not-set");
  const addUnavailable = $derived(context?.setup?.ready === false);
  const addHint = $derived(setupRequired ? promotionSetupHint : addUnavailable ? "The approved library folder is unavailable" : "");
  const selectedIndex = $derived(navigationFiles.findIndex(file => file.path === selectedFile?.path));
  function warnings(copies: ManagedDownload[]) {
    return [...new Map(copies.map(qualityWarning).filter(value => value !== null).map(value => [value.label, value])).values()];
  }
  function selectionCopies(copies: ManagedDownload[]) {
    return allGroups.find(group => group.files.some(file => file.path === copies[0].path))?.files ?? copies;
  }
  function selectSong(copies: ManagedDownload[], checked: boolean) {
    const paths = new Set(selectionCopies(copies).map(file => file.path));
    chosen = chosen.filter(path => !paths.has(path));
    if (checked) chosen = [...chosen, copies[0].path];
  }
  function makeRequest(file: ManagedDownload): PromotionRequest {
    const item = context?.items.find(item => item.path === file.path);
    if (!item?.recordingId) throw new Error("This kept copy has no recording identity yet. Index it before adding it to the library.");
    const backendLibraryId = item.backendLibraryId ?? context?.backendLibraryId;
    if (!backendLibraryId) throw new Error("Choose an accessible library before adding songs.");
    return { recordingId: item.recordingId, backendLibraryId, rootId: context?.rootId ?? "00000000-0000-0000-0000-000000000000", requestKey: newRequestId(), unit: "album" };
  }
  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    const results = await Promise.allSettled([downloads.list("kept"), libraryPromotions.kept()]);
    if (results[0].status === "fulfilled") {
      data = results[0].value; files = data.files;
      if (selected && !files.some(file => file.path === selected?.path)) open = false;
    }
    else error = results[0].reason instanceof Error ? results[0].reason.message : "Kept songs could not be loaded.";
    if (results[1].status === "fulfilled") context = Array.isArray(results[1].value.items) ? results[1].value : null;
    else error = results[1].reason instanceof Error ? results[1].reason.message : "Library status could not be loaded.";
    if (results.every(result => result.status === "fulfilled")) error = "";
    loading = false; refreshing = false;
  }
  function openSong(file: ManagedDownload) {
    selected = file; request = null; panelError = ""; open = true;
    try { request = makeRequest(file); }
    catch { /* Copy actions remain available before library indexing. */ }
  }
  function confirm(next: Removal) { removalError = ""; deletionNeedsPreview = false; removal = next; confirmOpen = true; }
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
    if (!removal || action || deletionNeedsPreview) return;
    action = "remove"; removalError = "";
    try {
      if (removal.kind === "delete-kept") {
        await downloads.deleteKeptCopy(removal.preview, removal.requestId);
        toast.success("Kept copy deletion queued.");
      } else if (removal.kind === "all") {
        const result = await downloads.removeAll("kept");
        toast.success(result.message || `${countLabel(result.deletedCount, "indexed kept track")} removed${result.skippedUnknown ? `; ${countLabel(result.skippedUnknown, "diagnostic file")} skipped` : ""}${result.skippedReferenced ? `; ${countLabel(result.skippedReferenced, "referenced file")} protected` : ""}.`);
      } else {
        const result = await downloads.remove(removal.file.path, "kept");
        toast.success(result.message || (removal.kind === "release" ? "Keep released. Other interests remain protected." : `${removal.file.title || removal.file.fileName} removed.`));
      }
      confirmOpen = false;
      await refresh();
    } catch (cause) {
      removalError = cause instanceof Error ? cause.message : "Removal failed. Refresh and retry.";
      if (removal.kind === "delete-kept") {
        deletionNeedsPreview = true;
        removalError += " Close this dialog and check the kept copy again before deleting.";
      }
    }
    finally { action = ""; }
  }
  async function startSelected() {
    if (busy || addUnavailable) return;
    busy = true; bulkError = "";
    let lastId = "";
    const startedAlbums = new Set<string>();
    try {
      for (const file of files.filter(file => chosen.includes(file.path))) {
        const next = bulkRequests.get(file.path) ?? makeRequest(file); bulkRequests.set(file.path, next);
        const plan = await libraryPromotions.preview(next);
        const key = `${next.backendLibraryId}:${plan.album.artist}:${plan.album.title}:${plan.album.year ?? ""}`;
        if (startedAlbums.has(key)) { chosen = chosen.filter(path => path !== file.path); continue; }
        const result = plan.canAddDirectly === false ? await libraryPromotions.createRequest(next) : await libraryPromotions.start(next);
        if (!("requested" in result)) lastId = result.id;
        startedAlbums.add(key);
        chosen = chosen.filter(path => path !== file.path);
      }
      bulkOpen = false;
      if (lastId) { toast.success(`Adding ${startedAlbums.size} ${startedAlbums.size === 1 ? "album" : "albums"} to your library.`); window.location.hash = `#/library/to-library?run=${encodeURIComponent(lastId)}`; }
    } catch (cause) { bulkError = cause instanceof Error ? cause.message : "The selected albums could not be started."; }
    finally { busy = false; }
  }
  const scheduler = createRefreshScheduler(refresh, 250, 1000);
  function resetFilters() { query = ""; providerFilter = ""; segment = "all"; writeFilter("library.kept.filter", "all"); }
  onMount(() => {
    segment = readFilter("library.kept.filter", ["all", "explicit", "lower"]) ?? "all";
    controlsOpen = segment !== "all";
    void refresh();
    void home.schema().then(schema => providers = schema.providers).catch(() => {});
    const unsubscribe = liveUpdates.subscribe(() => { if (document.visibilityState !== "hidden") scheduler.schedule(); }, 10000);
    return () => { unsubscribe(); scheduler.cancel(); };
  });
</script>

{#snippet songRow(file: ManagedDownload, copies: ManagedDownload[], groupKey: string, copiesId: string, physicalCopy: boolean)}
  <LibraryDownloadRow {file} {copies} storage="kept" provider={findProviderDefinition(providers, file.provider)} providerName={[...new Set(copies.map(copy => providerDisplayName(providers, copy.provider)))].join(" + ")}
    quality={downloadQualityLabel(file)} qualities={[...new Map(copies.map(copy => [downloadQualityLabel(copy), { label: downloadQualityLabel(copy), lower: lowerDownloadQuality(copy) }])).values()]}
    warnings={warnings(copies)} lowerQuality={copies.some(lowerDownloadQuality)} size={formatDownloadSize(copies.reduce((total, copy) => total + copy.size, 0))} lifecycle={keptByText(file.keptBy)}
    inLibrary={copies.some(copy => context?.items.some(item => item.path === copy.path && item.inLibrary))} selected={open && copies.some(copy => copy.path === selectedFile?.path)} selection={selecting && !physicalCopy} checked={selectionCopies(copies).some(copy => chosen.includes(copy.path))} oncheck={checked => selectSong(copies, checked)}
    expanded={expanded.includes(groupKey)} {copiesId} busy={Boolean(action)} onopen={() => openSong(file)}
    ontoggle={copies.length > 1 && !physicalCopy ? () => expanded = expanded.includes(groupKey) ? expanded.filter(key => key !== groupKey) : [...expanded, groupKey] : undefined}
    onadd={() => openSong(file)} addUnavailable={addHint} onkeep={() => {}} onrelease={() => confirm({ kind: "release", file })} onremove={() => confirm({ kind: "one", file })} ondeletion={() => void previewDeletion(file)} />
{/snippet}

<section class="promotion-page" class:panel-open={open} aria-label="Kept songs">
  <header class="promotion-page-heading"><h2>Kept songs</h2><div class="heading-actions"><Button size="sm" class="rounded-full" variant="outline" title={addHint} disabled={loading || busy || Boolean(action) || addUnavailable} onclick={() => { if (!selecting) selecting = true; else if (chosen.length) { bulkError = ""; bulkOpen = true; } }} >Add selected to library</Button>
    <DropdownMenu.Root><DropdownMenu.Trigger class="icon-button library-download-menu" aria-label="Actions for kept songs"><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu" align="end" sideOffset={6}>
      <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void refresh()}>Refresh</DropdownMenu.Item>
      <DropdownMenu.Item class="bits-menu-item" onSelect={() => controlsOpen = !controlsOpen}>{controlsOpen ? "Hide search and sort" : "Search and sort"}</DropdownMenu.Item>
      {#if removableFiles.length}<DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action)} onSelect={() => confirm({ kind: "all" })}>Remove all</DropdownMenu.Item>{/if}
    </DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
  </div></header>
  {#if loading}<RowsSkeleton count={5} />{:else}
    {#if addHint}<p class="selection-note">{addHint}</p>{/if}
    {#if panelError}<p role="alert" class="notice-error">{panelError}</p>{/if}
    {#if error}<ErrorState title="Library status is unavailable" detail={error} onretry={refresh} />{/if}
    <PageStatus label="Kept totals" items={[{ label: `${allGroups.length} kept`, emphasis: true }, ...(files.length > allGroups.length ? [{ label: countLabel(files.length, "copy", "copies") }] : []), { label: `${lossless} lossless` }, ...(libraryCount === null ? [] : [{ label: `${libraryCount} already in your library` }]), ...(data?.diagnosticCount ? [{ label: `${data.diagnosticCount} need review`, tone: "warn" as const }] : [])]} />
    {#if controlsOpen}<div class="kept-controls">
      <FilterSegments bind:value={segment} storageKey="library.kept.filter" label="Kept song filters" options={[{ id: "all", label: "All", count: allGroups.length }, { id: "explicit", label: "Your Keeps", count: allGroups.filter(group => group.files.some(file => file.explicitlyKept)).length }, { id: "lower", label: "Lower quality", count: allGroups.filter(group => group.files.some(lowerDownloadQuality)).length }]} />
      <div class="kept-search-sort"><SearchField bind:value={query} label="Filter kept songs" placeholder="Search kept songs" hiddenLabel /><SelectField bind:value={providerFilter} label="Source" options={[{ value: "", label: "All sources" }, ...availableProviders.map(value => ({ value, label: providerDisplayName(providers, value) }))]} /><SelectField bind:value={sort} label="Sort songs" options={[{ value: "lifecycle", label: "Kept by" }, { value: "track", label: "Song" }, { value: "provider", label: "Source" }, { value: "quality", label: "Quality" }, { value: "size", label: "Size" }, { value: "updated", label: "Newest" }]} onchange={() => reversed = false} /><Button variant="text" aria-pressed={reversed} onclick={() => reversed = !reversed}>Reverse order</Button></div>
    </div>{/if}
    {#if selecting}<p class="selection-note">Select songs to add their albums. <Button variant="text" onclick={() => { selecting = false; chosen = []; }}>Cancel selection</Button></p>{/if}
    {#if songGroups.length}<div class="promotion-list downloads-table-scroll" class:selecting>
      <LibraryDownloadTable label="Kept songs" kept {sort} {reversed} onsort={value => { if (sort === value) reversed = !reversed; else { sort = value; reversed = false; } }}>
      {#each songGroups as group, index (group.key)}
        {@const copiesId = `${id}-copies-${index}`}
        <tbody class="kept-song-group">
          {@render songRow(group.files[0], group.files, group.key, copiesId, false)}
          {#if group.files.length > 1}
            <tr class="kept-copies" id={copiesId} hidden={!expanded.includes(group.key)}><td colspan="6">
              {#if expanded.includes(group.key)}
                <p class="copies-explanation">The song row opens the highest-quality copy. Each copy has its own details and actions.</p>
                <LibraryDownloadTable class="copies-table" label={`Copies of ${group.files[0].title || group.files[0].fileName}`} kept><tbody>
                  {#each group.files as file (file.path)}{@render songRow(file, [file], "", "", true)}{/each}
                </tbody></LibraryDownloadTable>
              {/if}
            </td></tr>
          {/if}
        </tbody>
      {/each}
      </LibraryDownloadTable>
    </div>{:else if files.length}<EmptyState title="No songs match these filters" description="Try another source, search, or filter.">{#snippet action()}<Button variant="outline" onclick={resetFilters}>Show all songs</Button>{/snippet}</EmptyState>{:else}<EmptyState title="No kept songs" description="Use Keep on a cached song to save it permanently." />{/if}
  {/if}
</section>
{#if selectedFile}<LibraryPromotionAddPanel bind:open recordingId={request?.recordingId ?? ""} backendLibraryId={request?.backendLibraryId} rootId={request?.rootId} {providers} {administrator} {setupRequired} title={selectedFile.album || selectedFile.title} description={selectedFile.artist} cover={selectedFile.artworkUrl} onPrevious={selectedIndex > 0 ? () => openSong(navigationFiles[selectedIndex - 1]) : undefined} onNext={selectedIndex >= 0 && selectedIndex < navigationFiles.length - 1 ? () => openSong(navigationFiles[selectedIndex + 1]) : undefined}>
  {#snippet copyActions()}<section class="kept-copy" aria-label="Kept copy"><h3 class="sec-label">Kept copy</h3><p>{providerDisplayName(providers, selectedFile?.provider)} · {qualityDetails(selectedFile!).join(" · ")} · {selectedFile?.sizeFormatted}</p><p>{selectedFile?.publicationState === "Imported" ? "Imported original · read-only" : selectedFile?.explicitlyKept ? "Your Keep" : selectedFile?.referenceCount ? "Household" : "No active references"}{selectedFile?.referenceCount ? ` · ${countLabel(selectedFile.referenceCount, "reference")}` : ""}</p><p class="copy-path">{selectedFile?.path}</p>
    <AdvancedFold placement="flush" class="copy-facts" label="Copy details"><p>Never removed by cache cleanup.</p><p>{selectedFile?.explicitlyKept ? "Your Keep protects this copy. Other household interests also stay protected when you stop keeping." : selectedFile?.publicationState === "Imported" ? "This imported original is read-only." : selectedFile?.referenceCount ? "Household interests protect this copy." : "This copy has no active household references."}</p>
      {#if qualityWarning(selectedFile!)}<p class="copy-warning">{qualityWarning(selectedFile!)?.label}: {qualityWarning(selectedFile!)?.detail}</p>{/if}
      {#if lowerDownloadQuality(selectedFile!)}<p class="copy-warning">This copy is below CD quality.</p>{/if}
      <dl><dt>Song</dt><dd>{selectedFile?.title || selectedFile?.fileName}</dd><dt>Artist</dt><dd>{selectedFile?.artist || "Unknown artist"}</dd><dt>Album</dt><dd>{selectedFile?.album || "Unknown album"}</dd><dt>Duration</dt><dd>{formatDuration(selectedFile?.durationMilliseconds)}</dd>
        {#if selectedFile?.externalId}<dt>Source ID</dt><dd>{selectedFile.externalId}</dd>{/if}
        <dt>Quality</dt><dd>{selectedFile?.quality || selectedFile?.codec}</dd><dt>Location</dt><dd>Kept</dd><dt>Status</dt><dd>{selectedFile?.publicationState === "Imported" ? "Imported original · read-only" : selectedFile?.publicationState}</dd>
        <dt>Last access</dt><dd><time datetime={selectedFile?.lastAccessedAt} title={selectedFile?.lastAccessedAt}>{relativeTime(selectedFile!.lastAccessedAt)}</time></dd>
        {#if selectedFile?.expiresAt}<dt>Leaves cache</dt><dd><time datetime={selectedFile.expiresAt} title={selectedFile.expiresAt}>{relativeTime(selectedFile.expiresAt)}</time></dd>{/if}
        <dt>Your Keep</dt><dd>{selectedFile?.explicitlyKept ? "Active" : "None"}</dd><dt>Size</dt><dd>{countLabel(selectedFile!.size, "byte")}</dd>
        <dt>Updated</dt><dd><time datetime={selectedFile?.lastModified} title={selectedFile?.lastModified}>{relativeTime(selectedFile!.lastModified)}</time></dd><dt>Filename</dt><dd>{selectedFile?.fileName}</dd>
      </dl>
    </AdvancedFold>
    <SongPlaylistMembership file={{ ...selectedFile!, canonicalRecordingId: context?.items.find(item => item.path === selectedFile?.path)?.recordingId ?? selectedFile?.canonicalRecordingId }} {open} />
    <div class="kept-copy-actions"><LibraryDownloadActions file={selectedFile!} storage="kept" busy={Boolean(action)} onkeep={() => {}} onrelease={() => confirm({ kind: "release", file: selectedFile! })} onremove={() => confirm({ kind: "one", file: selectedFile! })} ondeletion={() => void previewDeletion(selectedFile!)} /></div>
  </section>{/snippet}
</LibraryPromotionAddPanel>{/if}
<ConfirmDialog bind:open={bulkOpen} title="Add selected albums to your library?" description={`${chosen.length} selected ${chosen.length === 1 ? "song" : "songs"}. Each album is added once, using your saved profile and the best copy your download sources can get.`} confirmLabel="Add to library" confirmVariant="default" closeOnConfirm={false} disabled={busy} error={bulkError} onConfirm={startSelected} />
<ConfirmDialog bind:open={confirmOpen}
  title={removal?.kind === "delete-kept" ? `Delete the kept copy of ${removal.preview.title}?` : removal?.kind === "all" ? "Remove all kept songs?" : removal?.kind === "release" ? "Stop keeping this song?" : "Remove this song?"}
  description={removal?.kind === "delete-kept"
    ? `This removes the audio and lyrics for everyone in this household. ${countLabel(removal.preview.favoriteReferences, "favorite")}, ${countLabel(removal.preview.playlistReferences, "playlist reference")}, and ${countLabel(removal.preview.keepReferences, "explicit Keep")}${removal.preview.otherReferences ? `, and ${countLabel(removal.preview.otherReferences, "other reference")}` : ""} currently protect it. Hearts and playlist entries stay. This ends offline playback from this copy; the song will stream or download again when played online. A new Keep can save it again.`
    : removal?.kind === "all" ? bulkDescription
    : removal?.kind === "release" ? `This releases your Keep on ${removal.file.title || removal.file.fileName}. Other household interests stay protected. If none remain, the file enters a 24-hour grace period before cleanup.`
    : `This deletes the indexed audio file and its lyrics sidecar for ${removal?.file.title || removal?.file.fileName || "this song"} (${removal?.file.sizeFormatted || ""}). This cannot be undone.`}
  confirmLabel={removal?.kind === "delete-kept" ? "Delete kept copy" : removal?.kind === "all" ? `Remove ${countLabel(removableFiles.length, "copy", "copies")}` : removal?.kind === "release" ? "Stop keeping" : "Remove song"}
  disabled={Boolean(action)} confirmDisabled={deletionNeedsPreview} closeOnConfirm={false} error={removalError} onConfirm={remove} />

<style>
  .promotion-page { display: grid; min-width: 0; align-content: start; gap: 18px; }
  .promotion-page-heading { display: flex; min-width: 0; align-items: center; justify-content: space-between; gap: 12px; }
  .heading-actions, .kept-copy-actions { display: flex; align-items: center; gap: var(--space-2); }
  .heading-actions { min-width: 0; max-width: 100%; }
  .heading-actions :global([data-slot="button"]) { min-width: 0; max-width: 100%; min-height: var(--control-sm); height: auto; flex-shrink: 1; white-space: normal; overflow-wrap: anywhere; }
  .kept-controls { display: grid; min-width: 0; gap: var(--space-3); }
  .kept-search-sort { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); }
  .kept-search-sort :global(.search-field) { flex: 1 1 160px; }
  .kept-search-sort :global(.select-root) { min-width: 0; }
  .kept-copy { min-width: 0; }
  .kept-copy p { margin: 0 0 var(--space-2); color: var(--color-ink-muted); font-size: var(--text-xs); overflow-wrap: anywhere; }
  .kept-copy-actions { justify-content: flex-end; margin-top: var(--space-3); }
  :global(.copy-facts) { margin-bottom: var(--space-2); font-size: var(--text-xs); line-height: 1.5; }
  :global(.copy-facts) dl { display: grid; grid-template-columns: minmax(0, .8fr) minmax(0, 1.8fr); gap: var(--space-2); margin: 0; }
  :global(.copy-facts) dt { color: var(--color-ink-muted); }
  :global(.copy-facts) dd { min-width: 0; margin: 0; overflow-wrap: anywhere; }
  :global(.copy-facts > p), :global(.copy-facts > dl) { margin-block: var(--space-3); }
  .kept-copy .copy-warning { color: var(--warning); }
  h2 { margin: 0; font-size: var(--text-section); font-weight: 600; }
  .promotion-list { overflow: hidden; border: 1px solid var(--color-edge); border-radius: 14px; }
  .downloads-table-scroll { container: downloads / inline-size; min-width: 0; }
  .kept-copies { background: var(--color-panel-raised); }
  .kept-copies[hidden] { display: none; }
  .kept-copies > td { padding: var(--space-3); }
  .copies-explanation { margin: 0 0 var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; }
  .selection-note { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0; font-size: .8rem; color: var(--color-ink-muted); }
  @media (max-width: 899px) {
    .promotion-page-heading { flex-wrap: wrap; }
    .promotion-page-heading :global([data-slot="button"]) { min-height: 44px; }
    .kept-copies, .kept-copies > td { display: block; }
    .kept-copies > td { padding: var(--space-2); }
    .kept-copy-actions :global([data-slot="button"]) { min-height: 44px; }
    .kept-search-sort :global([data-slot="button"]) { min-height: 44px; }
  }
</style>
