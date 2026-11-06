<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { X, Bell, CircleCheck, MoreHorizontal } from "@lucide/svelte";
  import { Switch } from "$lib/components/ui/switch";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import GroupedRow from "$lib/components/patterns/GroupedRow.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import { problemAlerts, type AlertSettings, type AlertChannel, type AlertChannelKind, type AlertIncident } from "$lib/api";
  import { alertDate, alertDeliveryLabel, destinationHost, parsePermittedHosts, thresholdDraft, thresholdsFromDraft, type AlertThresholdDraft } from "$lib/alerts";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
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
  const incidentGroups = $derived.by(() => {
    const groups = new Map<string, AlertIncident[]>();
    for (const incident of openIncidents) { const key = `${incident.kind}:${incident.message}`; groups.set(key, [...(groups.get(key) ?? []), incident]); }
    return [...groups.values()];
  });
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
    const previous = editing, reversible = Boolean(editing && !destination.trim() && kind === editing.kind);
    action = "channel"; editorError = "";
    try {
      await loading;
      const host = destination.trim() ? destinationHost(destination) : editing ? savedHost(editing) : "";
      const channel = await problemAlerts.saveChannel(editing?.id ?? null, { displayName: name.trim(), kind: kind as AlertChannelKind,
        enabled, household, expectedRevision: editing?.revision ?? 0, ...(destination.trim() ? { destination: destination.trim() } : {}) });
      if (host) hostHints = { ...hostHints, [channel.id]: { host, revision: channel.revision } };
      destination = ""; open = false;
      toast.success("Alert channel saved.", reversible && previous ? { label: "Undo", run: async () => {
        if (action) return;
        const current = data?.channels.find(item => item.id === channel.id);
        if (!current) return;
        action = current.id;
        try { await problemAlerts.saveChannel(current.id, { displayName: previous.displayName, kind: previous.kind, enabled: previous.enabled, household: previous.household, expectedRevision: current.revision }); await load(); }
        catch (cause) { toast.error(message(cause, "The channel change could not be undone. Try again.")); }
        finally { action = ""; }
      } } : undefined); await load();
    } catch (cause) { editorError = message(cause, "The channel could not be saved. Reload alerts and retry."); }
    finally { action = ""; }
  }
  async function toggle(channel: AlertChannel, value: boolean, undo = true) {
    if (action || value === channel.enabled) return;
    action = channel.id;
    try {
      await loading;
      const saved = await problemAlerts.saveChannel(channel.id, { displayName: channel.displayName, kind: channel.kind,
        enabled: value, household: channel.household, expectedRevision: channel.revision });
      const host = savedHost(channel);
      if (host) hostHints = { ...hostHints, [saved.id]: { host, revision: saved.revision } };
      if (data) data = { ...data, channels: data.channels.map(item => item.id === saved.id ? saved : item) };
      toast.success(`Alert channel turned ${saved.enabled ? "on" : "off"}.`, undo ? { label: "Undo", run: async () => { const current = data?.channels.find(item => item.id === saved.id); if (current) await toggle(current, channel.enabled, false); } } : undefined);
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
  async function saveConfiguration(section: "thresholds" | "hosts", undo = true) {
    if (action || changed || !administrator || (section === "thresholds" ? !thresholds : !permittedHosts)) return;
    const previous = section === "thresholds" ? thresholdSaved : hostsSaved;
    action = section; error = "";
    try {
      await loading;
      const next = section === "thresholds" ? await problemAlerts.thresholds(thresholds!, revision) : await problemAlerts.destinations(permittedHosts!, revision);
      apply(next, section, true); toast.success(section === "thresholds" ? "Disk alert thresholds saved." : "Allowed destination hosts saved.", undo ? { label: "Undo", run: async () => {
        if (section === "thresholds") draft = JSON.parse(previous); else hosts = previous;
        await saveConfiguration(section, false);
      } } : undefined);
    } catch (cause) { error = message(cause, "Alert settings could not be saved. Reload alerts and retry."); }
    finally { action = ""; }
  }
  onMount(() => {
    void load(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; unsubscribe(); scheduler.cancel(); destination = ""; };
  });
</script>

<div class="alerts-settings">
  <PageStatus items={[{ label: countLabel(openIncidents.length, "open problem"), tone: openIncidents.length ? "warn" : "good" }, { label: data?.channels.length ? countLabel(data.channels.length, "alert channel") : "No alert channels yet" }]} />
  {#if error}<ErrorState title="Alerts could not be loaded" detail={error} onretry={() => load()} />{/if}
  {#if !data && !error}<RowsSkeleton count={3} />{/if}
  <section class="settings-stack" aria-label="Open problems">
    <div class="settings-toolbar"><h3>Open problems</h3><RefreshButton label="Refresh alerts" disabled={Boolean(action)} onrefresh={() => load()} /></div>
    {#if data && !openIncidents.length}<section class="settings-card"><EmptyState title="No open problems" description="Allstarr will show problems here when something needs attention.">{#snippet icon()}<CircleCheck size={24} />{/snippet}</EmptyState></section>{/if}
    {#each incidentGroups as group (group[0].id)}
      <GroupedRow count={group.length} summary={group[0].kind.includes("source") ? group.length === 1 ? "source needs a look" : "sources need a look" : group.length === 1 ? "open problem" : "open problems"} reason={group[0].message}>
        {#snippet action()}<Button variant="outline" size="sm" href={group[0].kind.includes("source") ? "#/sources" : "#/settings/health"}>{group[0].kind.includes("source") ? "Review sources" : "Review health"}</Button>{/snippet}
        {#snippet members()}{#each group as incident (incident.id)}<ItemRow title={incident.resource || incident.message} subtitle={`${incident.action} · Started ${alertDate(incident.openedAt)}`} />{/each}{/snippet}
      </GroupedRow>
    {/each}
  </section>
  {#if data}
    <div class="channel-grid">
      {#each administrator ? [false, true] : [false] as forHousehold}
        {@const channels = data.channels.filter(channel => channel.household === forHousehold)}
        <article class="settings-card" class:channel-empty={!channels.length} role="region" aria-label={forHousehold ? "Household alerts" : "Your alert channels"}>
          {#if channels.length}
            <header><h3>{forHousehold ? "Household alerts" : "Your alert channels"}</h3><Button variant="text" disabled={Boolean(action)} aria-label={forHousehold ? "Add household channel" : "Add your channel"} onclick={() => edit(null, forHousehold)}>Add channel</Button></header>
            {#each channels as channel (channel.id)}
              {@const delivery = lastDelivery(channel.id)}
              <div class="channel-row"><div><strong>{channel.displayName}</strong><small>{kindLabel(channel.kind)} · {channel.hasDestination ? `Saved${savedHost(channel) ? ` · ${savedHost(channel)}` : ""}` : "No destination saved"}</small><small>Last delivery: {alertDeliveryLabel(delivery)}{delivery ? ` · ${alertDate(delivery.createdAt)}` : ""}</small>{#if delivery?.safeError}<small>{delivery.safeError}</small>{/if}{#if delivery?.status === "retrying" && delivery.nextAttemptAt}<small>Next attempt: {alertDate(delivery.nextAttemptAt)}</small>{/if}</div>
                <div class="channel-actions"><Switch aria-label={`Enable ${channel.displayName}`} checked={channel.enabled} disabled={Boolean(action)} onchange={event => toggle(channel, event.currentTarget.checked)} /><Button variant="outline" size="sm" disabled={Boolean(action)} aria-label={`Edit ${channel.displayName}`} onclick={() => edit(channel, forHousehold)}>Edit</Button>
                  <DropdownMenu.Root><DropdownMenu.Trigger class="icon-button" aria-label={`More actions for ${channel.displayName}`}><MoreHorizontal size={18} /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content class="menu-content"><DropdownMenu.Item class="menu-item" disabled={Boolean(action) || !channel.enabled} onSelect={() => testChannel(channel)}>Send test to {channel.displayName}</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
                </div>
              </div>
            {/each}
          {:else}
            <EmptyState title={forHousehold ? "Household alerts" : "Your alert channels"} description={forHousehold ? "Disk space and health checks. Nothing set up yet." : "When your own source connections need attention. Nothing set up yet."}>
              {#snippet icon()}<Bell size={24} />{/snippet}
              {#snippet action()}<Button variant={forHousehold ? "outline" : "default"} disabled={Boolean(action)} aria-label={forHousehold ? "Add household channel" : "Add your channel"} onclick={() => edit(null, forHousehold)}>Add channel</Button>{/snippet}
            </EmptyState>
          {/if}
        </article>
      {/each}
    </div>
    {#if administrator && data.thresholds}
      {#if changed}<ErrorState title="Alert settings changed elsewhere" detail="Your edits are kept. Reload the saved values before changing them." onretry={() => load(true)} />{/if}
      <article class="settings-card" role="region" aria-label="Disk alert thresholds"><header><h3>Low disk space</h3></header>
        <form aria-label="Disk alert thresholds" onsubmit={event => event.preventDefault()} onchange={event => { if (event.currentTarget.checkValidity() && thresholdDirty) void saveConfiguration("thresholds"); }}>
          <fieldset class="settings-fields" disabled={Boolean(action)}>
            {#each thresholdFields.filter(field => field.key === "warningGiB" || field.key === "criticalGiB") as field}<label class="setting-field settings-row"><div><strong>{field.label}</strong><small>{field.key === "warningGiB" ? "Warn below this much free space." : "Must be no higher than the warning."}</small></div><input type="number" aria-label={field.label} required min="0" max={field.max} step={field.step} bind:value={draft[field.key]} /></label>{/each}
            <AdvancedFold description="percentages, recovery margin and repeat alerts">
              {#each thresholdFields.filter(field => field.key !== "warningGiB" && field.key !== "criticalGiB") as field}<label class="setting-field settings-row"><div><strong>{field.label}</strong></div><input type="number" aria-label={field.label} required min={field.key === "cooldownMinutes" ? 1 : 0} max={field.max} step={field.step} bind:value={draft[field.key]} /></label>{/each}
            </AdvancedFold>
            {#if !thresholds}<p class="notice-error alert-notice" role="alert">Check the limits and recovery margins. Warning limits must be at least the critical limits.</p>{/if}
          </fieldset>
        </form>
      <AdvancedFold description="hosts listeners may send alerts to">
        <form class="settings-card" aria-label="Allowed destination hosts" onsubmit={event => event.preventDefault()} onchange={() => { if (hostsDirty) void saveConfiguration("hosts"); }}><label class="settings-row"><div><strong>Allowed hosts</strong><small>One host per line, with an optional port. Administrators can use other hosts. Leave empty to allow no listener destinations.</small></div><textarea aria-label="Allowed hosts" rows="4" bind:value={hosts} spellcheck="false" disabled={Boolean(action)}></textarea></label>{#if !permittedHosts}<p class="notice-error alert-notice" role="alert">Enter host names with optional ports, without a scheme, path or username.</p>{/if}</form>
      </AdvancedFold>
      </article>
    {/if}
  {/if}
</div>

<Dialog.Root bind:open onOpenChange={value => { if (!value) { destination = ""; editorError = ""; } }}>
  <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog settings-flow-dialog">
    <header><div><Dialog.Title>{editing ? "Edit alert channel" : "Add an alert channel"}</Dialog.Title><Dialog.Description>Get a message when something needs attention.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close channel editor"><X size={18} /></Dialog.Close></header>
    <form class="settings-dialog-content" onsubmit={event => { event.preventDefault(); void saveChannel(); }}>
      <div class="field"><span>Channel type</span><ThreeDetentSwitch label="Channel type" options={kinds} value={kind} onchange={value => kind = value} disabled={Boolean(action)} /></div>
      <label class="field"><span>Channel name</span><input aria-label="Channel name" required maxlength="80" bind:value={name} disabled={Boolean(action)} /></label>
      <label class="field"><span>{kind === "ntfy" ? "Topic URL" : kind === "discord" ? "Discord webhook URL" : "Destination URL"}</span><input type="password" inputmode="url" aria-label="Destination URL" autocomplete="new-password" required={!editing?.hasDestination} maxlength="2048" bind:value={destination} disabled={Boolean(action)} /><small>{editing?.hasDestination ? "Saved. Leave blank to keep this destination." : "Kept private. The destination is never shown after saving."}</small></label>
      {#if administrator}<label class="field"><span>Alerts for</span><SelectField label="Alerts for" value={household ? "household" : "personal"} options={[{ value: "personal", label: "Your source connections" }, { value: "household", label: "Household: disk space and health" }]} onchange={value => household = value === "household"} disabled={Boolean(action)} /></label>{/if}
      <label class="channel-actions"><Switch aria-label="Channel enabled" bind:checked={enabled} disabled={Boolean(action)} /><span>Channel enabled</span></label>
      <div class="channel-preview"><strong>What you'll receive</strong><p>{household ? "Warnings about low disk space and health checks, followed by recovery messages." : "Warnings when your source connections need attention, followed by recovery messages."}</p><small>Save the channel, then use Send test from its menu to check delivery.</small></div>
      {#if !administrator && data?.permittedDestinations?.length}<p class="settings-muted">Allowed hosts: {data.permittedDestinations.join(", ")}</p>{/if}
      {#if editorError}<p class="notice-error" role="alert">{editorError}</p>{/if}
      <footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "text" })} disabled={Boolean(action)}>Cancel</Dialog.Close><Button type="submit" disabled={Boolean(action) || !validChannel}>{action === "channel" ? "Saving…" : "Save channel"}</Button></footer>
    </form>
  </Dialog.Content></Dialog.Portal>
</Dialog.Root>
<style>
  .channel-empty { border-style: dashed; }
  .channel-empty :global(.empty-state) { align-items: flex-start; min-height: 0; padding: var(--space-4); text-align: left; }
  .channel-empty :global(.empty-icon) { display: none; }
  .alerts-settings { display: grid; gap: var(--space-4); min-width: 0; }
  .settings-toolbar h3 { margin: 0; font-size: var(--text-sm); }
  .channel-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-4); }
  .channel-row { display: grid; gap: var(--space-3); padding: var(--space-3) var(--space-4); border-top: 1px solid var(--color-edge); }
  .channel-row > div:first-child { display: grid; min-width: 0; gap: var(--space-1); overflow-wrap: anywhere; }
  .channel-row strong { font-size: var(--text-sm); }
  .channel-row small, .channel-preview small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .channel-actions { display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-2); }
  .channel-actions :global(.ui-switch) { margin-right: auto; }
  .channel-preview { border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); background: var(--color-panel-raised); font-size: var(--text-sm); }
  .channel-preview p { margin: var(--space-2) 0; }
  .alert-notice { padding: var(--space-3) var(--space-4); }
  @media (max-width: 760px) { .channel-grid { grid-template-columns: minmax(0, 1fr); } }
</style>
