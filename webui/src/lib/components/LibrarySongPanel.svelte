<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { adminUrl } from "$lib/admin-url";
  import type { ManagedDownload, ProviderDefinition } from "$lib/api";
  import { relativeTime } from "$lib/activity";
  import { qualityDetails } from "$lib/downloads";
  import { formatDuration } from "$lib/playlists";
  import LibraryDownloadActions from "$lib/components/LibraryDownloadActions.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";

  let { open = $bindable(false), file, storage, provider, providerName, warning, lowerQuality = false,
    busy = false, moving = false, onPrevious, onNext, onkeep, onrelease, onremove, ondeletion }: {
    open?: boolean;
    file: ManagedDownload;
    storage: "cache" | "kept";
    provider?: ProviderDefinition;
    providerName: string;
    warning?: { label: string; detail: string } | null;
    lowerQuality?: boolean;
    busy?: boolean;
    moving?: boolean;
    onPrevious?: () => void;
    onNext?: () => void;
    onkeep: () => void;
    onrelease: () => void;
    onremove: () => void;
    ondeletion: () => void;
  } = $props();

  const cached = $derived(storage === "cache");
</script>

<SidePanel bind:open title={file.title || file.fileName} description={`${file.artist || "Unknown artist"}${file.album ? ` · ${file.album}` : ""} · ${formatDuration(file.durationMilliseconds)}`} {onPrevious} {onNext}>
  {#snippet artwork()}
    <span class="media-art song-art">
      <ProviderMark id={file.provider || "file"} definition={provider} label={providerName} />
      {#if file.artworkUrl}<img src={adminUrl(file.artworkUrl)} alt="" onerror={event => event.currentTarget.remove()} />{/if}
    </span>
  {/snippet}
  <div class="song-details">
    <div class="lifecycle-note" class:leaving={cached && Boolean(file.expiresAt)}>
      <strong>{cached ? file.expiresAt ? `Leaves the cache ${relativeTime(file.expiresAt)}.` : "A temporary cached copy." : "Never removed by cache cleanup."}</strong>
      <p>{cached ? "Keep this copy to save it permanently." : file.explicitlyKept ? "Your Keep protects this copy. Other household interests also stay protected when you stop keeping." : file.publicationState === "Imported" ? "This imported original is read-only." : file.referenceCount ? "Household interests protect this copy." : "This copy has no active household references."}</p>
    </div>
    {#if warning}<div class="quality-note"><strong>{warning.label}</strong><p>{warning.detail}</p></div>{/if}
    {#if lowerQuality}<p class="lower-quality">This copy is below CD quality.</p>{/if}
    <section aria-label="Song metadata">
      <h3>Song</h3>
      <dl>
        <dt>Artist</dt><dd>{file.artist || "Unknown artist"}</dd>
        <dt>Album</dt><dd>{file.album || "Unknown album"}</dd>
        <dt>Duration</dt><dd>{formatDuration(file.durationMilliseconds)}</dd>
        <dt>Source</dt><dd><span class="source-name"><ProviderMark id={file.provider || "file"} definition={provider} label={providerName} />{providerName}</span></dd>
        {#if file.externalId}<dt>Source ID</dt><dd class="file-value">{file.externalId}</dd>{/if}
        <dt>Quality</dt><dd>{file.quality || file.codec}<small>{qualityDetails(file).join(" · ")}</small></dd>
      </dl>
    </section>
    <section aria-label="Storage lifecycle">
      <h3>Storage</h3>
      <dl>
        <dt>Location</dt><dd>{cached ? file.storage === "transcoded" ? "Quality override cache" : "Track cache" : "Kept"}</dd>
        <dt>Status</dt><dd>{file.publicationState === "Imported" ? "Imported original · read-only" : file.publicationState}</dd>
        <dt>Last access</dt><dd><time datetime={file.lastAccessedAt} title={file.lastAccessedAt}>{relativeTime(file.lastAccessedAt)}</time></dd>
        {#if file.expiresAt}<dt>Leaves cache</dt><dd><time datetime={file.expiresAt} title={file.expiresAt}>{relativeTime(file.expiresAt)}</time></dd>{/if}
        <dt>Your Keep</dt><dd>{file.explicitlyKept ? "Active" : "None"}</dd>
        {#if file.referenceCount != null}<dt>References</dt><dd>{file.referenceCount} household reference{file.referenceCount === 1 ? "" : "s"}</dd>{/if}
      </dl>
    </section>
    <section aria-label="File metadata">
      <h3>File</h3>
      <dl>
        <dt>Size</dt><dd>{file.sizeFormatted} <small>{countLabel(file.size, "byte")}</small></dd>
        <dt>Updated</dt><dd><time datetime={file.lastModified} title={file.lastModified}>{relativeTime(file.lastModified)}</time></dd>
        <dt>Filename</dt><dd class="file-value">{file.fileName}</dd>
        <dt>Path</dt><dd class="file-value">{file.path}</dd>
      </dl>
    </section>
  </div>
  {#snippet footer()}
    <LibraryDownloadActions {file} {storage} {busy} {moving} placement="panel" {onkeep} {onrelease} {onremove} {ondeletion} />
  {/snippet}
</SidePanel>

<style>
  .song-art { position: relative; display: block; width: 100%; height: 100%; overflow: hidden; }
  .song-art > img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
  .song-art :global(.provider-mark) { width: 100%; height: 100%; }
  .song-details { display: grid; gap: var(--space-5); }
  .lifecycle-note, .quality-note { border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); background: var(--color-panel-raised); font-size: var(--text-sm); line-height: 1.5; }
  .lifecycle-note.leaving, .quality-note { border-color: color-mix(in srgb, var(--warning) 30%, var(--color-edge)); background: color-mix(in srgb, var(--warning) 7%, var(--color-panel)); }
  .lifecycle-note strong, .quality-note strong { font-weight: 600; }
  .lifecycle-note p, .quality-note p { margin: var(--space-1) 0 0; color: var(--color-ink-muted); }
  .lower-quality { margin: 0; color: var(--warning); font-size: var(--text-sm); }
  h3 { margin: 0 0 var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; letter-spacing: .06em; text-transform: uppercase; }
  dl { display: grid; grid-template-columns: minmax(0, .8fr) minmax(0, 1.8fr); gap: var(--space-3); margin: 0; font-size: var(--text-sm); line-height: 1.5; }
  dt { color: var(--color-ink-muted); }
  dd { min-width: 0; margin: 0; overflow-wrap: anywhere; }
  dd small { display: block; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .source-name { display: inline-flex; align-items: center; gap: var(--space-2); }
  .source-name :global(.provider-mark) { width: var(--space-4); height: var(--space-4); }
  .file-value { font-size: var(--text-xs); }
</style>
