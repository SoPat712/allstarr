<script lang="ts">
  import { Download, MoreHorizontal } from "@lucide/svelte";
  import { downloads, type ManagedDownload } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";

  let { file, storage, busy = false, moving = false, placement = "row", onkeep, onrelease, onremove, ondeletion, onadd, addUnavailable = "" }: {
    file: ManagedDownload;
    storage: "cache" | "kept";
    busy?: boolean;
    moving?: boolean;
    placement?: "row" | "panel" | "menu";
    onadd?: () => void;
    addUnavailable?: string;
    onkeep: () => void;
    onrelease: () => void;
    onremove: () => void;
    ondeletion: () => void;
  } = $props();

  const cached = $derived(storage === "cache");
  const canKeep = $derived(cached && file.removable && !file.explicitlyKept);
</script>

{#if placement === "panel"}
  <div class="song-panel-actions">
    <div class="destructive-actions">
      {#if file.removable}
        {#if file.explicitlyKept}<Button class="danger-text" variant="text" disabled={busy} onclick={onrelease}>Stop keeping</Button>
        {:else}<Button class="danger-text" variant="text" disabled={busy || moving} onclick={onremove}>Remove</Button>{/if}
      {/if}
      {#if !cached && file.canDeleteKeptCopy}<Button class="danger-text" variant="text" disabled={busy} onclick={ondeletion}>Delete kept copy…</Button>{/if}
    </div>
    <div class="primary-actions">
      <Button variant={canKeep || moving ? "outline" : "default"} href={downloads.fileUrl(file.path, storage)}><Download size={16} data-icon="inline-start" aria-hidden="true" />Download</Button>
      {#if moving}<Button disabled>Moving to Kept…</Button>
      {:else if canKeep}<Button disabled={busy} onclick={onkeep}>Keep</Button>{/if}
    </div>
  </div>
{:else}
  {#if placement !== "menu"}
  {#if moving}<Button variant="outline" size="sm" disabled>Moving to Kept…</Button>
  {:else if canKeep}<Button variant="outline" size="sm" disabled={busy} title={`Keep this ${file.quality || file.codec} copy`} onclick={onkeep}>Keep</Button>
  {:else}<Button variant="outline" size="sm" href={downloads.fileUrl(file.path, storage)}><Download size={16} data-icon="inline-start" aria-hidden="true" />Download</Button>{/if}
  {/if}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger class="icon-button library-download-menu" aria-label={`Actions for ${file.title || file.fileName}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
    <DropdownMenu.Portal>
      <DropdownMenu.Content class="bits-menu" align="end" sideOffset={6}>
        {#if cached || placement === "menu"}
          <DropdownMenu.Item class="bits-menu-item">
            {#snippet child({ props })}<a {...props} href={downloads.fileUrl(file.path, storage)}>Download</a>{/snippet}
          </DropdownMenu.Item>
        {/if}
        {#if onadd}<DropdownMenu.Item class="bits-menu-item" disabled={busy || Boolean(addUnavailable)} title={addUnavailable} onSelect={onadd}>Add to library</DropdownMenu.Item>{/if}
        {#if file.removable}
          {#if file.explicitlyKept}<DropdownMenu.Item class="bits-menu-item danger-item" disabled={busy} onSelect={onrelease}>Stop keeping</DropdownMenu.Item>
          {:else}<DropdownMenu.Item class="bits-menu-item danger-item" disabled={busy || moving} onSelect={onremove}>Remove</DropdownMenu.Item>{/if}
        {/if}
        {#if !cached && file.canDeleteKeptCopy}<DropdownMenu.Item class="bits-menu-item danger-item" disabled={busy} onSelect={ondeletion}>Delete kept copy…</DropdownMenu.Item>{/if}
        {#if !cached && !file.removable && !file.canDeleteKeptCopy}<DropdownMenu.Item class="bits-menu-item" disabled>Protected copy</DropdownMenu.Item>{/if}
      </DropdownMenu.Content>
    </DropdownMenu.Portal>
  </DropdownMenu.Root>
{/if}

<style>
  :global(.library-download-menu) { position: relative; display: inline-flex; width: var(--control-md); min-width: var(--control-md); height: var(--control-md); min-height: var(--control-md); align-items: center; justify-content: center; flex: 0 0 auto; color: var(--color-ink-muted); }
  .song-panel-actions { display: flex; width: 100%; min-width: 0; align-items: center; justify-content: space-between; gap: var(--space-2); }
  .destructive-actions, .primary-actions { display: flex; align-items: center; gap: var(--space-2); }
  .primary-actions { margin-left: auto; }
  .song-panel-actions :global([data-slot="button"]) { padding-inline: var(--space-2); font-size: .75rem; }
  :global(.song-panel-actions .danger-text) { color: var(--danger); }
  @media (max-width: 899px) { :global(.library-download-menu) { width: 44px; min-width: 44px; height: 44px; min-height: 44px; } .destructive-actions, .primary-actions { min-width: 0; } .song-panel-actions :global([data-slot="button"]) { min-height: 44px; } }
  @media (max-width: 380px) { .destructive-actions { gap: var(--space-1); }.song-panel-actions :global([data-slot="button"]) { padding-inline: var(--space-1); font-size: .6875rem; }.primary-actions :global(svg) { display: none; } }
</style>
