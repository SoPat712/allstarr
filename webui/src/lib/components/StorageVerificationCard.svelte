<script lang="ts">
  import { onMount } from "svelte";
  import { setupAccess, type FileVerification } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  let status = $state<FileVerification | null>(null), csrf = $state(""), error = $state(""), busy = $state(false), message = $state("");
  async function load() {
    try { const response = await setupAccess.files(); status = response.settings; csrf = response.csrfToken; error = ""; }
    catch (cause) { error = cause instanceof Error ? cause.message : "Saved-file verification is unavailable. Check the data folder and retry."; }
  }
  async function verify() {
    busy = true; error = "";
    try { message = (await setupAccess.verify(csrf)).message; await load(); }
    catch (cause) { error = cause instanceof Error ? cause.message : "Verification could not be queued. Check the mount and retry."; }
    finally { busy = false; }
  }
  onMount(() => { void load(); const timer = setInterval(() => { if (status?.pending) void load(); }, 5000); return () => clearInterval(timer); });
</script>
<article class="settings-card verification-card" aria-label="Saved-file verification">
  <header><h3>Saved-file verification</h3></header>
  <p class="settings-impact">Kept and downloaded songs are checked where they are. Nothing is moved, retagged or deleted.</p>
  {#if status}<p class="settings-impact" role="status">{status.pending ? "Verifying saved files. Follow progress in Activity." : status.needsReview ? "Some saved files need review. Restore the original bytes at their existing paths, then verify again." : status.verifiedAt ? "Saved files verified." : "No copied-folder verification is pending."} {status.files} owned files.</p>{/if}
  {#if message}<p class="settings-impact" role="status">{message}</p>{/if}
  {#if error}<p role="alert" class="notice-error">{error}</p>{/if}
  <footer><Button variant="secondary" disabled={busy || status?.pending || !status?.available} onclick={() => void verify()}>{busy ? "Queuing…" : "Verify saved files"}</Button><a class="button secondary" href="#/activity">Open Activity</a>{#if error}<Button variant="secondary" onclick={() => void load()}>Try again</Button>{/if}</footer>
</article>

<style>
  .verification-card > footer { display: flex; flex-wrap: wrap; gap: var(--space-2); }
</style>
