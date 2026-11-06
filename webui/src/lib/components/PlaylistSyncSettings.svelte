<script lang="ts">
  import { playlistLinks, type PlaylistLink, type PlaylistSchedule } from "$lib/api";
  import { LockKeyhole } from "@lucide/svelte";
  import { toast } from "$lib/toasts";
  import { Switch } from "$lib/components/ui/switch";
  import SelectField from "$lib/components/SelectField.svelte";
  import { scheduleCadence } from "$lib/playlists";

  let { playlist, schedule, sourceName, targetName, onPlaylist, onSchedule }: {
    playlist: PlaylistLink; schedule: PlaylistSchedule | null; sourceName: string; targetName: string;
    onPlaylist: (value: PlaylistLink) => void; onSchedule: (value: PlaylistSchedule, playlistId: string) => void;
  } = $props();
  let saving = $state(false);
  let error = $state("");
  let customOpen = $state(false);
  const times = ["0 4 * * *", "0 3 * * *", "0 * * * *", "0 3 * * 1"];

  async function changeSchedule(changes: { enabled?: boolean; cronExpression?: string; timeZoneId?: string }, undo = false) {
    if (saving) return;
    const previous = schedule;
    const next = { cronExpression: previous?.cronExpression ?? "0 4 * * *", timeZoneId: previous?.timeZoneId ?? Intl.DateTimeFormat().resolvedOptions().timeZone,
      enabled: previous?.enabled ?? true, ...changes };
    saving = true; error = "";
    try {
      const saved = previous ? await playlistLinks.updateSchedule(previous, next)
        : await playlistLinks.createSchedule(playlist.id, { ...next, overlapPolicy: "skip", misfirePolicy: "runOnce" });
      onSchedule(saved, playlist.id);
      if (!previous) {
        try {
          const links = await playlistLinks.list();
          const current = links.playlistLinks.find(item => item.id === playlist.id);
          if (current) onPlaylist(current);
        } catch { error = "Sync settings saved. Refresh before changing other playlist settings."; }
      }
      toast.success(undo ? "Sync settings restored." : "Sync settings saved.", undo ? undefined : { label: "Undo", run: () => changeSchedule(previous
        ? { enabled: previous.enabled, cronExpression: previous.cronExpression, timeZoneId: previous.timeZoneId }
        : { enabled: false }, true) });
    } catch (cause) { error = cause instanceof Error ? cause.message : "Sync settings could not be saved. Try again."; }
    finally { saving = false; }
  }

  async function changePlaylist(changes: Partial<Pick<PlaylistLink, "mirrorStaleEntries" | "trackRetention">>, undo = false) {
    if (saving) return;
    const previous = playlist;
    saving = true; error = "";
    try {
      const saved = await playlistLinks.update(previous.id, {
        expectedRevision: previous.revision, mode: previous.mode, projectionMode: previous.projectionMode,
        materializationMode: previous.materializationMode, importMode: previous.importMode, trackRetention: previous.trackRetention,
        scheduleId: schedule?.id ?? previous.scheduleId, targetPlaylistId: previous.targetPlaylistId, targetCredentialReferenceId: previous.targetCredentialReferenceId,
        mirrorStaleEntries: previous.mirrorStaleEntries, preserveManualEntries: previous.preserveManualEntries,
        syncName: previous.syncName, syncDescription: previous.syncDescription, syncArtwork: previous.syncArtwork,
        ruleVersion: previous.ruleVersion, policyVersion: previous.policyVersion, ...changes,
      });
      onPlaylist({ ...previous, ...saved });
      toast.success(undo ? "Playlist settings restored." : "Playlist settings saved.", undo ? undefined : { label: "Undo", run: () => changePlaylist(
        Object.fromEntries(Object.keys(changes).map(key => [key, previous[key as keyof typeof changes]])), true) });
    } catch (cause) { error = cause instanceof Error ? cause.message : "Playlist settings could not be saved. Try again."; }
    finally { saving = false; }
  }
</script>

<section class="sync-settings" aria-label="Sync settings" aria-busy={saving}>
  <h3 class="sec-label">Sync</h3>
  {#if playlist.importMode === "linked"}
    <div class="setting"><div><strong>Keep in sync</strong><small>Follows changes in {sourceName}</small></div><Switch aria-label="Keep in sync" bind:checked={() => schedule?.enabled ?? false, enabled => void changeSchedule({ enabled })} disabled={saving} /></div>
    <div class="setting"><div><strong>When</strong><small>{schedule ? scheduleCadence(schedule.cronExpression) : "Choose when to check"}</small></div>
      <SelectField bind:value={() => schedule?.cronExpression ?? "0 4 * * *", cronExpression => void changeSchedule({ cronExpression })} label="Sync time" disabled={saving} options={[
        { value: "0 4 * * *", label: "Daily at 4:00 AM" }, { value: "0 3 * * *", label: "Daily at 3:00 AM" },
        { value: "0 * * * *", label: "Every hour" }, { value: "0 3 * * 1", label: "Mondays at 3:00 AM" },
        ...(schedule && !times.includes(schedule.cronExpression) ? [{ value: schedule.cronExpression, label: scheduleCadence(schedule.cronExpression) }] : []),
      ]} />
    </div>
    <div class="setting"><div><strong>Songs removed in {sourceName}</strong><small>Also leave the {targetName} copy</small></div><span class="locked" title="This playlist requires removed source songs to leave its copy."><LockKeyhole size={14} aria-hidden="true" />{playlist.mirrorStaleEntries === false ? "Off" : "On"}</span></div>
    <details bind:open={customOpen}><summary>Advanced</summary><form onsubmit={event => {
      event.preventDefault(); const fields = new FormData(event.currentTarget);
      void changeSchedule({ cronExpression: String(fields.get("cron")), timeZoneId: String(fields.get("zone")) });
    }}><label class="field"><span>Cron schedule</span><input name="cron" bind:value={() => schedule?.cronExpression ?? "0 4 * * *", cronExpression => void changeSchedule({ cronExpression })} required onchange={event => void changeSchedule({ cronExpression: event.currentTarget.value })} /></label><label class="field"><span>Time zone</span><input name="zone" value={schedule?.timeZoneId ?? Intl.DateTimeFormat().resolvedOptions().timeZone} required onchange={event => void changeSchedule({ timeZoneId: event.currentTarget.value })} /></label></form></details>
  {:else}<p>Imported once. Later changes in {sourceName} are ignored.</p>{/if}
  <div class="setting"><div><strong>Keep downloaded copies</strong><small>Save permanent copies of songs available to download</small></div><Switch aria-label="Keep downloaded copies" bind:checked={() => playlist.trackRetention === "keepAll", checked => void changePlaylist({ trackRetention: checked ? "keepAll" : "onDemand" })} disabled={saving} /></div>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
</section>

<style>
  .locked { display: inline-flex; align-items: center; gap: var(--space-2); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .sync-settings { display: grid; min-width: 0; }
  .setting { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); min-height: 64px; padding-block: var(--space-3); border-bottom: 1px solid var(--color-edge); }
  .setting > div { min-width: 0; }.setting strong { font-size: var(--text-sm); }.setting small { display: block; margin-top: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .setting :global(.select-root) { max-width: 55%; }.setting :global(.select-trigger) { font-size: var(--text-xs); padding-inline: var(--space-2); }
  summary { display: flex; justify-content: space-between; min-height: 44px; align-items: center; cursor: pointer; color: var(--color-signal-text); font-size: var(--text-xs); }
  summary::after { content: "▾"; }
  form { display: grid; gap: var(--space-2); }p { font-size: var(--text-sm); color: var(--color-ink-muted); }
</style>
