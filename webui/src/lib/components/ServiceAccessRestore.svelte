<script lang="ts">
  import { onMount } from "svelte";
  import { settings, setupAccess, type EnvMigrationPreview } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import { markAccessRestored, savedBeforeRestart, waitForRestart } from "$lib/restart";
  let preview = $state<EnvMigrationPreview | null>(null), busy = $state(false), saved = $state(false), error = $state("");
  const abort = new AbortController();
  onMount(() => {
    void (async () => {
      try {
        const [access, status] = await Promise.all([setupAccess.backend(), settings.migrationStatus()]);
        if (access.settings.supported && !access.settings.configured && status.serviceAccessAvailable)
          preview = await settings.previewServiceAccess();
      } catch (cause) { error = cause instanceof Error ? cause.message : "Legacy access could not be reviewed."; }
    })();
    return () => abort.abort();
  });
  async function restore() {
    if (!preview || busy) return;
    busy = true; error = "";
    try {
      if (!saved) {
        try {
          const result = await settings.applyMigration(preview);
          if (result.backendServiceAccess !== "verified") throw new Error("The legacy key could not be verified. Enter administrator credentials below to restore access.");
        } catch (cause) { if (!savedBeforeRestart(cause)) throw cause; }
        saved = true;
      }
      await waitForRestart(abort.signal);
      markAccessRestored();
      window.location.reload();
    } catch (cause) { error = cause instanceof Error ? cause.message : "Access could not be restored. Retry or enter administrator credentials."; }
    finally { busy = false; }
  }
</script>
{#if preview?.backend?.serviceAccessAction === "verify_then_encrypt"}
  <section class="settings-card p-5 space-y-4" aria-label="Restore legacy media-server access">
    <h3>Restore media-server access</h3>
    <p>A legacy Jellyfin key is available for {preview.backend.endpoint}. Confirm to verify its administrator permissions, encrypt it, and restart Allstarr. This restores playlist sync and library scans.</p>
    <Button disabled={busy || !preview.canApply} onclick={() => void restore()}>{busy ? "Restoring access…" : saved ? "Check restart again" : "Verify and restore access"}</Button>
    {#if saved}<p role="status">Restoring access. Allstarr is restarting and this page reloads when it is back…</p>{/if}
    {#if error}<p role="alert" class="notice-error">{error}</p>{/if}
  </section>
{/if}
