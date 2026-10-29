<script lang="ts">
  import { onMount } from "svelte";
  import { X } from "@lucide/svelte";
  import { problemAlerts, type AlertSettings, type AlertChannel, type AlertChannelKind, type AlertIncident } from "$lib/api";
  import { alertDate, alertDeliveryLabel, destinationHost, parsePermittedHosts, thresholdDraft, thresholdsFromDraft, type AlertThresholdDraft } from "$lib/alerts";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import SelectField from "$lib/components/SelectField.svelte";

  let { administrator }: { administrator: boolean } = $props();
  let data = $state<AlertSettings | null>(null), incidents = $state<AlertIncident[]>([]);
  let error = $state(""), action = $state(""), changed = $state(false), revision = $state(0);
  let draft = $state<AlertThresholdDraft>({ warningGiB: 5, warningPercent: 5, criticalGiB: 1, criticalPercent: 1, hysteresisGiB: 1, hysteresisPercent: 1, cooldownMinutes: 30 });
  let thresholdSaved = $state(""), hosts = $state(""), hostsSaved = $state("");
  let open = $state(false), editing = $state<AlertChannel | null>(null), household = $state(false);
  let name = $state(""), kind = $state("ntfy"), enabled = $state(true), destination = $state(""), editorError = $state("");
  let hostHints = $state<Record<string, { host: string; revision: number }>>({});
  let loading: Promise<void> | null = null;
  let disposed = false;
  const kinds = [{ value: "ntfy", label: "ntfy" }, { value: "discord", label: "Discord" }, { value: "webhook", label: "Webhook" }];
  const thresholdFields: { key: keyof AlertThresholdDraft; label: string; max: number; step: string }[] = [
    { key: "warningGiB", label: "Warning free space (GiB)", max: 1048576, step: "any" },
    { key: "warningPercent", label: "Warning free space (%)", max: 99, step: "any" },
    { key: "criticalGiB", label: "Critical free space (GiB)", max: 1048576, step: "any" },
    { key: "criticalPercent", label: "Critical free space (%)", max: 99, step: "any" },
    { key: "hysteresisGiB", label: "Recovery margin (GiB)", max: 1048576, step: "any" },
    { key: "hysteresisPercent", label: "Recovery margin (%)", max: 50, step: "any" },
    { key: "cooldownMinutes", label: "Repeat alert interval (minutes)", max: 1440, step: "1" },
  ];
  const thresholdDirty = $derived(Boolean(thresholdSaved && JSON.stringify(draft) !== thresholdSaved));
  const hostsDirty = $derived(hosts !== hostsSaved);
  const thresholds = $derived(thresholdsFromDraft(draft));
  const permittedHosts = $derived(parsePermittedHosts(hosts));
  const openIncidents = $derived(incidents.filter(incident => incident.state === "open"));
  const validChannel = $derived(Boolean(name.trim() && name.trim().length <= 80 && !/[\x00-\x1f\x7f]/.test(name) &&
    (destination.trim() ? destinationHost(destination) : editing?.hasDestination)));
  const message = (cause: unknown, fallback: string) => cause instanceof Error ? cause.message : fallback;
  const kindLabel = (value: string) => kinds.find(item => item.value === value)?.label ?? "Webhook";
  const savedHost = (channel: AlertChannel) => hostHints[channel.id]?.revision === channel.revision ? hostHints[channel.id].host : "";
  const lastDelivery = (id: string) => data?.deliveries.filter(delivery => delivery.channelId === id)
    .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt))[0];

  function apply(next: AlertSettings, reset = "", ownWrite = false) {
    const keepThresholds = thresholdDirty, keepHosts = hostsDirty;
    if (next.thresholds && (!keepThresholds || reset === "all" || reset === "thresholds")) {
      draft = thresholdDraft(next.thresholds); thresholdSaved = JSON.stringify(draft);
    }
    if (!keepHosts || reset === "all" || reset === "hosts") {
      hosts = (next.permittedDestinations ?? []).join("\n"); hostsSaved = hosts;
    }
    if (ownWrite || reset === "all" || (!keepThresholds && !keepHosts)) { revision = next.revision; changed = false; }
    else if (next.revision !== revision) changed = true;
    data = next;
  }
  async function load(reset = false) {
    if (loading) return loading;
    loading = (async () => {
      try {
        const [settings, current] = await Promise.all([problemAlerts.read(), problemAlerts.incidents()]);
        if (disposed) return;
        apply(settings, reset ? "all" : ""); incidents = current; error = "";
      } catch (cause) { if (!disposed) error = message(cause, "Alerts could not be loaded. Refresh to retry."); }
      finally { loading = null; }
    })();
    return loading;
  }
  const scheduler = createRefreshScheduler(async () => { if (!action) await load(); });
  function edit(channel: AlertChannel | null, forHousehold: boolean) {
    editing = channel; household = forHousehold; name = channel?.displayName ?? ""; kind = channel?.kind ?? "ntfy";
    enabled = channel?.enabled ?? true; destination = ""; editorError = ""; open = true;
  }
  async function saveChannel() {
    if (action || !validChannel) return;
    action = "channel"; editorError = "";
    try {
      await loading;
      const host = destination.trim() ? destinationHost(destination) : editing ? savedHost(editing) : "";
      const channel = await problemAlerts.saveChannel(editing?.id ?? null, { displayName: name.trim(), kind: kind as AlertChannelKind,
        enabled, household, expectedRevision: editing?.revision ?? 0, ...(destination.trim() ? { destination: destination.trim() } : {}) });
      if (host) hostHints = { ...hostHints, [channel.id]: { host, revision: channel.revision } };
      destination = ""; open = false;
      toast.success("Alert channel saved."); await load();
    } catch (cause) { editorError = message(cause, "The channel could not be saved. Reload alerts and retry."); }
    finally { action = ""; }
  }
  async function toggle(channel: AlertChannel, value: boolean) {
    if (action || value === channel.enabled) return;
    action = channel.id;
    try {
      await loading;
      const saved = await problemAlerts.saveChannel(channel.id, { displayName: channel.displayName, kind: channel.kind,
        enabled: value, household: channel.household, expectedRevision: channel.revision });
      const host = savedHost(channel);
      if (host) hostHints = { ...hostHints, [saved.id]: { host, revision: saved.revision } };
      if (data) data = { ...data, channels: data.channels.map(item => item.id === saved.id ? saved : item) };
      toast.success(`Alert channel turned ${saved.enabled ? "on" : "off"}.`);
    } catch (cause) {
      if (data) data = { ...data, channels: data.channels.map(item => item.id === channel.id ? { ...channel } : item) };
      toast.error(message(cause, "The channel could not be changed. Refresh alerts and retry."));
    }
    finally { action = ""; }
  }
  async function testChannel(channel: AlertChannel) {
    if (action || !channel.enabled) return;
    action = channel.id;
    try {
      await loading;
      await problemAlerts.test(channel.id);
      toast.info("Test notification queued. Check last delivery for the result."); await load();
    } catch (cause) { toast.error(message(cause, "The test could not be queued. Try again.")); }
    finally { action = ""; }
  }
  async function saveConfiguration(section: "thresholds" | "hosts") {
    if (action || changed || !administrator || (section === "thresholds" ? !thresholds : !permittedHosts)) return;
    action = section; error = "";
    try {
      await loading;
      const next = section === "thresholds" ? await problemAlerts.thresholds(thresholds!, revision) : await problemAlerts.destinations(permittedHosts!, revision);
      apply(next, section, true); toast.success(section === "thresholds" ? "Disk alert thresholds saved." : "Allowed destination hosts saved.");
    } catch (cause) { error = message(cause, "Alert settings could not be saved. Reload alerts and retry."); }
    finally { action = ""; }
  }
  onMount(() => {
    void load(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); destination = ""; };
  });
</script>

<div class="alerts-settings">
  <article class="settings-card" role="region" aria-label="Open problems">
    <header><div><h3>Open problems</h3><small>Problems that need attention now.</small></div>
      <RefreshButton variant="secondary" label="Refresh alerts" disabled={Boolean(action)} onrefresh={() => load()} /></header>
    {#if error}<p class="notice-error alert-notice" role="alert">{error}</p>{/if}
    {#if !data && !error}<p class="alert-empty" role="status">Loading alert settings…</p>
    {:else if data && !openIncidents.length}<p class="alert-empty">No open problems.</p>
    {:else if openIncidents.length}
      <ul class="alert-list">
        {#each openIncidents as incident (incident.id)}
          <li><div><strong>{incident.message}</strong><small>{incident.action}</small>
            <small>{incident.severity === "critical" ? "Critical" : "Warning"} · Started <time datetime={incident.openedAt ?? undefined}>{alertDate(incident.openedAt)}</time></small></div></li>
        {/each}
      </ul>
    {/if}
  </article>

  {#if data}
    {#each administrator ? [false, true] : [false] as forHousehold}
      {@const channels = data.channels.filter(channel => channel.household === forHousehold)}
      <article class="settings-card" role="region" aria-label={forHousehold ? "Household alerts" : "Your alert channels"}>
        <header><div><h3>{forHousehold ? "Household alerts" : "Your alert channels"}</h3>
          <small>{forHousehold ? "Notify you about household problems, including disk space and health checks." : "Notify you when your source connections need attention."}</small></div></header>
        {#if channels.length}
          <ul class="alert-list">
            {#each channels as channel (channel.id)}
              {@const delivery = lastDelivery(channel.id)}
              <li>
                <div><strong>{channel.displayName}</strong><small>{kindLabel(channel.kind)} · {channel.hasDestination ? `Saved${savedHost(channel) ? ` · ${savedHost(channel)}` : ""}` : "No destination saved"}</small>
                  <small>Last delivery: {alertDeliveryLabel(delivery)}{delivery ? ` · ${alertDate(delivery.createdAt)}` : ""}</small>
                  {#if delivery?.safeError}<small>{delivery.safeError}</small>{/if}
                  {#if delivery?.status === "retrying" && delivery.nextAttemptAt}<small>Next attempt: {alertDate(delivery.nextAttemptAt)}</small>{/if}
                </div>
                <div class="alert-actions">
                  <label class="alert-enabled"><span class="settings-toggle"><Checkbox aria-label={`Enable ${channel.displayName}`} disabled={Boolean(action)}
                    bind:checked={() => channel.enabled, (value) => void toggle(channel, value)} /></span><span>{channel.enabled ? "On" : "Off"}</span></label>
                  <Button variant="secondary" disabled={Boolean(action)} aria-label={`Edit ${channel.displayName}`} onclick={() => edit(channel, forHousehold)}>Edit</Button>
                  <Button variant="secondary" disabled={Boolean(action) || !channel.enabled} aria-label={`Send test to ${channel.displayName}`} onclick={() => void testChannel(channel)}>Send test</Button>
                </div>
              </li>
            {/each}
          </ul>
        {:else}<p class="alert-empty">No {forHousehold ? "household " : ""}channels yet. Add one to receive notifications.</p>{/if}
        <footer class="alert-footer"><Button variant="secondary" disabled={Boolean(action)} aria-label={forHousehold ? "Add household channel" : "Add your channel"} onclick={() => edit(null, forHousehold)}>Add channel</Button></footer>
      </article>
    {/each}
    {#if administrator && data.thresholds}
      {#if changed}<div class="degraded-banner" role="status"><p>Alert settings changed elsewhere. Your edits are kept. Reload the saved values before saving.</p><Button variant="secondary" onclick={() => void load(true)}>Reload saved values</Button></div>{/if}
      <article class="settings-card" role="region" aria-label="Disk alert thresholds">
        <header><div><h3>Disk alert thresholds</h3><small>Warn when either free-space limit is reached. Critical limits must be no higher than warning limits.</small></div></header>
        <form aria-label="Disk alert thresholds" onsubmit={event => { event.preventDefault(); void saveConfiguration("thresholds"); }}>
          <fieldset class="settings-fields" disabled={Boolean(action)}>
            {#each thresholdFields.filter(field => field.key === "warningGiB" || field.key === "criticalGiB") as field}
              <label class="setting-field settings-row"><div><strong>{field.label}</strong></div>
                <input type="number" aria-label={field.label} required min="0" max={field.max} step={field.step} bind:value={draft[field.key]} /></label>
            {/each}
            <details class="source-advanced-settings"><summary>Advanced</summary>
              {#each thresholdFields.filter(field => field.key !== "warningGiB" && field.key !== "criticalGiB") as field}
                <label class="setting-field settings-row"><div><strong>{field.label}</strong></div>
                  <input type="number" aria-label={field.label} required min={field.key === "cooldownMinutes" ? 1 : 0} max={field.max} step={field.step} bind:value={draft[field.key]} /></label>
              {/each}
              <p class="settings-impact">Recovery requires this much additional free space above the warning limits. The repeat interval limits repeated notifications for an ongoing problem.</p>
            </details>
            {#if !thresholds}<p class="notice-error alert-notice">Check the limits and recovery margins. Warning limits must be at least the critical limits.</p>{/if}
            <footer><span>{thresholdDirty ? "Unsaved changes" : ""}</span><Button type="submit" disabled={!thresholdDirty || !thresholds || changed || Boolean(action)}>{action === "thresholds" ? "Saving…" : "Save thresholds"}</Button></footer>
          </fieldset>
        </form>
      </article>
      <article class="settings-card" role="region" aria-label="Allowed destination hosts">
        <header><div><h3>Allowed destination hosts</h3><small>Listeners can use only these destination hosts. Administrators can use other hosts.</small></div></header>
        <form aria-label="Allowed destination hosts" onsubmit={event => { event.preventDefault(); void saveConfiguration("hosts"); }}>
          <fieldset class="settings-fields" disabled={Boolean(action)}>
            <label class="setting-field settings-row"><div><strong>Allowed hosts</strong><small>One host per line, with an optional port. Up to 32 hosts. Leave empty to allow no listener destinations.</small></div>
              <textarea aria-label="Allowed hosts" rows="4" bind:value={hosts} placeholder="notify.example&#10;webhook.example:8443" spellcheck="false"></textarea></label>
            {#if !permittedHosts}<p class="notice-error alert-notice">Enter host names with optional ports, without a scheme, path or username.</p>{/if}
            <footer><span>{hostsDirty ? "Unsaved changes" : ""}</span><Button type="submit" disabled={!hostsDirty || !permittedHosts || changed || Boolean(action)}>{action === "hosts" ? "Saving…" : "Save hosts"}</Button></footer>
          </fieldset>
        </form>
      </article>
    {/if}
  {/if}
</div>

<Dialog.Root bind:open onOpenChange={value => { if (!value) { destination = ""; editorError = ""; } }}>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog">
      <header><div><p class="eyebrow">{household ? "Household alerts" : "Your alerts"}</p><Dialog.Title>{editing ? "Edit alert channel" : "Add alert channel"}</Dialog.Title>
        <Dialog.Description>Choose where to receive alerts. Saved destinations stay hidden.</Dialog.Description></div>
        <Dialog.Close class="icon-button" aria-label="Close channel editor"><X size={18} aria-hidden="true" /></Dialog.Close></header>
      <form onsubmit={event => { event.preventDefault(); void saveChannel(); }}>
        <label class="field"><span>Channel type</span><SelectField label="Channel type" options={kinds} bind:value={kind} disabled={Boolean(action)} /></label>
        <label class="field"><span>Channel name</span><input aria-label="Channel name" required maxlength="80" bind:value={name} disabled={Boolean(action)} /></label>
        <label class="field"><span>Destination URL</span><input type="password" inputmode="url" aria-label="Destination URL" autocomplete="new-password" required={!editing?.hasDestination} maxlength="2048" bind:value={destination} disabled={Boolean(action)} />
          <small>{editing?.hasDestination ? `Saved${savedHost(editing) ? ` · ${savedHost(editing)}` : ""}. Leave blank to keep this destination.` : "Enter the ntfy topic URL, Discord webhook URL, or webhook destination URL."}</small></label>
        <label class="alert-enabled"><span class="settings-toggle"><Checkbox aria-label="Channel enabled" bind:checked={enabled} disabled={Boolean(action)} /></span><span>Channel enabled</span></label>
        {#if editorError}<p class="notice-error" role="alert">{editorError}</p>{/if}
        <footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "secondary" })} disabled={Boolean(action)}>Cancel</Dialog.Close>
          <Button type="submit" disabled={Boolean(action) || !validChannel}>{action === "channel" ? "Saving…" : "Save channel"}</Button></footer>
      </form>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  .alerts-settings { display: grid; gap: var(--space-5); min-width: 0; }
  .alert-list { list-style: none; padding: 0; margin: 0; }
  .alert-list li { display: flex; align-items: center; justify-content: space-between; gap: var(--space-4); padding: var(--space-4) var(--space-6); }
  .alert-list li + li { border-top: 1px solid var(--color-edge); }
  .alert-list li > div:first-child { display: grid; gap: var(--space-1); min-width: 0; overflow-wrap: anywhere; }
  .alert-list strong { font-size: var(--text-sm); font-weight: 500; }
  .alert-list small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .alert-actions, .alert-enabled { display: flex; align-items: center; gap: var(--space-3); }
  .alert-actions { flex-wrap: wrap; flex-shrink: 0; }
  .alert-enabled { min-height: 44px; color: var(--color-ink); }
  .alert-enabled :global([data-slot="checkbox"]) { box-sizing: border-box; width: 44px; height: 44px; padding: 0; margin: 0; border: 0; background: transparent; outline: 0; }
  .alert-enabled :global([data-slot="checkbox-indicator"]) { width: 20px; height: 20px; border: 2px solid var(--md-sys-color-on-surface-variant); border-radius: 3px; }
  .alert-enabled :global([data-checked] [data-slot="checkbox-indicator"]) { border-color: var(--md-sys-color-primary); background: var(--md-sys-color-primary); color: var(--md-sys-color-on-primary); }
  .alert-empty, .alert-notice, .alert-footer { margin: 0; padding: var(--space-4) var(--space-6); }
  .alert-footer { border-top: 1px solid var(--color-edge); }
  @media (max-width: 760px) {
    .alerts-settings .settings-card > header { flex-wrap: wrap; }
    .alert-list li { align-items: stretch; flex-direction: column; }
    .alert-actions { justify-content: flex-end; }
    .alert-enabled { margin-right: auto; }
  }
</style>
