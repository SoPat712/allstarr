<script lang="ts">
  import type { Snippet } from "svelte";
  import type { DownloadSort } from "$lib/downloads";

  type Sort = DownloadSort | "lifecycle";
  let { label, kept = false, sort = "track", reversed = false, onsort, children, class: className = "" }: {
    label: string;
    kept?: boolean;
    sort?: Sort;
    reversed?: boolean;
    onsort?: (value: Sort) => void;
    children: Snippet;
    class?: string;
  } = $props();
  const columns: { key: Sort; label: string; className: string }[] = [
    { key: "track", label: "Song", className: "song-heading" },
    { key: "provider", label: "Source", className: "source-heading" },
    { key: "quality", label: "Quality", className: "quality-heading" },
    { key: "lifecycle", label: "", className: "lifecycle-heading" },
    { key: "size", label: "Size", className: "size-heading" },
  ];
  function order(key: Sort) {
    if (key !== sort) return "none";
    const descending = key === "quality" || key === "size" || key === "updated";
    return descending !== reversed ? "descending" : "ascending";
  }
</script>

<table class={`library-downloads-table ${className}`} class:kept aria-label={label}>
  <thead><tr>
    {#each columns as column}<th class={column.className} scope="col" aria-sort={onsort ? order(column.key) : undefined}>{#if onsort}<button type="button" onclick={() => onsort?.(column.key)}>{column.key === "lifecycle" ? kept ? "Kept by" : "Leaves cache" : column.label}</button>{:else}{column.key === "lifecycle" ? kept ? "Kept by" : "Leaves cache" : column.label}{/if}</th>{/each}
    <th class="actions-heading" scope="col"><span class="sr-only">Actions</span></th>
  </tr></thead>
  {@render children()}
</table>

<style>
  .library-downloads-table { width: 100%; min-width: 800px; table-layout: fixed; border-collapse: collapse; text-align: left; }
  th { border-bottom: 1px solid var(--color-edge); padding: var(--space-2) var(--space-3); color: var(--color-ink-muted); font-size: .68rem; font-weight: 600; text-transform: uppercase; letter-spacing: .05em; }
  th button { min-height: var(--control-sm); border: 0; padding: 0; background: transparent; color: inherit; cursor: pointer; font: inherit; text-transform: inherit; letter-spacing: inherit; text-align: left; white-space: nowrap; }
  th[aria-sort="ascending"] button, th[aria-sort="descending"] button { color: var(--color-signal-text); font-weight: 600; }
  .song-heading { padding-left: var(--space-4); }
  .source-heading { width: 160px; }
  .lifecycle-heading { width: 120px; }
  .size-heading { width: 108px; text-align: right; }
  .quality-heading { width: 210px; }
  .kept .source-heading { width: 130px; }
  .kept .lifecycle-heading { width: 150px; }
  .kept .quality-heading { width: 150px; }
  .kept .size-heading { width: 90px; }
  .actions-heading { width: 160px; }
  .copies-table { min-width: 0; border: 1px solid var(--color-edge); }
  @media (min-width: 900px) {
    @container downloads (max-width: 850px) {
      .library-downloads-table { min-width: 0; }
      .song-heading { width: auto; padding-left: var(--space-2); }
      .source-heading, .kept .source-heading { width: 100px; }
      .quality-heading, .kept .quality-heading { width: 90px; }
      .actions-heading { width: 128px; }
      .lifecycle-heading, .size-heading, :global(.lifecycle-cell), :global(.size-cell) { display: none; }
      th, :global(.library-download-row > td) { padding-inline: var(--space-2); }
      :global(.library-download-row > td), :global(.song-title > strong) { font-size: var(--text-xs); }
      :global(.song-copy) { gap: var(--space-2); }
      :global(.library-download-art) { width: 36px; height: 36px; }
      :global(.source-name) { flex-wrap: wrap; gap: var(--space-1); overflow-wrap: normal; }
      :global(.library-download-actions [data-slot="button"]) { padding-inline: var(--space-2); font-size: var(--text-xs); }
      :global(.library-download-actions [data-slot="button"] svg) { display: none; }
      :global(.library-download-menu) { width: 32px; min-width: 32px; height: 32px; min-height: 32px; }
    }
  }
  @media (max-width: 899px) {
    .library-downloads-table, .library-downloads-table :global(tbody) { display: block; width: 100%; min-width: 0; }
    thead { display: none; }
  }
</style>
