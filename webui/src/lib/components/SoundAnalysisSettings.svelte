<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { audioMuse, mixes, ApiError, type AudioMuseConnection, type AudioMuseStatus } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Switch } from "$lib/components/ui/switch";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";

  let status = $state<AudioMuseStatus | null>(null), connection = $state<AudioMuseConnection | null>(null);
  let publishEnabled = $state<boolean | null>(null), publishError = $state("");
  let connectionOpen = $state(false), connectionLoading = $state(false), refreshing = $state(false);
  let address = $state(""), key = $state(""), connectionError = $state(""), error = $state(""), action = $state("");
  let refreshRequest = 0, connectionRequest = 0, disposed = false;
  const statusLabel = $derived(!status ? "Checking connection…" : status.reason === null
    ? `Connected · ${status.coverageComplete ? "" : "At least "}${countLabel(status.analyzedSongs, "song")} analyzed`
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
    const previous = connection.baseUrl, changingKey = Boolean(key);
    action = "connection"; connectionError = ""; refreshRequest++; refreshing = false;
    try {
      const next = await audioMuse.configure(address.trim(), key, connection.revision);
      if (disposed) return;
      connection = next; key = ""; connectionOpen = false;
      toast.success("AudioMuse connection saved.", !changingKey ? { label: "Undo", run: async () => {
        if (action || !connection) return;
        action = "connection";
        try { connection = await audioMuse.configure(previous, "", connection.revision); await refresh(); }
        catch { toast.error("The connection could not be restored. Try again."); }
        finally { action = ""; void refresh(); }
      } } : undefined);
    } catch (cause) {
      if (!disposed) connectionError = cause instanceof ApiError && cause.status === 409
        ? "The connection changed. Close and reopen to load it before saving."
        : "The connection could not be saved. Check the address and try again.";
    } finally { if (!disposed) { action = ""; void refresh(); } }
  }
  async function setPublishing(enabled: boolean, undo = true) {
    if (action || publishEnabled === null) return;
    const previous = publishEnabled;
    action = "publish"; publishError = ""; refreshRequest++; refreshing = false;
    try {
      await mixes.setPublishSetting(enabled);
      if (disposed) return;
      publishEnabled = enabled;
      toast.success(enabled ? "Listeners can add mixes to their apps." : "Listeners can no longer add mixes to their apps.", undo ? { label: "Undo", run: () => setPublishing(previous, false) } : undefined);
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
  <PageStatus items={[{ label: statusLabel }, { label: connection?.keyConfigured ? "Key saved" : "No key saved" }]} />
  <div class="sound-refresh"><RefreshButton label="Refresh sound analysis" disabled={Boolean(action)} onrefresh={refresh} /></div>
  {#if error}<ErrorState title="Some sound analysis settings are unavailable" detail={error} onretry={refresh} />{/if}
  {#if !status && refreshing}<RowsSkeleton count={2} />{:else}
    <section class="settings-card" aria-label="AudioMuse connection"><header><h3>AudioMuse</h3></header>
      <div class="settings-row"><div><strong>Connection</strong><small>{countLabel(status?.analyzedSongs ?? 0, "song")} analyzed{connection?.keyConfigured ? " · key saved" : ""}</small></div><div class="connection-actions"><StatePill tone={status?.reason === null ? "ok" : "neutral"} text={status?.reason === null ? "Connected" : "Not connected"} /><Button type="button" variant="outline" disabled={Boolean(action)} onclick={editConnection}>Edit connection</Button></div></div>
      <label class="settings-row"><div><strong>Let listeners add mixes to their apps</strong><small>Each listener picks which mixes show as playlists.</small></div><span class="settings-toggle"><Switch checked={publishEnabled === true} disabled={publishEnabled === null || Boolean(action)} aria-label="Let listeners add mixes to their apps" onchange={event => void setPublishing(event.currentTarget.checked)} /></span></label>
      {#if publishError}<p class="notice-error settings-impact" role="alert">{publishError}</p>{/if}
    </section>
  {/if}
</div>

<Dialog.Root bind:open={connectionOpen} onOpenChange={open => { if (!open) { key = ""; connectionRequest++; } }}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog settings-flow-dialog">
    <header><div><Dialog.Title>AudioMuse connection</Dialog.Title><Dialog.Description>Connect AudioMuse to analyze sounds in your library.</Dialog.Description></div></header>
    <form onsubmit={event => { event.preventDefault(); void configure(); }}>
      <div class="connection-fields">
        {#if connectionLoading}<p role="status">Loading connection…</p>{/if}
        <label class="field"><span>AudioMuse address</span><input type="url" bind:value={address} maxlength="2048" disabled={connectionLoading || Boolean(action)} /></label>
        <label class="field"><span>API key</span><input type="password" aria-label="API key" aria-describedby="sound-key-help" bind:value={key} autocomplete="new-password" maxlength="4096" disabled={connectionLoading || Boolean(action)} /><small id="sound-key-help">{connection?.keyConfigured ? "Key saved. Leave blank to keep it." : "Enter your AudioMuse key."}</small></label>
        {#if connectionError}<p class="notice-error" role="alert">{connectionError}</p>{/if}
      </div>
      <footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "text" })} disabled={Boolean(action)}>Cancel</Dialog.Close><Button type="submit" disabled={connectionLoading || Boolean(action) || !connection}>{action === "connection" ? "Saving…" : "Save"}</Button></footer>
    </form>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>

<style>
  .sound-refresh { display: flex; justify-content: end; }
  .sound-settings, .connection-fields { display: grid; gap: var(--space-4); min-width: 0; }
  .connection-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: end; gap: var(--space-2); }
  .connection-fields { padding: var(--space-4); }
  @media (max-width: 650px) { .connection-actions { justify-content: start; } }
</style>
