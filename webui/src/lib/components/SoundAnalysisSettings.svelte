<script lang="ts">
  import { onMount } from "svelte";
  import { audioMuse, mixes, ApiError, type AudioMuseConnection, type AudioMuseStatus } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import RefreshButton from "$lib/components/RefreshButton.svelte";

  let status = $state<AudioMuseStatus | null>(null), connection = $state<AudioMuseConnection | null>(null);
  let publishEnabled = $state<boolean | null>(null), publishError = $state("");
  let connectionOpen = $state(false), connectionLoading = $state(false), refreshing = $state(false);
  let address = $state(""), key = $state(""), connectionError = $state(""), error = $state(""), action = $state("");
  let refreshRequest = 0, connectionRequest = 0, disposed = false;
  const statusLabel = $derived(!status ? "Checking connection…" : status.reason === null
    ? `Connected · ${status.coverageComplete ? "" : "At least "}${status.analyzedSongs.toLocaleString()} songs analyzed`
    : ["not_enabled", "not_set_up"].includes(status.reason) ? "Not set up" : "Can't reach AudioMuse");

  async function refresh() {
    if (action) return;
    const current = ++refreshRequest;
    refreshing = true;
    const results = await Promise.allSettled([audioMuse.status(), audioMuse.connection(), mixes.publishSetting()]);
    if (disposed || current !== refreshRequest) return;
    if (results[0].status === "fulfilled") status = results[0].value;
    if (results[1].status === "fulfilled" && !connectionOpen) connection = results[1].value;
    if (results[2].status === "fulfilled") publishEnabled = results[2].value.enabled;
    error = results.some(result => result.status === "rejected") ? "Some sound analysis settings could not be loaded. Refresh to try again." : "";
    refreshing = false;
  }
  const refreshScheduler = createRefreshScheduler(refresh);

  async function editConnection() {
    const current = ++connectionRequest;
    connectionOpen = true; connectionLoading = true; connectionError = ""; key = ""; address = ""; connection = null;
    try {
      const next = await audioMuse.connection();
      if (!disposed && current === connectionRequest) { connection = next; address = next.baseUrl; }
    } catch { if (!disposed && current === connectionRequest) connectionError = "The connection could not be loaded. Close and try again."; }
    finally { if (!disposed && current === connectionRequest) connectionLoading = false; }
  }
  async function configure() {
    if (action || !connection) return;
    action = "connection"; connectionError = ""; refreshRequest++; refreshing = false;
    try {
      const next = await audioMuse.configure(address.trim(), key, connection.revision);
      if (disposed) return;
      connection = next; key = ""; connectionOpen = false;
      toast.success("AudioMuse connection saved.");
    } catch (cause) {
      if (!disposed) connectionError = cause instanceof ApiError && cause.status === 409
        ? "The connection changed. Close and reopen to load it before saving."
        : "The connection could not be saved. Check the address and try again.";
    } finally { if (!disposed) { action = ""; void refresh(); } }
  }
  async function setPublishing(enabled: boolean) {
    if (action || publishEnabled === null) return;
    action = "publish"; publishError = ""; refreshRequest++; refreshing = false;
    try {
      await mixes.setPublishSetting(enabled);
      if (disposed) return;
      publishEnabled = enabled;
      toast.success(enabled ? "Listeners can add mixes to their apps." : "Listeners can no longer add mixes to their apps.");
    } catch {
      if (!disposed) publishError = "The mix setting could not be saved. Try again.";
    } finally { if (!disposed) { action = ""; void refresh(); } }
  }
  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(refreshScheduler.schedule);
    return () => { disposed = true; refreshRequest++; connectionRequest++; key = ""; unsubscribe(); refreshScheduler.cancel(); };
  });
</script>

<div class="sound-settings" aria-busy={refreshing}>
  <div class="sound-settings-actions"><RefreshButton variant="secondary" label="Refresh sound analysis" disabled={Boolean(action)} onrefresh={refresh} /></div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  <section class="settings-card" aria-label="AudioMuse connection">
    <header><h3>AudioMuse connection</h3></header>
    <div class="settings-row"><div><strong>{statusLabel}</strong>{#if connection?.keyConfigured}<small>Key saved</small>{/if}</div><Button type="button" variant="secondary" disabled={Boolean(action)} onclick={editConnection}>Edit connection</Button></div>
  </section>
  <section class="settings-card" aria-label="Mix publishing">
    <label class="settings-row">
      <div><strong>Let listeners add mixes to their apps</strong><small>Each listener chooses which mixes to show as playlists in their music apps.</small>{#if publishEnabled === null}<small>Checking availability…</small>{/if}</div>
      <span class="settings-toggle"><Checkbox checked={publishEnabled === true} disabled={publishEnabled === null || Boolean(action)} aria-label="Let listeners add mixes to their apps" onCheckedChange={enabled => void setPublishing(enabled === true)} /></span>
    </label>
    {#if publishError}<p class="notice-error" role="alert">{publishError}</p>{/if}
  </section>
</div>

<Dialog.Root bind:open={connectionOpen} onOpenChange={open => { if (!open) { key = ""; connectionRequest++; } }}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog">
    <header><div><Dialog.Title>AudioMuse connection</Dialog.Title><Dialog.Description>Connect AudioMuse to analyze sounds in your library.</Dialog.Description></div></header>
    <form onsubmit={event => { event.preventDefault(); void configure(); }}>
      <div class="connection-fields">
        {#if connectionLoading}<p role="status">Loading connection…</p>{/if}
        <label class="field"><span>AudioMuse address</span><input type="url" bind:value={address} maxlength="2048" disabled={connectionLoading || Boolean(action)} /></label>
        <label class="field"><span>API key</span><input type="password" aria-label="API key" aria-describedby="sound-key-help" bind:value={key} autocomplete="new-password" maxlength="4096" disabled={connectionLoading || Boolean(action)} /><small id="sound-key-help">{connection?.keyConfigured ? "Key saved. Leave blank to keep it." : "Enter your AudioMuse key."}</small></label>
        {#if connectionError}<p class="notice-error" role="alert">{connectionError}</p>{/if}
      </div>
      <footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "secondary" })} disabled={Boolean(action)}>Cancel</Dialog.Close><Button type="submit" disabled={connectionLoading || Boolean(action) || !connection}>{action === "connection" ? "Saving…" : "Save"}</Button></footer>
    </form>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>

<style>
  .sound-settings, .connection-fields { display: grid; gap: var(--space-4); min-width: 0; }
  .sound-settings-actions { display: flex; justify-content: end; }
  .settings-toggle :global([data-slot="checkbox"]) { box-sizing: border-box; width: 44px; height: 44px; padding: 0; margin: 0; border: 0; background: transparent; outline: 0; }
  .settings-toggle :global([data-slot="checkbox"]:focus-visible) { outline: 2px solid var(--md-sys-color-primary); }
  .settings-toggle :global([data-slot="checkbox-indicator"]) { width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px; }
  .settings-toggle :global([data-checked] [data-slot="checkbox-indicator"]) { border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
</style>
