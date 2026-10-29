<script lang="ts">
  import { onMount } from "svelte";
  import { historyImports, type HistoryImport } from "$lib/api";
  import { formatCount } from "$lib/intelligence";
  import { historyBytes, historyDateRange, historyError, historyPlays, historySource, importPhase, importProgress, importState, importSummary } from "$lib/history-import";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Progress } from "$lib/components/ui/progress";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";

  let { onchanged }: { onchanged: () => void } = $props();
  let items = $state<HistoryImport[]>([]), loading = $state(true), error = $state(""), summary = $state("");
  let picker: HTMLInputElement;
  let uploading = $state(false), bytes = $state(0), size = $state(0), action = $state(false);
  let preview = $state<HistoryImport | null>(null), previewOpen = $state(false), previewError = $state("");
  let removing = $state<HistoryImport | null>(null), deleteOpen = $state(false), deleteError = $state("");
  let disposed = false, pending: Promise<void> | null = null;
  const uploadController = new AbortController();
  const active = (item: HistoryImport) => ["pending", "running"].includes(importPhase(item));

  function replace(item: HistoryImport) {
    items = items.some(row => row.importId === item.importId) ? items.map(row => row.importId === item.importId ? item : row) : [item, ...items];
  }

  async function load(showError = false) {
    if (pending) return pending;
    pending = (async () => {
      try {
        const listed = await historyImports.list();
        const next = await Promise.all(listed.map(item => active(item) ? historyImports.get(item.importId) : item));
        if (disposed) return;
        for (const item of next) {
          const previous = items.find(row => row.importId === item.importId);
          if (previous && importPhase(previous) !== "completed" && importPhase(item) === "completed") { summary = importSummary(item); onchanged(); }
        }
        items = next; error = "";
      } catch (failure) { if (!disposed && (showError || loading)) error = historyError(failure, "Your imports could not be loaded. Refresh to try again."); }
      finally { pending = null; if (!disposed) loading = false; }
    })();
    return pending;
  }

  async function upload(file?: File) {
    if (!file || uploading || action) return;
    error = ""; summary = "";
    if (!file.size || file.size > 500 * 1024 ** 2) { error = "Choose a history file up to 500 MB."; return; }
    uploading = true; bytes = 0; size = file.size;
    try {
      await pending;
      const item = await historyImports.upload(file, value => { if (!disposed) bytes = value; }, uploadController.signal);
      if (!disposed) { replace(item); preview = item; previewError = ""; previewOpen = true; }
    } catch (failure) { if (!disposed) error = historyError(failure, "Your history could not be uploaded. Try again."); }
    finally { if (!disposed) uploading = false; }
  }

  function review(item: HistoryImport) { preview = item; previewError = ""; previewOpen = true; }
  async function confirm() {
    if (!preview || action) return;
    action = true; previewError = "";
    try {
      await pending;
      const item = await historyImports.confirm(preview);
      if (!disposed) { replace(item); previewOpen = false; if (importPhase(item) === "completed") { summary = importSummary(item); onchanged(); } }
    } catch (failure) { if (!disposed) previewError = historyError(failure, "Your history could not be imported. Try again."); }
    finally { if (!disposed) action = false; }
  }

  async function remove() {
    if (!removing || action) return;
    action = true; deleteError = "";
    try {
      await pending;
      await historyImports.remove(removing);
      if (!disposed) { items = items.filter(item => item.importId !== removing?.importId); deleteOpen = false; summary = ""; onchanged(); }
    } catch (failure) { if (!disposed) deleteError = historyError(failure, "This import could not be deleted. Try again."); }
    finally { if (!disposed) action = false; }
  }

  const scheduler = createRefreshScheduler(async () => { if (!uploading && !action) await load(); });
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; uploadController.abort(); unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="settings-card history-imports" aria-labelledby="history-imports-heading">
  <header><div><h3 id="history-imports-heading">Listening history from other services</h3><small>Bring in your history from Spotify, Last.fm, ListenBrainz, Maloja or Koito. Imported plays show separately and never scrobble.</small></div></header>
  <div class="settings-row">
    <div><a class="spotify-hint" href="https://www.spotify.com/account/privacy/" target="_blank" rel="noreferrer">From Spotify, request 'Extended streaming history' in your privacy settings.</a></div>
    <Button disabled={uploading || action} onclick={() => picker.click()}>Import history</Button>
    <input bind:this={picker} hidden type="file" accept=".zip,.json,.csv" aria-label="History file" onchange={() => { const file = picker.files?.[0]; picker.value = ""; void upload(file); }} />
  </div>
  {#if uploading}
    <div class="history-message" role="status"><p>{bytes >= size ? "Checking your history…" : `Uploading ${historyBytes(bytes)} of ${historyBytes(size)}…`}</p><Progress value={100 * bytes / size} aria-label="History upload" /></div>
  {/if}
  {#if error}<p class="history-message notice-error" role="alert">{error}</p>{/if}
  {#if summary}<p class="history-message" role="status">{summary}</p>{/if}
  <header><h3>Your imports</h3><RefreshButton variant="secondary" label="Refresh imports" disabled={uploading || action} onrefresh={() => load(true)} /></header>
  {#if loading}<p class="history-message" role="status">Loading imports…</p>
  {:else if !items.length}<p class="history-message">No imports yet.</p>
  {:else}
    <ul class="history-list">
      {#each items as item (item.importId)}
        <li class="settings-row">
          <div><strong>{historySource(item.preview.format)}</strong><small>{historyDateRange(item.preview.earliest, item.preview.latest)}</small><small>{historyPlays(item.preview.completed)} in file · Import date unavailable</small><span class="history-state">{importState(item)}</span>{#if active(item)}<Progress value={importProgress(item)} aria-label={`${historySource(item.preview.format)} import`} />{/if}</div>
          <div class="history-actions">{#if importPhase(item) === "previewed"}<Button variant="secondary" disabled={uploading || action} onclick={() => review(item)}>Review</Button>{/if}<Button variant="ghost" disabled={uploading || action} onclick={() => { removing = item; deleteError = ""; deleteOpen = true; }}>Delete</Button></div>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<Dialog.Root bind:open={previewOpen}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog">
    <header><div><Dialog.Title>Import history</Dialog.Title><Dialog.Description>Review your history before importing. Imported plays show separately and never scrobble.</Dialog.Description></div></header>
    <form onsubmit={event => { event.preventDefault(); void confirm(); }}>
      {#if preview}
        <div class="history-preview">
          <strong>{historySource(preview.preview.format)}</strong>
          <p>{historyDateRange(preview.preview.earliest, preview.preview.latest)}</p>
          <small>{historyBytes(preview.sizeBytes)}</small>
          <ul><li>{historyPlays(preview.preview.completed)}</li><li>{formatCount(preview.preview.skipped)} {preview.preview.skipped === 1 ? "skip" : "skips"}</li><li>{formatCount(preview.preview.songs)} {preview.preview.songs === 1 ? "song" : "songs"}</li><li>{formatCount(preview.preview.malformed)} {preview.preview.malformed === 1 ? "row" : "rows"} not recognized</li></ul>
          {#if preview.preview.unresolvedNewRows}<p>{formatCount(preview.preview.unresolvedNewRows)} {preview.preview.unresolvedNewRows === 1 ? "listen couldn't" : "listens couldn't"} be matched to known songs.</p>{/if}
          {#if preview.preview.duplicateInFile || preview.preview.duplicateExisting}<p>{formatCount(preview.preview.duplicateInFile + preview.preview.duplicateExisting)} duplicate {preview.preview.duplicateInFile + preview.preview.duplicateExisting === 1 ? "listen will" : "listens will"} be left out.</p>{/if}
          {#if preview.preview.outsideRetentionRows}<p>{formatCount(preview.preview.outsideRetentionRows)} older listens are outside your history retention and will be left out.</p>{/if}
          {#if previewError}<p class="notice-error" role="alert">{previewError}</p>{/if}
        </div>
      {/if}
      <footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "secondary" })} disabled={action}>Cancel</Dialog.Close><Button type="submit" disabled={action || !preview}>{action ? "Importing…" : "Import"}</Button></footer>
    </form>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>

<ConfirmDialog bind:open={deleteOpen} title="Delete this import?" description="Its plays and their effect on your taste are removed." confirmLabel="Delete" disabled={action} closeOnConfirm={false} error={deleteError} onConfirm={remove} />

<style>
  .history-imports :global(.settings-row) { grid-template-columns: minmax(0, 1fr) auto; }
  .spotify-hint { color: var(--color-signal-text); font-size: var(--text-sm); text-decoration: underline; text-underline-offset: 3px; display: flex; align-items: center; min-height: 44px; }
  .history-message { padding: var(--space-4) var(--space-6); font-size: var(--text-sm); }
  .history-message p { margin-bottom: var(--space-2); }
  .history-list, .history-preview ul { list-style: none; margin: 0; padding: 0; }
  .history-state { font-size: var(--text-sm); overflow-wrap: anywhere; }
  .history-list :global([data-slot="progress"]) { margin-top: var(--space-2); }
  .history-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); justify-content: end; }
  .history-preview { display: grid; min-width: 0; gap: var(--space-3); }
  .history-preview p, .history-preview small { color: var(--color-ink-muted); }
  .history-preview li { padding-block: var(--space-2); }
  .history-preview li + li { border-top: 1px solid var(--color-edge); }
  @media (max-width: 600px) { .history-imports :global(.settings-row) { grid-template-columns: minmax(0, 1fr); } .history-actions { justify-content: start; } }
</style>
