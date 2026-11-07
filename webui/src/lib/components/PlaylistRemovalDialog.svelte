<script lang="ts">
  import type { PlaylistRemovalPreview } from "$lib/api";
  import { playlistRemovalConfirmation } from "$lib/playlists";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Button } from "$lib/components/ui/button";

  let {
    open = $bindable(false),
    name,
    preview,
    loading = false,
    previewError = "",
    disabled = false,
    error = "",
    onRetry,
    onConfirm,
  }: {
    open: boolean;
    name: string;
    preview: PlaylistRemovalPreview | null;
    loading?: boolean;
    previewError?: string;
    disabled?: boolean;
    error?: string;
    onRetry: () => void | Promise<void>;
    onConfirm: () => void | Promise<void>;
  } = $props();

  const copy = $derived(playlistRemovalConfirmation(name, preview?.userAddedSongs.length ?? 0));
</script>

<ConfirmDialog
  bind:open
  title={copy.title}
  description={copy.description}
  confirmLabel="Remove from my apps"
  {disabled}
  confirmDisabled={loading || !preview || Boolean(previewError)}
  closeOnConfirm={false}
  {error}
  {onConfirm}
>
  {#if loading}
    <p class="removal-preview-status" role="status">Checking songs added in Jellyfin…</p>
  {:else if previewError}
    <div class="removal-preview-error">
      <p class="notice-error" role="alert">{previewError}</p>
      <Button variant="text" {disabled} onclick={() => void onRetry()}>Try again</Button>
    </div>
  {:else if preview && copy.userAddedSummary}
    <div class="removal-songs">
      <p>{copy.userAddedSummary}</p>
      <details>
        <summary>See songs</summary>
        <ul aria-label="Songs you added in Jellyfin">
          {#each preview.userAddedSongs as song}
            <li><span>{song.name}</span>{#if song.artist}<small>{song.artist}</small>{/if}</li>
          {/each}
        </ul>
      </details>
    </div>
  {/if}
</ConfirmDialog>

<style>
  .removal-preview-status, .removal-songs { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .removal-preview-status, .removal-preview-error, .removal-songs { margin: var(--space-3) var(--space-4) 0; }
  .removal-songs p { margin: 0; }
  .removal-preview-error { display: grid; justify-items: start; gap: var(--space-2); }
  .removal-preview-error p { margin: 0; }
  .removal-songs { display: grid; min-width: 0; gap: var(--space-3); }
  details { min-width: 0; border: 1px solid var(--color-edge); border-radius: var(--radius-md); }
  summary { min-height: 44px; padding: var(--space-3); color: var(--color-signal-text); cursor: pointer; }
  ul { max-height: min(35dvh, 14rem); margin: 0; padding: 0 var(--space-3) var(--space-3); list-style: none; overflow: auto; }
  li { display: grid; gap: var(--space-1); padding-block: var(--space-2); overflow-wrap: anywhere; }
  li + li { border-top: 1px solid var(--color-edge); }
  li span { color: var(--color-ink); }
  small { font-size: var(--text-xs); }
</style>
