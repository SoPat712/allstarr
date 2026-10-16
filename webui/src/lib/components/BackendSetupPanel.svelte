<script lang="ts">
  import { onMount } from "svelte";
  import { setupAccess } from "$lib/api";
  import { waitForRestart } from "$lib/restart";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  let { onReady }: { onReady: () => Promise<void> } = $props();
  let status = $state<Awaited<ReturnType<typeof setupAccess.initial>> | null>(null);
  let backend = $state("Jellyfin"), url = $state(""), username = $state(""), password = $state("");
  let scans = $state(false), busy = $state(false), saved = $state(false), error = $state("");
  const abort = new AbortController();
  async function load() {
    error = "";
    try { status = await setupAccess.initial(); }
    catch (cause) { error = cause instanceof Error ? cause.message : "Setup is unavailable. Check the data folder and try again."; }
  }
  async function finishRestart() {
    await waitForRestart(abort.signal);
    await onReady();
  }
  async function save() {
    busy = true; error = "";
    try {
      if (!saved) {
        await setupAccess.connect({ backend, url, username, password, useForLibraryScans: backend === "Jellyfin" && scans }, status?.csrfToken ?? "");
        saved = true;
      }
      password = "";
      await finishRestart();
    } catch (cause) { error = cause instanceof Error ? cause.message : "The media server could not be saved. Check its address and try again."; }
    finally { password = ""; busy = false; }
  }
  onMount(() => { void load(); return () => abort.abort(); });
</script>
<main class="min-h-svh grid place-items-center p-4">
  <section class="panel w-full max-w-lg p-6 sm:p-8" aria-labelledby="connect-title">
    <div class="brand-mark login-brand mb-6" aria-hidden="true">A</div>
    <h1 id="connect-title" class="text-2xl font-semibold">Connect your media server</h1>
    <p class="mt-3 text-ink-muted">Choose Jellyfin or Subsonic / OpenSubsonic. Allstarr verifies an administrator before saving this household's connection.</p>
    {#if saved}<p class="mt-4" role="status">Your connection is saved. Waiting for Allstarr to restart…</p>{/if}
    {#if status && !status.storageReady}<p class="notice-error mt-4">The data folder is not ready. Check its persistent mount and permissions, then restart Allstarr.</p>{/if}
    {#if error}<p role="alert" class="notice-error mt-4">{error}</p>{/if}
    {#if !saved}
      <form class="mt-6 space-y-4" onsubmit={(event) => { event.preventDefault(); void save(); }}>
        <label class="block">Media server type<select class="w-full mt-1" bind:value={backend} disabled={busy}><option>Jellyfin</option><option>Subsonic</option></select></label>
        <label class="block">Media server address<input class="w-full mt-1" type="url" placeholder="http://media-server:8096" required bind:value={url} disabled={busy} /></label>
        <label class="block">Administrator username<input class="w-full mt-1" autocomplete="username" required bind:value={username} disabled={busy} /></label>
        <label class="block">Administrator password<input class="w-full mt-1" type="password" autocomplete="current-password" required bind:value={password} disabled={busy} /></label>
        {#if backend === "Jellyfin"}<label class="flex items-start gap-3"><Checkbox bind:checked={scans} disabled={busy} /><span>Allow background library scans. Only the verified access token is kept encrypted; your password is discarded.</span></label>{/if}
        <Button type="submit" disabled={busy || !status?.storageReady} class="w-full">{busy ? "Verifying…" : "Verify and save"}</Button>
      </form>
    {/if}
    {#if !busy}<Button variant="secondary" class="mt-4 w-full" onclick={() => void (saved ? save() : load())}>{saved ? "Check restart again" : "Check again"}</Button>{/if}
  </section>
</main>
