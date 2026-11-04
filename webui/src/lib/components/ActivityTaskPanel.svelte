<script lang="ts">
  import { onMount } from "svelte";
  import { CalendarClock } from "@lucide/svelte";
  import { playlistLinks, type ActivityTask, type PlaylistDetails, type PlaylistLink, type PlaylistSchedule, type ProviderDefinition } from "$lib/api";
  import { providerDisplayName } from "$lib/sources";
  import { internalActivityLink } from "$lib/activity";
  import { toast } from "$lib/toasts";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import ActivityOutcome from "./ActivityOutcome.svelte";
  import { scheduleParts } from "./activity-rows";

  let { task, name, providers = [], onclose, onchanged, onrun, running }: {
    task: ActivityTask; name: string; providers?: ProviderDefinition[]; onclose: () => void; onchanged: () => Promise<void>;
    onrun: () => Promise<void>; running: boolean;
  } = $props();
  let open = $state(true);
  let details = $state<PlaylistDetails | null>(null);
  let link = $state<PlaylistLink | null>(null);
  let schedule = $state<PlaylistSchedule | null>(null);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");
  let repeat = $state("daily");
  let at = $state("04:00");
  let day = $state("1");
  let custom = $state("");
  let zone = $state("");
  let stopOpen = $state(false);
  let disposed = false;
  let saved = $state(false);

  function time(value?: string | null) {
    return value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) : "Not recorded";
  }
  function setSchedule(value: PlaylistSchedule) {
    schedule = value;
    const parts = scheduleParts(value.cronExpression, value.enabled);
    repeat = parts.repeat; at = parts.time; day = parts.day;
    custom = value.cronExpression; zone = value.timeZoneId;
  }
  async function load() {
    if (!task.runPlaylistLinkId) return;
    loading = true; error = "";
    try {
      const [incoming, all] = await Promise.all([playlistLinks.details(task.runPlaylistLinkId), playlistLinks.list()]);
      if (disposed) return;
      details = incoming;
      link = all.playlistLinks.find(item => item.id === task.runPlaylistLinkId) ?? null;
      if (incoming.schedule) setSchedule(incoming.schedule);
    } catch { if (!disposed) error = "Task settings could not be loaded. Close and reopen this task to try again."; }
    finally { if (!disposed) loading = false; }
  }
  async function persistSchedule(previous: PlaylistSchedule, input: { cronExpression: string; timeZoneId: string; enabled: boolean }, undo = true) {
    if (saving) return;
    saving = true; saved = false; error = "";
    try {
      const updated = await playlistLinks.updateSchedule(previous, { cronExpression: input.cronExpression, timeZoneId: input.timeZoneId, enabled: input.enabled });
      if (!disposed) { setSchedule(updated); saved = true; }
      toast.success("Schedule saved.", undo ? { label: "Undo", run: () => persistSchedule(updated, previous, false) } : undefined);
      await onchanged();
    } catch {
      if (!disposed) { setSchedule(previous); error = "The schedule could not be saved. It may have changed elsewhere. Reload this task and try again."; }
      toast.error("Schedule could not be saved.");
    } finally { saving = false; }
  }
  async function saveSchedule() {
    if (!schedule || saving) return;
    if (!/^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(at) || !zone.trim()) return;
    const [hour, minute] = at.split(":").map(Number);
    const cronExpression = repeat === "daily" ? minute + " " + hour + " * * *" : repeat === "weekly" ? minute + " " + hour + " * * " + day : custom;
    const input = { cronExpression, timeZoneId: zone.trim(), enabled: repeat !== "manual" };
    if (schedule.cronExpression === input.cronExpression && schedule.timeZoneId === input.timeZoneId && schedule.enabled === input.enabled) return;
    await persistSchedule(schedule, input);
  }
  async function changeRule(key: "mirrorStaleEntries" | "preserveManualEntries", value: boolean, undo = true, current: PlaylistLink | null = link) {
    if (!current || saving) return;
    const previous = current;
    saving = true; saved = false; error = "";
    try {
      const updated = await playlistLinks.update(previous.id, {
        expectedRevision: previous.revision, mode: previous.mode, projectionMode: previous.projectionMode,
        materializationMode: previous.materializationMode, importMode: previous.importMode,
        trackRetention: previous.trackRetention, scheduleId: previous.scheduleId, targetPlaylistId: previous.targetPlaylistId,
        targetCredentialReferenceId: previous.targetCredentialReferenceId, mirrorStaleEntries: previous.mirrorStaleEntries,
        preserveManualEntries: previous.preserveManualEntries, syncName: previous.syncName, syncDescription: previous.syncDescription,
        syncArtwork: previous.syncArtwork, ruleVersion: previous.ruleVersion, policyVersion: previous.policyVersion, [key]: value,
      });
      if (!disposed) { link = updated; saved = true; }
      toast.success("Update rule saved.", undo ? { label: "Undo", run: () => changeRule(key, previous[key], false, disposed ? updated : link) } : undefined);
    } catch { if (!disposed) error = "The update rule could not be saved. Reload this task and try again."; toast.error("Update rule could not be saved."); }
    finally { saving = false; }
  }
  onMount(() => { void load(); return () => { disposed = true; }; });
</script>

<SidePanel bind:open title={name} description={details ? "Playlist sync · " + providerDisplayName(providers, details.sourceProviderId) + " → " + (details.targetProtocol || "Your music app") : task.runPlaylistLinkId ? "Playlist sync" : "Automatic upkeep"} {onclose}>
  <PageStatus label="Task status" items={[{ label: task.enabled ? "Next: " + (task.nextRunAt ? time(task.nextRunAt) : "When needed") : "Paused" }, { label: "Last run: " + time(task.lastRunAt) }]} />
  {#if error}<ErrorState title={error} onretry={load} />{/if}
  {#if loading}<RowsSkeleton count={3} label="Loading task settings" />
  {:else if task.runPlaylistLinkId}
    {#if schedule}
      <section class="settings-group" aria-label="Schedule">
        <h3>Schedule</h3><p>Changes save as you make them.</p>
        <fieldset disabled={saving}>
          <label>Repeat<select value={repeat} onchange={(event) => { repeat = event.currentTarget.value; void saveSchedule(); }}><option value="daily">Daily</option><option value="weekly">Weekly</option><option value="manual">Only when I ask</option><option value="custom">Custom</option></select></label>
          {#if repeat === "weekly"}<label>Day<select bind:value={day} onchange={() => void saveSchedule()}>{#each ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"] as label, index}<option value={String(index)}>{label}</option>{/each}</select></label>{/if}
          {#if repeat === "custom"}<label>Schedule expression<input bind:value={custom} onblur={() => void saveSchedule()} /></label>{:else if repeat !== "manual"}<label>Time<input type="time" bind:value={at} onchange={() => void saveSchedule()} /></label>{/if}
          <label>Time zone<input bind:value={zone} onblur={() => void saveSchedule()} /></label>
        </fieldset>
      </section>
    {:else}<p>No editable schedule is attached. Open the playlist to add one.</p>{/if}
    {#if link}
      <section class="settings-group" aria-label="Update rules"><h3>Update rules</h3>
        <p>{link.materializationMode === "reconcile" ? "Follows the source’s order when updating your music app." : "Recreates the playlist when updating your music app."}</p>
        <div class="rule"><div><strong>Drop songs the source drops</strong><small>Remove songs that are no longer in the source playlist.</small></div><Switch checked={link.mirrorStaleEntries} disabled={saving} aria-label="Drop songs the source drops" onchange={(event) => void changeRule("mirrorStaleEntries", event.currentTarget.checked)} /></div>
        <div class="rule"><div><strong>Keep songs added in your app</strong><small>Preserve songs you added yourself.</small></div><Switch checked={link.preserveManualEntries} disabled={saving} aria-label="Keep songs added in your app" onchange={(event) => void changeRule("preserveManualEntries", event.currentTarget.checked)} /></div>
      </section>
    {/if}
    {#if saved}<p class="saved" role="status">Saved</p>{/if}
    <section class="settings-group" aria-label="Recent runs"><h3>Recent runs</h3>
      {#if task.lastRunAt}<p>{time(task.lastRunAt)}</p><ActivityOutcome state={task.state} label={task.state === "Scheduled" ? "Run recorded" : task.state} />{:else}<p>No runs recorded yet.</p>{/if}
      <Button variant="ghost" href="#/activity?view=history">Open in History</Button>
    </section>
  {:else}<section class="settings-group"><h3>About this task</h3><p>{task.cadence}</p><p>{task.detail || "Runs automatically when needed."}</p><ActivityOutcome state={task.state} label={task.state} /></section>{/if}
  {#snippet footer()}
    <div class="task-footer">
      {#if schedule?.enabled}<Button variant="ghost" class="stop-sync" disabled={saving} onclick={() => stopOpen = true}>Pause schedule…</Button>{/if}
      {#if task.runPlaylistLinkId}<Button disabled={running || saving || loading} onclick={() => void onrun()}>{running ? "Queued" : "Run now"}</Button>{:else if internalActivityLink(task.actionHref)}<Button href={task.actionHref!}>{task.actionLabel || "Open settings"}</Button>{/if}
    </div>
  {/snippet}
</SidePanel>
<ConfirmDialog bind:open={stopOpen} title="Pause this schedule?" description="This playlist will only update when you run it yourself. You can turn the schedule back on here." confirmLabel="Pause schedule" onConfirm={async () => { if (schedule) await persistSchedule(schedule, { ...schedule, enabled: false }); }} />

<style>
  .settings-group { margin-top: var(--space-5); border: 1px solid var(--color-edge); border-radius: var(--radius-lg); padding: var(--space-4); }
  h3 { margin: 0 0 var(--space-2); color: var(--color-ink); font-size: var(--text-sm); }
  p, small { color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; overflow-wrap: anywhere; }
  fieldset { display: grid; gap: var(--space-3); min-width: 0; border: 0; padding: 0; margin: var(--space-4) 0 0; }
  label { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); color: var(--color-ink); font-size: var(--text-sm); }
  input, select { min-width: 0; max-width: 65%; min-height: 44px; padding: var(--space-2); border: 1px solid var(--color-edge); border-radius: var(--radius-sm); background: var(--color-panel); color: var(--color-ink); font: inherit; }
  .rule { display: flex; justify-content: space-between; align-items: center; gap: var(--space-4); padding-block: var(--space-3); border-top: 1px solid var(--color-edge); }
  .rule div { min-width: 0; } strong { font-size: var(--text-sm); font-weight: 500; } small { display: block; margin-top: var(--space-1); }
  .saved { color: var(--success); } .task-footer { display: flex; width: 100%; justify-content: space-between; gap: var(--space-3); } :global(.stop-sync) { color: var(--danger); }
</style>
