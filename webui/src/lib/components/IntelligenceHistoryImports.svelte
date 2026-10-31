<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, historyImports, type HistoryImport } from "$lib/api";
  import { formatCount } from "$lib/intelligence";
  import { combineHistoryPreviews, groupHistoryImports, historyBytes, historyDateRange, historyError, historyPlays, historySource, importBatchProgress, importPhase, importProgress, importState, importSummary } from "$lib/history-import";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Progress } from "$lib/components/ui/progress";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";

  type PreviewFile = { name: string; item?: HistoryImport; error?: string };
  let { onchanged }: { onchanged: () => void } = $props();
  let items = $state<HistoryImport[]>([]), loading = $state(true), error = $state(""), summary = $state("");
  let batches = $state<Record<string, string>>({});
  const storageKey = "allstarr.history-import-batches";
  const groups = $derived(groupHistoryImports(items, batches));
  let picker: HTMLInputElement;
  let uploading = $state(false), bytes = $state(0), size = $state(0), action = $state(false);
  let fileNumber = $state(0), fileCount = $state(0);
  let previewFiles = $state<PreviewFile[]>([]), previewOpen = $state(false), previewError = $state("");
  let ownsPreviews = false;
  const previews = $derived(previewFiles.flatMap(row => row.item ? [row.item] : []));
  const total = $derived(combineHistoryPreviews(previews));
  const waiting = $derived(previews.filter(item => importPhase(item) === "previewed"));
  let removing = $state<HistoryImport[]>([]), deleteOpen = $state(false), deleteError = $state("");
  let disposed = false, pending: Promise<void> | null = null;
  const uploadController = new AbortController();
  const active = (item: HistoryImport) => ["pending", "running"].includes(importPhase(item));
  const source = (rows: HistoryImport[]) => [...new Set(rows.map(item => historySource(item.preview.format)))].join(", ") || "Listening history";

  function saveBatches() {
    try { localStorage.setItem(storageKey, JSON.stringify(batches)); } catch { /* Browser storage is optional. */ }
  }

  function replace(item: HistoryImport) {
    items = items.some(row => row.importId === item.importId) ? items.map(row => row.importId === item.importId ? item : row) : [item, ...items];
  }

  function forget(item: HistoryImport) {
    items = items.filter(row => row.importId !== item.importId);
    delete batches[item.importId]; saveBatches();
  }

  function groupState(rows: HistoryImport[]) {
    if (rows.length === 1) return importState(rows[0]);
    if (rows.some(active)) {
      const progress = importBatchProgress(rows);
      return `Importing ${progress.percent}% · File ${progress.file} of ${rows.length}`;
    }
    const failed = rows.filter(item => importPhase(item) === "failed").length;
    return failed ? `${failed} ${failed === 1 ? "file" : "files"} failed` : rows.every(item => importPhase(item) === "completed") ? "Done" : "Waiting";
  }

  async function load(showError = false) {
    if (pending) return pending;
    pending = (async () => {
      try {
        const listed = await historyImports.list();
        const visibleBatches = new Set(listed.map(item => batches[item.importId]).filter(Boolean));
        const missing = Object.keys(batches).filter(id => visibleBatches.has(batches[id]) && !listed.some(item => item.importId === id));
        for (const id of missing) {
          try { listed.push(await historyImports.get(id)); }
          catch (failure) { if (failure instanceof ApiError && failure.status === 404) { delete batches[id]; saveBatches(); } else throw failure; }
        }
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

  async function upload(files: File[]) {
    if (!files.length || uploading || action) return;
    error = ""; summary = ""; previewFiles = []; previewError = ""; ownsPreviews = true;
    uploading = true; fileCount = files.length; fileNumber = 1; bytes = 0; size = files[0].size;
    let batch = "";
    await pending;
    for (const [index, file] of files.entries()) {
      if (disposed) break;
      fileNumber = index + 1; bytes = 0; size = file.size;
      let reason = !/\.(zip|json|csv)$/i.test(file.name) ? "Not a history file" : !file.size || file.size > 500 * 1024 ** 2 ? "Choose a history file up to 500 MB." : "";
      if (!reason) {
        try {
          const item = await historyImports.upload(file, value => { if (!disposed) bytes = value; }, uploadController.signal);
          if (disposed) break;
          replace(item);
          if (files.length > 1) { batch ||= item.importId; batches[item.importId] = batch; saveBatches(); }
          previewFiles = [...previewFiles, { name: file.name, item }];
        } catch (failure) {
          reason = failure instanceof ApiError && (failure.details as { error?: string } | null)?.error === "history_import_format_unsupported"
            ? "Not a history file" : historyError(failure, "Your history could not be uploaded. Try again.");
        }
      }
      if (reason && !disposed) previewFiles = [...previewFiles, { name: file.name, error: reason }];
    }
    if (!disposed) {
      uploading = false;
      if (files.length === 1 && !previews.length) error = previewFiles[0]?.error ?? "Your history could not be uploaded. Try again.";
      else previewOpen = true;
    }
  }

  function review(rows: HistoryImport[]) {
    previewFiles = rows.map(item => ({ name: item.displayFileName, item }));
    ownsPreviews = false; previewError = ""; previewOpen = true;
  }

  async function cancel() {
    if (action) return;
    if (!ownsPreviews) { previewOpen = false; return; }
    action = true; previewError = "";
    await pending;
    for (const item of waiting) {
      try {
        await historyImports.remove(item);
        if (disposed) return;
        forget(item);
        previewFiles = previewFiles.filter(row => row.item?.importId !== item.importId);
      } catch (failure) { if (!disposed) previewError = historyError(failure, "A preview could not be deleted. Try Cancel again."); }
    }
    if (!disposed) { if (!previewError) previewOpen = false; action = false; }
  }

  async function confirm() {
    if (!waiting.length || action) return;
    action = true; previewError = "";
    await pending;
    for (const preview of waiting) {
      try {
        const item = await historyImports.confirm(preview);
        if (disposed) return;
        replace(item);
        previewFiles = previewFiles.map(row => row.item?.importId === item.importId ? { name: row.name, item } : row);
        if (importPhase(item) === "completed") { summary = importSummary(item); onchanged(); }
      } catch (failure) {
        if (disposed) return;
        const reason = historyError(failure, "Your history could not be imported. Try again.");
        previewError = reason;
        previewFiles = previewFiles.map(row => row.item?.importId === preview.importId ? { ...row, error: reason } : row);
      }
    }
    if (!disposed) { if (!previewError) previewOpen = false; action = false; }
  }

  async function remove() {
    if (!removing.length || action) return;
    action = true; deleteError = "";
    await pending;
    let changed = false;
    for (const item of removing) {
      try {
        await historyImports.remove(item);
        if (disposed) return;
        forget(item); removing = removing.filter(row => row.importId !== item.importId); changed = true;
      } catch (failure) { if (!disposed) deleteError = historyError(failure, "This import could not be deleted. Try again."); }
    }
    if (!disposed) { if (!removing.length) deleteOpen = false; if (changed) { summary = ""; onchanged(); } action = false; }
  }

  const scheduler = createRefreshScheduler(async () => { if (!uploading && !action) await load(); });
  onMount(() => {
    try {
      const saved: unknown = JSON.parse(localStorage.getItem(storageKey) ?? "{}");
      if (saved && typeof saved === "object" && !Array.isArray(saved)) batches = Object.fromEntries(Object.entries(saved).filter(([, value]) => typeof value === "string"));
    } catch { /* Browser storage is optional. */ }
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; uploadController.abort(); unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="settings-card history-imports" aria-labelledby="history-imports-heading">
  <header><div><h3 id="history-imports-heading">Listening history from other services</h3><small>Bring in your history from Spotify, Last.fm, ListenBrainz, Maloja or Koito. Completed imported plays appear in Recap at their original dates and never scrobble.</small></div></header>
  <div class="settings-row">
    <div><a class="spotify-hint" href="https://www.spotify.com/account/privacy/" target="_blank" rel="noreferrer">From Spotify, request 'Extended streaming history' in your privacy settings.</a></div>
    <Button disabled={uploading || action} onclick={() => picker.click()}>Import history</Button>
    <input bind:this={picker} hidden type="file" multiple accept=".zip,.json,.csv" aria-label="History file" onchange={() => { const files = Array.from(picker.files ?? []); picker.value = ""; void upload(files); }} />
  </div>
  {#if uploading}
    {@const percent = Math.floor(100 * (fileNumber - 1 + Math.min(1, bytes / Math.max(1, size))) / fileCount)}
    <div class="history-message" role="status"><p>File {fileNumber} of {fileCount} · {percent}%</p><p>{bytes >= size ? "Checking your history…" : `Uploading ${historyBytes(bytes)} of ${historyBytes(size)}…`}</p><Progress value={percent} aria-label="History upload" /></div>
  {/if}
  {#if error}<p class="history-message notice-error" role="alert">{error}</p>{/if}
  {#if summary}<p class="history-message" role="status">{summary}</p>{/if}
  <header><h3>Your imports</h3><RefreshButton variant="secondary" label="Refresh imports" disabled={uploading || action} onrefresh={() => load(true)} /></header>
  {#if loading}<p class="history-message" role="status">Loading imports…</p>
  {:else if !items.length}<p class="history-message">No imports yet.</p>
  {:else}
    <ul class="history-list">
      {#each groups as group (group.id)}
        {@const combined = combineHistoryPreviews(group.items)}
        <li>
          <div class="settings-row">
            <div>
              {#if group.items.length > 1}<strong>{source(group.items)} · {group.items.length} files · {historyDateRange(combined.earliest, combined.latest, true)} · {historyPlays(combined.completed)}</strong>
              {:else}<strong>{source(group.items)}</strong><small>{historyDateRange(combined.earliest, combined.latest)}</small><small>{historyPlays(combined.completed)} in file · Import date unavailable</small>{/if}
              <span class="history-state">{groupState(group.items)}</span>
              {#if group.items.some(active)}<Progress value={group.items.length > 1 ? importBatchProgress(group.items).percent : importProgress(group.items[0])} aria-label={`${source(group.items)} import`} />{/if}
            </div>
            <div class="history-actions">{#if group.items.some(item => importPhase(item) === "previewed")}<Button variant="secondary" disabled={uploading || action} onclick={() => review(group.items)}>Review</Button>{/if}<Button variant="ghost" disabled={uploading || action} onclick={() => { removing = [...group.items]; deleteError = ""; deleteOpen = true; }}>Delete</Button></div>
          </div>
          {#if group.items.length > 1}
            <details class="history-files"><summary class="disclosure-summary"><DisclosureLabel title="Show files" /></summary>
              <ul class="history-file-list">{#each group.items as item (item.importId)}<li><strong>{item.displayFileName}</strong><small>{historyPlays(item.preview.completed)} · {historyDateRange(item.preview.earliest, item.preview.latest)}</small><span>{importState(item)}</span></li>{/each}</ul>
            </details>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
</section>

<Dialog.Root bind:open={previewOpen}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog" onEscapeKeydown={event => { event.preventDefault(); void cancel(); }} onInteractOutside={event => { event.preventDefault(); void cancel(); }}>
    <header><div><Dialog.Title>Import history</Dialog.Title><Dialog.Description>Review your history before importing. Completed imported plays appear in Recap at their original dates and never scrobble.</Dialog.Description></div></header>
    <form onsubmit={event => { event.preventDefault(); void confirm(); }}>
      <div class="history-preview">
        {#if previews.length}
          <strong>{source(previews)}</strong>
          <p>{historyDateRange(total.earliest, total.latest)}</p>
          <ul><li>{historyPlays(total.completed)}</li><li>{formatCount(total.skipped)} {total.skipped === 1 ? "skip" : "skips"}</li><li>{formatCount(total.songs)} {total.songs === 1 ? "song" : "songs"}</li><li>{formatCount(total.malformed)} {total.malformed === 1 ? "row" : "rows"} not recognized</li></ul>
          {#if previews.length > 1}<small>The same song can appear in more than one file.</small>{/if}
          {#if total.unresolvedNewRows}<p>{formatCount(total.unresolvedNewRows)} {total.unresolvedNewRows === 1 ? "listen couldn't" : "listens couldn't"} be matched to known songs.</p>{/if}
          {#if total.duplicateInFile || total.duplicateExisting}<p>{formatCount(total.duplicateInFile + total.duplicateExisting)} duplicate {total.duplicateInFile + total.duplicateExisting === 1 ? "listen will" : "listens will"} be left out.</p>{/if}
          {#if total.outsideRetentionRows}<p>{formatCount(total.outsideRetentionRows)} older listens are outside your history retention and will be left out.</p>{/if}
        {/if}
        {#if previewFiles.length > 1}
          <ul class="history-file-list" aria-label="Selected files">
            {#each previewFiles as row}<li><strong>{row.name}</strong>{#if row.item}<small>{historyPlays(row.item.preview.completed)} · {historyDateRange(row.item.preview.earliest, row.item.preview.latest)}</small>{/if}<span>{row.error ? `${row.item ? "" : "Skipped · "}${row.error}` : row.item && importPhase(row.item) !== "previewed" ? importState(row.item) : "Ready"}</span></li>{/each}
          </ul>
        {/if}
        {#if previewError}<p class="notice-error" role="alert">{previewError}</p>{/if}
      </div>
      <footer class="dialog-actions"><Button type="button" variant="secondary" disabled={action} onclick={() => void cancel()}>Cancel</Button><Button type="submit" disabled={action || !waiting.length}>{action ? "Please wait…" : previewFiles.length > 1 ? "Import all" : "Import"}</Button></footer>
    </form>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>

<ConfirmDialog bind:open={deleteOpen} title={removing.length > 1 ? "Delete these imports?" : "Delete this import?"} description={removing.length > 1 ? "All files in this group, their plays and their effect on your taste are removed." : "Its plays and their effect on your taste are removed."} confirmLabel="Delete" disabled={action} closeOnConfirm={false} error={deleteError} onConfirm={remove} />

<style>
  .history-imports :global(.settings-row) { grid-template-columns: minmax(0, 1fr) auto; }
  .spotify-hint { color: var(--color-signal-text); font-size: var(--text-sm); text-decoration: underline; text-underline-offset: 3px; display: flex; align-items: center; min-height: 44px; }
  .history-message { padding: var(--space-4) var(--space-6); font-size: var(--text-sm); }
  .history-message p { margin-bottom: var(--space-2); }
  .history-list, .history-preview ul, .history-file-list { list-style: none; margin: 0; padding: 0; }
  .history-list > li + li { border-top: 1px solid var(--color-edge); }
  .history-state { font-size: var(--text-sm); overflow-wrap: anywhere; }
  .history-list :global([data-slot="progress"]) { margin-top: var(--space-2); }
  .history-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); justify-content: end; }
  .history-preview { display: grid; min-width: 0; gap: var(--space-3); }
  .history-preview p, .history-preview small, .history-file-list small { color: var(--color-ink-muted); }
  .history-preview li, .history-file-list li { padding-block: var(--space-2); }
  .history-preview li + li, .history-file-list li + li { border-top: 1px solid var(--color-edge); }
  .history-file-list li { display: grid; gap: var(--space-1); font-size: var(--text-sm); overflow-wrap: anywhere; }
  .history-list strong { overflow-wrap: anywhere; }
  .history-files { padding: 0 var(--space-6) var(--space-4); }
  .history-files summary { min-height: 44px; }
  @media (max-width: 600px) { .history-imports :global(.settings-row) { grid-template-columns: minmax(0, 1fr); } .history-actions { justify-content: start; } }
</style>
