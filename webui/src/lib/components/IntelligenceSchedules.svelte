<script lang="ts">
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { intelligence, type IntelligenceSchedule, type IntelligenceScope } from "$lib/api";

  let {
    scope,
    schedules,
    policyEnabled,
    onChanged,
  }: {
    scope: IntelligenceScope;
    schedules: IntelligenceSchedule[];
    policyEnabled: boolean;
    onChanged: () => void | Promise<void>;
  } = $props();

  let editing = $state<IntelligenceSchedule | null>(null);
  let formOpen = $state(false);
  let name = $state("Your recommendations");
  let limit = $state(25);
  let preset = $state("0 8 * * *");
  let customCron = $state("");
  let enabled = $state(true);
  let timeZoneId = $state("UTC");
  let action = $state("");
  let error = $state("");
  let deleteTarget = $state<IntelligenceSchedule | null>(null);
  let deleteOpen = $state(false);

  const knownSchedules = ["0 8 * * *", "0 8 * * 1", "0 8 * * 5"];

  function cadence(cron: string) {
    return cron === "0 8 * * *" ? "Every day at 8:00 AM"
      : cron === "0 8 * * 1" ? "Every Monday at 8:00 AM"
        : cron === "0 8 * * 5" ? "Every Friday at 8:00 AM"
          : `Custom schedule · ${cron}`;
  }

  function begin(item?: IntelligenceSchedule) {
    editing = item ?? null;
    name = item?.name ?? "Your recommendations";
    limit = item?.limit ?? 25;
    enabled = item?.enabled ?? true;
    timeZoneId = item?.timeZoneId ?? Intl.DateTimeFormat().resolvedOptions().timeZone ?? "UTC";
    preset = item && !knownSchedules.includes(item.cronExpression) ? "custom" : item?.cronExpression ?? "0 8 * * *";
    customCron = preset === "custom" ? item?.cronExpression ?? "" : "";
    error = "";
    formOpen = true;
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    const cronExpression = preset === "custom" ? customCron.trim() : preset;
    action = "save";
    error = "";
    try {
      const input = {
        name: name.trim(),
        limit,
        cronExpression,
        timeZoneId,
        overlapPolicy: editing?.overlapPolicy ?? "skip" as const,
        misfirePolicy: editing?.misfirePolicy ?? "runOnce" as const,
        enabled,
      };
      if (editing) await intelligence.updateSchedule(scope, editing, input);
      else await intelligence.createSchedule(scope, input);
      formOpen = false;
      editing = null;
      await onChanged();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The automatic playlist could not be saved.";
    } finally {
      action = "";
    }
  }

  async function remove() {
    if (!deleteTarget) return;
    action = "delete";
    error = "";
    try {
      await intelligence.deleteSchedule(scope, deleteTarget);
      deleteOpen = false;
      deleteTarget = null;
      await onChanged();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The automatic playlist could not be removed.";
    } finally {
      action = "";
    }
  }
</script>

<section class="panel intelligence-schedules">
  <header>
    <div><p class="eyebrow">Automatic discovery</p><h3>Scheduled playlists</h3><p>Create a fresh recommendation playlist on a regular schedule.</p></div>
    <Button variant="secondary" disabled={!policyEnabled || Boolean(action)} onclick={() => begin()}>New schedule</Button>
  </header>

  {#if !policyEnabled}<p class="credential-safety">Save listening automatically before creating an automatic playlist.</p>{/if}
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

  {#if formOpen}
    <form class="schedule-form" onsubmit={save}>
      <label class="field"><span>Playlist name</span><input bind:value={name} maxlength="200" required /></label>
      <label class="field"><span>Tracks</span><input bind:value={limit} type="number" min="1" max="500" required /></label>
      <label class="field"><span>When to create it</span><SelectField bind:value={preset} label="When to create it" options={[
        { value: "0 8 * * *", label: "Every day at 8:00 AM" },
        { value: "0 8 * * 1", label: "Every Monday at 8:00 AM" },
        { value: "0 8 * * 5", label: "Every Friday at 8:00 AM" },
        { value: "custom", label: "Custom schedule" },
      ]} /></label>
      {#if preset === "custom"}<label class="field"><span>Advanced schedule</span><input bind:value={customCron} placeholder="0 8 * * *" required /><small>Five-part cron expression.</small></label>{/if}
      <label class="field"><span>Time zone</span><input bind:value={timeZoneId} maxlength="100" required /></label>
      <label class="toggle-line"><Checkbox bind:checked={enabled} /><span><strong>Run automatically</strong><small>Turn this off to keep the schedule without running it.</small></span></label>
      <footer><Button variant="secondary" onclick={() => formOpen = false}>Cancel</Button><Button type="submit" disabled={Boolean(action)}>{action === "save" ? "Saving…" : editing ? "Save schedule" : "Create schedule"}</Button></footer>
    </form>
  {:else if schedules.length}
    <ul class="schedule-list">
      {#each schedules as item}
        <li><article>
          <div><strong>{item.name}</strong><small>{item.limit} tracks · {cadence(item.cronExpression)} · {item.timeZoneId}</small><small>{item.enabled ? item.nextRunAt ? `Next run ${new Date(item.nextRunAt).toLocaleString()}` : "Waiting for its next run" : "Paused"}</small></div>
          <Badge state={item.enabled ? "healthy" : "suggested"}>{item.enabled ? "On" : "Paused"}</Badge>
          <div class="row-actions"><Button variant="secondary" size="sm" onclick={() => begin(item)}>Edit</Button><Button variant="destructive" size="sm" onclick={() => { deleteTarget = item; deleteOpen = true; }}>Remove</Button></div>
        </article></li>
      {/each}
    </ul>
  {:else}
    <div class="compact-empty"><strong>No automatic playlists</strong><p>Create one when you want fresh recommendations on a schedule.</p></div>
  {/if}
</section>

<ConfirmDialog
  bind:open={deleteOpen}
  title="Remove this automatic playlist schedule?"
  description={deleteTarget ? `${deleteTarget.name} will stop running. Existing playlists will not be removed.` : ""}
  confirmLabel={action === "delete" ? "Removing…" : "Remove schedule"}
  cancelLabel="Keep schedule"
  onConfirm={remove}
/>

<style>
  .intelligence-schedules{display:grid;gap:1rem;padding:1.15rem}.intelligence-schedules>header{display:flex;align-items:start;justify-content:space-between;gap:1rem}.intelligence-schedules h3{margin:.2rem 0}.intelligence-schedules header p:last-child{margin:0;color:var(--color-ink-muted)}.schedule-form{display:grid;grid-template-columns:2fr .7fr 1.4fr 1fr;align-items:end;gap:1rem;border-top:1px solid var(--color-edge);padding-top:1rem}.schedule-form .toggle-line{align-self:center}.schedule-form footer{grid-column:1/-1;display:flex;justify-content:flex-end;gap:.75rem}.field small,.toggle-line small,.schedule-list small{display:block;color:var(--color-ink-muted)}.schedule-list{display:grid;margin:0;padding:0;list-style:none}.schedule-list article{display:grid;grid-template-columns:minmax(0,1fr) auto auto;align-items:center;gap:1rem;border-top:1px solid var(--color-edge);padding:.9rem 0}.row-actions{display:flex;gap:.5rem}
  @media(max-width:900px){.schedule-form{grid-template-columns:1fr 1fr}.schedule-list article{grid-template-columns:minmax(0,1fr) auto}.row-actions{grid-column:1/-1}}
  @media(max-width:620px){.intelligence-schedules>header{flex-direction:column}.intelligence-schedules>header>:global([data-slot="button"]){width:100%}.schedule-form{grid-template-columns:1fr}.schedule-form footer{grid-column:auto}.schedule-form footer>:global([data-slot="button"]){flex:1}.schedule-list :global(.badge){justify-self:start}.row-actions{grid-column:auto}.row-actions>:global([data-slot="button"]){flex:1}}
</style>
