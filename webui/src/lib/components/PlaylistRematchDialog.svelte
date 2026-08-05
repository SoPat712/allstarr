<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { X } from "lucide-svelte";
  import { playlistLinks, type PlaylistRematchPreview } from "$lib/api";

  let {
    open = $bindable(false),
    onQueued,
  }: {
    open: boolean;
    onQueued: (jobId: string, message: string) => void | Promise<void>;
  } = $props();

  let prepared = $state(false);
  let loading = $state(false);
  let applying = $state(false);
  let confirmed = $state(false);
  let error = $state("");
  let preview = $state<PlaylistRematchPreview | null>(null);

  $effect(() => {
    if (!open) {
      prepared = false;
      loading = false;
      applying = false;
      confirmed = false;
      error = "";
      preview = null;
      return;
    }
    if (prepared) return;
    prepared = true;
    void loadPreview();
  });

  async function loadPreview() {
    loading = true;
    confirmed = false;
    error = "";
    preview = null;
    try {
      preview = await playlistLinks.previewRematch();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Allstarr could not inspect the current match state.";
    } finally {
      loading = false;
    }
  }

  async function applyRematch() {
    if (!preview?.canApply || !confirmed || applying) return;
    applying = true;
    error = "";
    try {
      const result = await playlistLinks.applyRematch(preview.confirmationId);
      open = false;
      await onQueued(
        result.jobId,
        result.created ? "Controlled rematch queued." : "This rematch is already queued.",
      );
    } catch (cause) {
      error = cause instanceof Error
        ? cause.message
        : "The match state changed. Review the preview again.";
      confirmed = false;
    } finally {
      applying = false;
    }
  }

  function tracks(count: number) {
    return `${count.toLocaleString()} ${count === 1 ? "track" : "tracks"}`;
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay match-dialog-overlay" />
    <Dialog.Content class="source-dialog match-dialog rematch-dialog">
      <header>
        <div>
          <Dialog.Title>Review full rematch</Dialog.Title>
          <Dialog.Description>
            Inspect the current account before Allstarr writes any new match decisions.
          </Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close rematch preview">
          <X size={18} aria-hidden="true" />
        </Dialog.Close>
      </header>

      <div class="rematch-body" aria-busy={loading}>
        {#if loading}
          <div class="detail-loading">Checking linked playlists, libraries, and protected choices…</div>
        {:else if error}
          <div class="compact-empty" role="alert">
            <strong>The rematch preview could not be prepared</strong>
            <p>{error}</p>
            <button class="button-secondary" type="button" onclick={() => void loadPreview()}>Try again</button>
          </div>
        {:else if preview}
          <section class="rematch-consequence">
            <div>
              <strong>{preview.canApply ? `${tracks(preview.uniqueTracksToRematch)} ${preview.uniqueTracksToRematch === 1 ? "needs" : "need"} review` : "No rematch needed"}</strong>
              <span class={`status-pill ${preview.canApply ? "suggested" : "healthy"}`}>
                {preview.canApply ? "Ready to queue" : "Current"}
              </span>
            </div>
            <p>
              Allstarr checked {preview.playlistCount.toLocaleString()} linked playlists across
              {preview.libraryCount.toLocaleString()} {preview.libraryCount === 1 ? "library" : "libraries"}.
              Only missing or stale decisions are eligible.
            </p>
          </section>

          <dl class="rematch-counts" aria-label="Rematch preview counts">
            <div><dt>Local</dt><dd>{preview.localRows.toLocaleString()}</dd></div>
            <div><dt>Exact provider</dt><dd>{preview.exactProviderRows.toLocaleString()}</dd></div>
            <div><dt>Generic external</dt><dd>{preview.genericExternalRows.toLocaleString()}</dd></div>
            <div><dt>Unresolved</dt><dd>{preview.unresolvedRows.toLocaleString()}</dd></div>
            <div><dt>Protected manual</dt><dd>{preview.confirmedManualRows.toLocaleString()}</dd></div>
            <div><dt>Stale revision</dt><dd>{preview.staleRevisionRows.toLocaleString()}</dd></div>
            <div><dt>Conflicting</dt><dd>{preview.conflictingRows.toLocaleString()}</dd></div>
            <div><dt>Rows to rematch</dt><dd>{preview.rowsToRematch.toLocaleString()}</dd></div>
          </dl>

          <p class="rematch-safety">
            Playlist order, repeated songs, manual choices, and saved provider routes stay intact.
            This job never downloads or plays media. Rows that change after this preview are skipped.
          </p>

          {#if preview.canApply}
            <label class="rematch-confirm">
              <input type="checkbox" bind:checked={confirmed} />
              <span>I reviewed these counts and want to queue this exact rematch.</span>
            </label>
          {/if}
        {/if}
      </div>

      <footer class="rematch-footer">
        <Dialog.Close class="button-secondary">{preview?.canApply ? "Cancel" : "Close"}</Dialog.Close>
        {#if preview?.canApply}
          <button class="button-primary" type="button" disabled={!confirmed || applying} onclick={() => void applyRematch()}>
            {applying ? "Queueing rematch…" : `Rematch ${tracks(preview.uniqueTracksToRematch)}`}
          </button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  .rematch-dialog{max-height:min(860px,calc(100dvh - 32px));width:min(720px,calc(100vw - 32px))}
  .rematch-body{display:grid;gap:18px;min-height:160px;overflow:auto;padding:20px}
  .rematch-consequence{display:grid;gap:8px}
  .rematch-consequence>div{align-items:center;display:flex;gap:12px;justify-content:space-between}
  .rematch-consequence strong{font-size:1.1rem}
  .rematch-consequence p,.rematch-safety{color:var(--color-text-muted);line-height:1.55;margin:0}
  .rematch-counts{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));margin:0}
  .rematch-counts div{border-block-start:1px solid var(--color-edge);display:grid;gap:4px;min-width:0;padding:14px 12px}
  .rematch-counts dt{color:var(--color-text-muted);font-size:.78rem;line-height:1.3}
  .rematch-counts dd{font-size:1.15rem;font-weight:700;margin:0}
  .rematch-confirm{align-items:start;background:var(--color-surface-raised);border:1px solid var(--color-edge);border-radius:12px;cursor:pointer;display:grid;gap:10px;grid-template-columns:auto minmax(0,1fr);padding:14px}
  .rematch-confirm input{margin-block-start:2px}
  @media(max-width:620px){.rematch-counts{grid-template-columns:repeat(2,minmax(0,1fr))}.rematch-footer{align-items:stretch;display:grid}.rematch-footer>*{width:100%}}
</style>
