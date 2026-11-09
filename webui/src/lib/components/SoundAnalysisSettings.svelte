<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { audioMuse, mixes, ApiError, type AudioMuseConnection, type AudioMuseStatus } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Button } from "$lib/components/ui/button";
  import { slide } from "svelte/transition";
  import { motion } from "$lib/motion";
  import { Switch } from "$lib/components/ui/switch";
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
    : ({
      not_enabled: connection?.keyConfigured ? "Add the AudioMuse address to finish" : "Not set up",
      key_rejected: "AudioMuse refused the key",
      unsupported_version: "This AudioMuse version isn't supported",
      not_linked: "AudioMuse isn't linked to this Jellyfin server",
      too_little_analyzed_music: "Waiting for AudioMuse to analyze songs",
      not_set_up: "AudioMuse isn't ready for Allstarr",
    } as Record<string, string>)[status.reason] ?? "Can't reach AudioMuse");

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
  const coverage = $derived(status && status.accessibleLibrarySongs > 0 ? Math.min(100, Math.round(status.analyzedSongs / status.accessibleLibrarySongs * 100)) : null);
  const tone = $derived(!status ? "neutral" : status.reason === null ? "ok" : status.reason === "too_little_analyzed_music" ? "info" : "warn");
  function closeConnection() { connectionOpen = false; key = ""; connectionRequest++; }

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
  {#if error}<ErrorState title="Some sound analysis settings are unavailable" detail={error} onretry={refresh} />{/if}
  {#if !status && refreshing}<RowsSkeleton count={2} />{:else}
    <section class="sound-hero" aria-label="AudioMuse">
      <span class="sound-logo" aria-hidden="true">AM</span>
      <div class="sound-copy">
        <h3>AudioMuse</h3>
        <p class="sound-state" data-tone={tone}><span class="sound-dot" aria-hidden="true"></span>{statusLabel}</p>
        <p>Listens to how your songs sound, so mixes and Instant Mix can follow mood, tempo and energy.</p>
        <div class="sound-actions">
          <Button type="button" variant="outline" disabled={Boolean(action)} aria-expanded={connectionOpen} onclick={() => connectionOpen ? closeConnection() : void editConnection()}>{connectionOpen ? "Close" : "Edit connection"}</Button>
          {#if connection?.baseUrl}<Button variant="text" href={connection.baseUrl} target="_blank" rel="noreferrer">Open AudioMuse ↗</Button>{/if}
          <RefreshButton label="Refresh sound analysis" disabled={Boolean(action)} onrefresh={refresh} />
        </div>
      </div>
      {#if coverage !== null}<div class="sound-ring" style={`--coverage:${coverage}`} role="img" aria-label={`${coverage}% of your library analyzed`}><span><b>{coverage}%</b>analyzed</span></div>{/if}
    </section>
    {#if connectionOpen}
      <form class="settings-card sound-connection" aria-label="AudioMuse connection" transition:slide={{ duration: motion(220) }} onsubmit={event => { event.preventDefault(); void configure(); }}>
        <header><h3>AudioMuse connection</h3></header>
        <div class="connection-fields">
          {#if connectionLoading}<p role="status">Loading connection…</p>{/if}
          <label class="field"><span>AudioMuse address</span><input type="url" bind:value={address} maxlength="2048" disabled={connectionLoading || Boolean(action)} /></label>
          <label class="field"><span>API key</span><input type="password" aria-label="API key" aria-describedby="sound-key-help" bind:value={key} autocomplete="new-password" maxlength="4096" disabled={connectionLoading || Boolean(action)} /><small id="sound-key-help">{connection?.keyConfigured ? "Key saved. Leave blank to keep it." : "Enter your AudioMuse key."}</small></label>
          <div class="connection-buttons"><Button type="button" variant="text" disabled={Boolean(action)} onclick={closeConnection}>Cancel</Button><Button type="submit" disabled={connectionLoading || Boolean(action) || !connection}>{action === "connection" ? "Saving…" : "Save"}</Button></div>
        </div>
        {#if connectionError}<p class="notice-error settings-impact" role="alert">{connectionError}</p>{/if}
      </form>
    {/if}
    <section class="settings-card" aria-label="Mixes from sound"><header><h3>Mixes from sound</h3></header>
      <label class="settings-row"><div><strong>Let listeners add mixes to their apps</strong><small>Each listener picks which mixes show as playlists.</small></div><span class="settings-toggle"><Switch checked={publishEnabled === true} disabled={publishEnabled === null || Boolean(action)} aria-label="Let listeners add mixes to their apps" onchange={event => void setPublishing(event.currentTarget.checked)} /></span></label>
      {#if publishError}<p class="notice-error settings-impact" role="alert">{publishError}</p>{/if}
    </section>
  {/if}
</div>

<style>
  .sound-settings, .connection-fields { display: grid; gap: var(--space-4); min-width: 0; }
  .sound-hero { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: var(--space-5); align-items: center; padding: var(--space-5);
    border: 1px solid var(--color-edge); border-radius: var(--radius-xl, 18px);
    background: linear-gradient(135deg, color-mix(in srgb, var(--color-signal) 13%, var(--color-panel)), var(--color-panel) 62%); }
  .sound-logo { display: grid; width: 56px; height: 56px; place-items: center; border-radius: 14px; background: linear-gradient(135deg, #7c4dff, #00bfa5); color: #fff; font-weight: 700; font-size: 1.05rem; }
  .sound-copy { display: grid; gap: var(--space-1); min-width: 0; }
  .sound-copy h3 { margin: 0; font-size: 1.1rem; font-weight: 600; }
  .sound-copy p { margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); }
  .sound-state { display: flex; align-items: center; gap: var(--space-2); color: var(--color-ink) !important; }
  .sound-dot { width: 8px; height: 8px; flex: none; border-radius: 50%; background: var(--color-ink-muted); }
  [data-tone="ok"] .sound-dot { background: var(--success); animation: sound-pulse 2.4s ease-out infinite; }
  [data-tone="warn"] .sound-dot { background: var(--color-warning); }
  [data-tone="info"] .sound-dot { background: var(--color-signal); }
  .sound-actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); margin-top: var(--space-2); }
  .sound-ring { --coverage: 0; display: grid; width: 96px; height: 96px; place-items: center; border-radius: 50%;
    background: conic-gradient(var(--color-signal) calc(var(--coverage) * 1%), var(--color-panel-raised, var(--color-edge)) 0); animation: sound-ring-in 900ms var(--ease-emphasized); }
  .sound-ring::before { content: ""; grid-area: 1 / 1; width: 78px; height: 78px; border-radius: 50%; background: var(--color-panel); }
  .sound-ring span { z-index: 1; grid-area: 1 / 1; text-align: center; color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.2; }
  .sound-ring b { display: block; color: var(--color-ink); font-size: 1.05rem; }
  .connection-fields { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto; align-items: end; padding: var(--space-4); }
  .connection-buttons { display: flex; gap: var(--space-2); justify-content: end; }
  @keyframes sound-pulse { 0% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--success) 55%, transparent); } 70%, 100% { box-shadow: 0 0 0 7px transparent; } }
  @keyframes sound-ring-in { from { background: conic-gradient(var(--color-signal) 0%, var(--color-panel-raised, var(--color-edge)) 0); } }
  @media (max-width: 700px) {
    .sound-hero { grid-template-columns: auto minmax(0, 1fr); }
    .sound-ring { grid-column: 1 / -1; justify-self: start; }
    .connection-fields { grid-template-columns: minmax(0, 1fr); }
  }
  @media (prefers-reduced-motion: reduce) { .sound-dot, .sound-ring { animation: none !important; } }
</style>
