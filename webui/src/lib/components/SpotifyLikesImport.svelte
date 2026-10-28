<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, spotifyLikes, type SpotifyLikesImportStatus } from "$lib/api";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { newReportRequestId as newRequestId } from "$lib/support";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Progress } from "$lib/components/ui/progress";

  let { accountId }: { accountId: string } = $props();
  const description = "Adds your Spotify likes as hearts. Nothing is downloaded. Running it again only adds new likes.";
  const labels = {
    Pending: "Waiting to import", Running: "Importing Spotify likes", RetryScheduled: "Waiting to retry",
    Deferred: "Waiting to continue", Succeeded: "Import complete", Failed: "Import needs attention", Cancelled: "Import stopped",
  };
  let confirmation = $state(false);
  let starting = $state(false);
  let jobId = $state("");
  let status = $state<SpotifyLikesImportStatus | null>(null);
  let error = $state("");
  let startError = $state("");
  let requestId = "";
  let reading = false;
  let mounted = false;
  const storageKey = () => `allstarr:spotify-likes:${accountId}`;
  const active = $derived(Boolean(jobId) && (!status || !["Succeeded", "Failed", "Cancelled"].includes(status.state)));
  const summary = $derived(status ? `Added ${status.imported} hearts · ${status.alreadyHearted} already hearted · ${status.needsReview} need a check · ${status.notFound} not found` : "");
  const checked = $derived(status ? status.total === null ? `${status.processed} likes checked` : `${status.processed} of ${status.total} likes checked` : "");
  const stoppedMessage = $derived.by(() => {
    if (!status || status.state !== "Failed") return "";
    if (status.errorCode === "spotify_likes_unauthorized") return "Check this account’s connection, then start a new import. Hearts already added are saved.";
    if (status.errorCode === "spotify_likes_source_changed") return "Your Spotify likes changed during the import. Start a new import to continue. Hearts already added are saved.";
    return "Check this account’s connection and start a new import. Hearts already added are saved.";
  });

  function remember(id: string) {
    try {
      if (id) localStorage.setItem(storageKey(), id);
      else localStorage.removeItem(storageKey());
    } catch { /* Importing also works when browser storage is unavailable. */ }
  }

  async function refreshStatus() {
    if (!jobId || reading) return;
    const currentId = jobId;
    reading = true;
    try {
      const next = await spotifyLikes.status(currentId);
      if (!mounted || jobId !== currentId) return;
      status = next;
      error = "";
    } catch (cause) {
      if (!mounted || jobId !== currentId) return;
      if (cause instanceof ApiError && cause.status === 404) {
        jobId = "";
        status = null;
        remember("");
        error = "The saved import is no longer available. You can start a new import.";
      } else {
        error = "Progress could not be refreshed. Refresh its status to check the import.";
      }
    } finally { reading = false; }
  }

  async function startImport() {
    if (starting || active) return;
    starting = true;
    startError = "";
    requestId ||= newRequestId();
    try {
      const receipt = await spotifyLikes.start(accountId, requestId);
      if (!mounted) return;
      if (!receipt.jobId) throw new Error("Missing import receipt");
      jobId = receipt.jobId;
      status = null;
      error = "";
      remember(jobId);
      requestId = "";
      confirmation = false;
      await refreshStatus();
    } catch {
      if (mounted) startError = "The import could not be started. Try again with this account.";
    } finally { starting = false; }
  }

  const scheduler = createRefreshScheduler(refreshStatus);
  onMount(() => {
    mounted = true;
    try { jobId = localStorage.getItem(storageKey()) ?? ""; } catch { /* Browser storage is optional. */ }
    if (jobId) void refreshStatus();
    const unsubscribe = liveUpdates.subscribe(() => { if (active || error && jobId) scheduler.schedule(); });
    return () => { mounted = false; unsubscribe(); scheduler.cancel(); };
  });
</script>

<section class="spotify-likes" aria-label="Spotify likes">
  <div class="spotify-likes-actions">
    <Button variant="secondary" disabled={starting || active} onclick={() => { startError = ""; confirmation = true; }}>Import Spotify likes</Button>
    {#if error && jobId}<RefreshButton variant="secondary" label="Refresh import status" onrefresh={refreshStatus} />{/if}
  </div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  {#if jobId}
    <div class="spotify-likes-progress" role="status" aria-live="polite" aria-atomic="true">
      <strong>{status ? labels[status.state] : "Checking import status…"}</strong>
      {#if status}
        {#if active}
          <span>{checked}</span>
          <Progress value={status.total === null ? null : Math.min(status.processed, Math.max(1, status.total))}
            max={status.total === null ? 100 : Math.max(1, status.total)} aria-label="Spotify likes checked" />
        {/if}
        <p>{summary}</p>
        {#if status.state === "RetryScheduled" || status.state === "Deferred"}<small>The import will continue automatically. Hearts already added are saved.</small>{/if}
        {#if stoppedMessage}<small>{stoppedMessage}</small>{/if}
        {#if status.state === "Cancelled"}<small>Hearts already added are saved. You can run another import.</small>{/if}
      {/if}
    </div>
  {/if}
</section>

<ConfirmDialog bind:open={confirmation} title="Import Spotify likes?" {description}
  confirmLabel="Import Spotify likes" confirmVariant="default" closeOnConfirm={false}
  disabled={starting} error={startError} onConfirm={startImport} />

<style>
  .spotify-likes { display: grid; gap: var(--space-3); min-width: 0; }
  .spotify-likes-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .spotify-likes-progress { display: grid; min-width: 0; gap: var(--space-2); font-size: var(--text-sm); overflow-wrap: anywhere; }
  .spotify-likes-progress p { margin: 0; }
  .spotify-likes-progress span, .spotify-likes-progress small { color: var(--color-ink-muted); }
  @media (max-width: 40rem) { .spotify-likes-actions { display: grid; } }
</style>
