<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { X } from "lucide-svelte";
  import {
    playlistLinks,
    type PlaylistLink,
    type PlaylistSourceUpdatePreview,
  } from "$lib/api";

  let {
    open = $bindable(false),
    playlist,
    providerName,
    targetName,
    onQueued,
  }: {
    open: boolean;
    playlist: PlaylistLink | null;
    providerName: string;
    targetName: string;
    onQueued: (jobId: string, message: string) => void | Promise<void>;
  } = $props();

  let prepared = $state(false);
  let loading = $state(false);
  let applying = $state(false);
  let error = $state("");
  let preview = $state<PlaylistSourceUpdatePreview | null>(null);

  $effect(() => {
    if (!open) {
      prepared = false;
      loading = false;
      applying = false;
      error = "";
      preview = null;
      return;
    }
    if (prepared || !playlist) return;
    prepared = true;
    void loadPreview();
  });

  async function loadPreview() {
    if (!playlist) return;
    loading = true;
    error = "";
    preview = null;
    try {
      preview = await playlistLinks.previewSourceUpdate(playlist.id);
    } catch (cause) {
      error = cause instanceof Error
        ? cause.message
        : `Allstarr could not compare the playlists in ${providerName} and ${targetName}.`;
    } finally {
      loading = false;
    }
  }

  async function applyUpdate() {
    if (!playlist || !preview?.canApply || applying) return;
    applying = true;
    error = "";
    try {
      const result = await playlistLinks.applySourceUpdate(
        playlist.id,
        preview.expectedRevision,
        preview.confirmationId,
      );
      const message = result.created
        ? `${preview.providerName} update queued.`
        : `${preview.providerName} update is already queued.`;
      open = false;
      await onQueued(result.jobId, message);
    } catch (cause) {
      error = cause instanceof Error
        ? cause.message
        : "One of the playlists changed. Review the comparison again.";
    } finally {
      applying = false;
    }
  }

  function changeLabel(change: PlaylistSourceUpdatePreview["changes"][number]) {
    if (change.kind === "add") return `Add at ${change.toPosition}`;
    if (change.kind === "remove") return `Remove from ${change.fromPosition}`;
    return `Move ${change.fromPosition} → ${change.toPosition}`;
  }

  function songs(count: number) {
    return `${count} ${count === 1 ? "song" : "songs"}`;
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay match-dialog-overlay" />
    <Dialog.Content class="source-dialog match-dialog playlist-source-update-dialog">
      <header>
        <div>
          <Dialog.Title>Update {providerName}?</Dialog.Title>
          <Dialog.Description>
            Review exactly what Allstarr will change before anything is sent to {providerName}.
          </Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label={`Close ${providerName} update preview`}>
          <X size={18} aria-hidden="true" />
        </Dialog.Close>
      </header>

      <div class="playlist-source-update-body" aria-busy={loading}>
        {#if loading}
          <div class="detail-loading">Comparing the playlists in {providerName} and {targetName}…</div>
        {:else if error}
          <div class="compact-empty" role="alert">
            <strong>The playlists could not be compared</strong>
            <p>{error}</p>
            <Button variant="secondary" onclick={() => void loadPreview()}>Try again</Button>
          </div>
        {:else if preview}
          <section class="playlist-source-update-summary">
            {#if preview.canApply}
              <p class="playlist-source-update-consequence">
                Allstarr will update <strong>“{preview.sourcePlaylistName}” in {preview.providerName}</strong>
                to match <strong>“{preview.backendPlaylistName}” in {targetName}</strong>.
                Allstarr will not change “{preview.backendPlaylistName}” in {targetName}.
              </p>
            {:else}
              <p class="playlist-source-update-consequence">{preview.message}</p>
              <p>Allstarr will not change “{preview.backendPlaylistName}” in {targetName}.</p>
            {/if}
            <p class="playlist-source-update-check">
              Allstarr checked both playlists just now. If either changes before this runs, nothing will be updated.
            </p>
          </section>

          <dl class="playlist-source-update-counts" aria-label="Planned playlist changes">
            <div><dt>Add</dt><dd>{preview.addedCount}</dd></div>
            <div><dt>Remove</dt><dd>{preview.removedCount}</dd></div>
            <div><dt>Move</dt><dd>{preview.movedCount}</dd></div>
            <div><dt>Skip</dt><dd>{preview.skippedCount}</dd></div>
          </dl>

          <p class="playlist-source-update-total">
            {preview.providerName} currently has {songs(preview.currentCount)}.
            The confirmed result has {songs(preview.includedCount)}.
            {#if preview.duplicateCount}
              Repeated songs stay repeated ({preview.duplicateCount}).
            {/if}
          </p>

          {#if preview.changes.length}
            <section class="playlist-source-update-list">
              <h3>Changes in {preview.providerName}</h3>
              <ol>
                {#each preview.changes as change}
                  <li>
                    <span>{changeLabel(change)}</span>
                    <strong>{change.title}</strong>
                    <small>{change.artist}</small>
                  </li>
                {/each}
              </ol>
              {#if preview.unshownChangeCount}
                <p>{preview.unshownChangeCount} more changes are included in the checked totals.</p>
              {/if}
            </section>
          {/if}

          {#if preview.skipped.length}
            <section class="playlist-source-update-list playlist-source-update-skips">
              <h3>Not sent to {preview.providerName}</h3>
              <ol>
                {#each preview.skipped as skipped}
                  <li>
                    <span>#{skipped.position}</span>
                    <strong>{skipped.title}</strong>
                    <small>{skipped.artist} · {skipped.reason}</small>
                  </li>
                {/each}
              </ol>
              {#if preview.unshownSkippedCount}
                <p>{preview.unshownSkippedCount} more skipped songs are included in the checked total.</p>
              {/if}
            </section>
          {/if}
        {/if}
      </div>

      <footer>
        <Dialog.Close class={buttonVariants({ variant: "secondary" })}>{preview?.canApply ? "Cancel" : "Close"}</Dialog.Close>
        {#if preview?.canApply}
          <Button disabled={applying} onclick={() => void applyUpdate()}>
            {applying ? `Queueing ${preview.providerName} update…` : `Update ${preview.providerName}`}
          </Button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
