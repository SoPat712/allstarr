<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { adminUrl } from "$lib/admin-url";
  import { playlistLinks, type ManagedDownload, type ProviderDefinition } from "$lib/api";
  import { relativeTime } from "$lib/activity";
  import { canonicalProviderId } from "$lib/sources";
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

  let playlists = $state<{ id: string; name: string }[]>([]);
  let membershipUnavailable = $state(false);
  let membershipRequest = 0;
  $effect(() => {
    const song = file;
    if (!open) { membershipRequest++; return; }
    const request = ++membershipRequest;
    playlists = []; membershipUnavailable = false;
    void (async () => {
      try {
        const links = (await playlistLinks.list()).playlistLinks ?? [];
        const found: { id: string; name: string }[] = [];
        for (const link of links) {
          const detail = await playlistLinks.details(link.id);
          if (detail.tracks?.some(track => track.providerRoutes?.some(route => canonicalProviderId(route.providerId) === canonicalProviderId(song.provider ?? "") && route.externalId === song.externalId)))
            found.push({ id: link.id, name: link.name });
        }
        if (request === membershipRequest) playlists = found;
      } catch { if (request === membershipRequest) membershipUnavailable = true; }
    })();
  });
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
    <dl aria-label="Song facts">
      <dt>Played from</dt><dd><span class="source-name"><ProviderMark id={file.provider || "file"} definition={provider} label={providerName} />{providerName}</span></dd>
      <dt>Quality</dt><dd>{qualityDetails(file).join(" · ") || file.quality}</dd>
      <dt>Size</dt><dd>{file.sizeFormatted}</dd>
      <dt>Saved</dt><dd><time datetime={file.lastModified} title={file.lastModified}>{relativeTime(file.lastModified)}</time></dd>
      <dt>Last played</dt><dd>Not provided</dd>
    </dl>
    <section aria-label="In your playlists"><h3>In your playlists</h3>
      {#each playlists as playlist}<a class="playlist-link" href={`#/library/playlists?id=${encodeURIComponent(playlist.id)}`}>{playlist.name}<span aria-hidden="true">›</span></a>
      {:else}<p class="empty-playlists">{membershipUnavailable ? "Playlist membership could not be loaded." : "No playlists found for this copy."}</p>{/each}
    </section>
    <details class="advanced"><summary><span>Advanced</span><span aria-hidden="true">▾</span></summary><dl>
      <dt>Artist</dt><dd>{file.artist || "Unknown artist"}</dd><dt>Album</dt><dd>{file.album || "Unknown album"}</dd>
      <dt>Duration</dt><dd>{formatDuration(file.durationMilliseconds)}</dd>
      {#if file.externalId}<dt>Source ID</dt><dd class="file-value">{file.externalId}</dd>{/if}
      <dt>Status</dt><dd>{file.publicationState === "Imported" ? "Imported original · read-only" : file.publicationState}</dd>
      <dt>Your Keep</dt><dd>{file.explicitlyKept ? "Active" : "None"}</dd>
      <dt>Last accessed</dt><dd><time datetime={file.lastAccessedAt} title={file.lastAccessedAt}>{relativeTime(file.lastAccessedAt)}</time></dd>
      {#if file.referenceCount != null}<dt>References</dt><dd>{countLabel(file.referenceCount, "household reference")}</dd>{/if}
      <dt>File size</dt><dd>{countLabel(file.size, "byte")}</dd>
      <dt>Filename</dt><dd class="file-value">{file.fileName}</dd><dt>Path</dt><dd class="file-value">{file.path}</dd>
    </dl></details>
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
  .lifecycle-note.leaving strong { color: var(--warning); }
  .lifecycle-note strong, .quality-note strong { font-weight: 600; }
  .lifecycle-note p, .quality-note p { margin: var(--space-1) 0 0; color: var(--color-ink-muted); }
  .lower-quality { margin: 0; color: var(--warning); font-size: var(--text-sm); }
  h3 { margin: 0 0 var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; letter-spacing: .06em; text-transform: uppercase; }
  dl { display: grid; grid-template-columns: minmax(0, .8fr) minmax(0, 1.8fr); gap: var(--space-3); margin: 0; font-size: var(--text-sm); line-height: 1.5; }
  dt { color: var(--color-ink-muted); }
  dd { min-width: 0; margin: 0; overflow-wrap: anywhere; }
  .source-name { display: inline-flex; align-items: center; gap: var(--space-2); }
  .source-name :global(.provider-mark) { width: var(--space-4); height: var(--space-4); }
  .playlist-link { display: flex; align-items: center; justify-content: space-between; min-height: 44px; border-bottom: 1px solid var(--color-edge); color: var(--color-signal-text); font-size: var(--text-sm); }
  .empty-playlists { margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); }
  .advanced { border-top: 1px solid var(--color-edge); }
  .advanced > summary { display: flex; align-items: center; justify-content: space-between; min-height: 44px; color: var(--color-signal-text); font-size: var(--text-sm); cursor: pointer; list-style: none; }
  .advanced > summary::-webkit-details-marker { display: none; }
  .file-value { font-size: var(--text-xs); }
</style>
