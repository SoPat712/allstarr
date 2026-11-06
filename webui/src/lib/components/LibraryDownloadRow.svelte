<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { ChevronDown } from "@lucide/svelte";
  import { adminUrl } from "$lib/admin-url";
  import type { ManagedDownload, ProviderDefinition } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import LibraryDownloadActions from "$lib/components/LibraryDownloadActions.svelte";
  import { relativeTime } from "$lib/activity";

  let { file, copies, storage, provider, providerName, quality, warnings = [], lowerQuality = false,
    size, lifecycle, expanded = false, copiesId, selected = false, busy = false, moving = false,
    onopen, ontoggle, onkeep, onrelease, onremove, ondeletion }: {
    file: ManagedDownload;
    copies: ManagedDownload[];
    storage: "cache" | "kept";
    provider?: ProviderDefinition;
    providerName: string;
    quality: string;
    warnings?: { label: string; detail: string }[];
    lowerQuality?: boolean;
    size: string;
    lifecycle: string;
    expanded?: boolean;
    copiesId?: string;
    selected?: boolean;
    busy?: boolean;
    moving?: boolean;
    onopen: () => void;
    ontoggle?: () => void;
    onkeep: () => void;
    onrelease: () => void;
    onremove: () => void;
    ondeletion: () => void;
  } = $props();
</script>

<tr class="download-row library-download-row" class:selected class:attention={lowerQuality} data-path={file.path}>
  <td class="song-cell">
    <button type="button" class="open-song" aria-label={`Open song details for ${file.title || file.fileName}`} onclick={onopen}></button>
    <div class="song-copy">
      <span class="media-art library-download-art">
        <ProviderMark id={file.provider || "file"} definition={provider} label={providerName} />
        {#if file.artworkUrl}<img src={adminUrl(file.artworkUrl)} alt="" loading="lazy" onerror={event => event.currentTarget.remove()} />{/if}
      </span>
      <div class="song-description">
        <div class="song-title"><strong>{file.title || file.fileName}</strong>
        {#if copies.length > 1 && ontoggle}
          <button type="button" class="copies-toggle" aria-label={`${expanded ? "Hide" : "Show"} ${countLabel(copies.length, "copy", "copies")} of ${file.title || file.fileName}`} aria-expanded={expanded} aria-controls={copiesId} onclick={ontoggle}>
            {countLabel(copies.length, "copy", "copies")}<ChevronDown size={14} aria-hidden="true" class={expanded ? "expanded" : ""} />
          </button>
        {/if}
        </div>
        <small>{file.artist || "Unknown artist"}{file.album ? ` · ${file.album}` : ""}</small>
        <small class="phone-facts">{storage === "cache" ? `Leaves ${lifecycle}` : lifecycle} · {quality}</small>

      </div>
    </div>
  </td>
  <td class="source-cell"><span class="source-name"><ProviderMark id={file.provider || "file"} definition={provider} label={providerName} /><span class="source-label" title={providerName}>{providerName}</span></span></td>
  <td class="quality-cell" class:has-warning={warnings.length > 0}>
    <span class:lower-quality={lowerQuality}>{quality}</span>
    {#each warnings as warning}<span class="quality-check-badge" title={warning.detail}>{warning.label}</span>{/each}
  </td>
  <td class="lifecycle-cell"><span>{lifecycle}</span>{#if file.publicationState === "Imported"}<small>Imported original · read-only</small>{/if}</td>
  <td class="size-cell"><span>{size}</span><span class="sr-only">Updated {relativeTime(file.lastModified)}</span></td>
  <td class="download-actions library-download-actions">
    <LibraryDownloadActions {file} {storage} {busy} {moving} {onkeep} {onrelease} {onremove} {ondeletion} />
  </td>
</tr>

<style>
  .library-download-row { position: relative; display: table-row; padding: 0; gap: 0; background: var(--color-panel); }
  .library-download-row:hover, .library-download-row:focus-within { background: var(--color-panel-raised); }
  .library-download-row.selected { background: color-mix(in srgb, var(--color-signal) 9%, var(--color-panel)); }
  td { min-width: 0; border-bottom: 1px solid var(--color-edge); padding: var(--space-2) var(--space-3); color: var(--color-ink); font-size: var(--text-sm); vertical-align: middle; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .song-cell { padding-left: var(--space-4); }
  .song-copy { display: flex; min-width: 0; align-items: center; gap: var(--space-3); }
  .song-description { min-width: 0; }
  .song-title { display: flex; align-items: center; gap: var(--space-2); min-width: 0; }
  .song-title > strong { flex: 1; min-width: 0; }
  strong { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--text-sm); font-weight: 600; line-height: 1.4; }
  small { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; margin-top: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.4; }
  .open-song { position: absolute; z-index: 1; inset: 0; width: 100%; border: 0; border-radius: 0; padding: 0; background: transparent; cursor: pointer; }
  .open-song:focus-visible { outline: 2px solid var(--color-signal); outline-offset: -2px; }
  .library-download-art { position: relative; width: 36px; height: 36px; flex: 0 0 auto; border-radius: var(--radius-sm); overflow: hidden; }
  .library-download-art > img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
  .library-download-art :global(.provider-mark) { width: 100%; height: 100%; }
  .source-name { display: flex; align-items: center; gap: var(--space-2); }
  .source-label { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .source-name :global(.provider-mark) { width: var(--space-4); height: var(--space-4); flex: 0 0 auto; }
  .quality-cell > span { display: block; }
  .quality-cell, .lifecycle-cell, .size-cell { font-variant-numeric: tabular-nums; }
  .size-cell { text-align: right; white-space: nowrap; }
  .library-download-actions { position: relative; z-index: 2; display: table-cell; text-align: right; align-items: center; justify-content: flex-end; gap: var(--space-1); white-space: nowrap; }
  .library-download-actions :global([data-slot="button"]) { width: auto; min-width: 0; }
  .copies-toggle { position: relative; z-index: 2; display: inline-flex; align-items: center; gap: var(--space-1); flex: none; min-height: 24px; margin-top: 0; border: 1px solid var(--color-edge); border-radius: var(--radius-sm); padding: var(--space-1) var(--space-2); background: var(--color-panel-raised); color: var(--color-ink-muted); font-size: var(--text-xs); cursor: pointer; }
  .copies-toggle :global(svg) { transition: transform var(--motion-standard); }
  .copies-toggle :global(.expanded) { transform: rotate(180deg); }
  .quality-check-badge { width: fit-content; margin-top: var(--space-1); border: 1px solid var(--color-edge); border-radius: var(--radius-sm); padding: var(--space-1) var(--space-2); color: var(--warning); font-size: var(--text-xs); }
  .quality-cell > .lower-quality { display: inline-block; width: fit-content; border-radius: 999px; padding: 2px var(--space-2); background: color-mix(in srgb, var(--warning) 18%, var(--color-panel)); color: var(--warning); }
  .phone-facts { display: none; }
  @media (max-width: 899px) {
    .library-download-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; border-bottom: 1px solid var(--color-edge); }
    td { border: 0; padding: var(--space-3) var(--space-2); }
    .song-cell { grid-column: 1; grid-row: 1; padding-left: var(--space-3); padding-bottom: 0; }
    .song-copy { gap: var(--space-2); }
    .song-title > strong { display: -webkit-box; overflow: hidden; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-clamp: 2; }
    .song-description > small:not(.phone-facts) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .phone-facts { display: block; }
    .source-cell { grid-column: 1; grid-row: 2; padding-top: var(--space-1); padding-left: calc(36px + var(--space-3) + var(--space-2)); color: var(--color-ink-muted); font-size: var(--text-xs); }
    .quality-cell, .lifecycle-cell, .size-cell { display: none; }
    .quality-cell.has-warning { display: flex; grid-column: 1 / -1; gap: var(--space-2); padding-top: 0; padding-left: calc(36px + var(--space-3) + var(--space-2)); }
    .quality-cell.has-warning > span:not(.quality-check-badge) { display: none; }
    .library-download-actions { display: flex; grid-column: 2; grid-row: 1 / 3; padding-right: var(--space-2); }
    .library-download-actions :global([data-slot="button"]) { min-height: 44px; padding-inline: var(--space-2); font-size: var(--text-xs); }
    .library-download-actions :global([data-slot="button"] svg) { display: none; }
    .library-download-art { width: 36px; height: 36px; }
    .copies-toggle { min-height: 44px; }
  }
  @media (prefers-reduced-motion: reduce) { .copies-toggle :global(svg) { transition: none; } }
</style>
