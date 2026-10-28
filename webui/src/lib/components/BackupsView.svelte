<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, settings, type BackupPolicy, type BackupPolicyInput, type StorageOverview } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import FilePicker from "$lib/components/FilePicker.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Skeleton } from "$lib/components/ui/skeleton";

  let { administrator }: { administrator: boolean } = $props();
  let data = $state<StorageOverview | null>(null);
  let saved = $state<BackupPolicy | null>(null);
  let draft = $state<BackupPolicyInput | null>(null);
  let refreshing = $state(false);
  let action = $state("");
  let error = $state("");
  let changed = $state(false);
  let restoreOpen = $state(false);
  let restoreFile = $state<File | null>(null);
  let restoreError = $state("");
  let disposed = false;
  let polls = $state(0);
  let timer: ReturnType<typeof setTimeout> | undefined;
  const ready = $derived(data?.storage.readiness?.toLowerCase() === "ready");
  const pending = $derived(data?.restorePending === true);
  const status = $derived(data?.backupStatus);
  const active = $derived(Boolean(status?.activeJobId) || status?.state === "queued" || status?.state === "running");
  const directoryUnavailable = $derived(status?.directoryState === "unavailable" || status?.directoryState === "changed");
  const failed = $derived(Boolean(status?.lastFailure && (!Number.isFinite(Date.parse(status.lastSuccessAt ?? "")) ||
    !Number.isFinite(Date.parse(status.lastFailure.at)) || Date.parse(status.lastFailure.at) > Date.parse(status.lastSuccessAt!))));
  const dirty = $derived(Boolean(draft && saved && Object.entries(input(saved)).some(([key, value]) => draft?.[key as keyof BackupPolicyInput] !== value)));
  const stateLabel = $derived(pending ? "Restore pending" : !ready ? "Unavailable" : status?.state === "queued" ? "Queued"
    : status?.state === "running" ? "Backing up" : status?.state === "unavailable" || directoryUnavailable ? "Unavailable"
    : failed ? "Last backup failed" : "Ready");

  function input(policy: BackupPolicy): BackupPolicyInput {
    return { enabled: policy.enabled, timeUtc: policy.timeUtc, directory: policy.directory,
      dailyRetentionCount: policy.dailyRetentionCount, weeklyRetentionCount: policy.weeklyRetentionCount,
      manualRetentionCount: policy.manualRetentionCount };
  }
  function date(value?: string | null) {
    if (!value) return "Not available";
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? "Not available" : new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(parsed);
  }
  function size(value?: number | null) {
    return typeof value === "number" && Number.isFinite(value) && value > 0
      ? `${(value / 1024 / 1024).toLocaleString(undefined, { maximumFractionDigits: 1 })} MiB` : "Size unavailable";
  }
  function message(cause: unknown, fallback: string) {
    if (cause instanceof ApiError) {
      const details = cause.details as { message?: string; error?: string } | null;
      return details?.message ?? (details?.error && /\s/.test(details.error) ? details.error : fallback);
    }
    return cause instanceof Error ? cause.message : fallback;
  }
  function poll() {
    clearTimeout(timer);
    if (active && polls < 60 && !disposed) timer = setTimeout(() => { polls++; void refresh(); }, 2000);
    else if (!active) polls = 0;
  }
  async function refresh(reload = false) {
    if (refreshing || !administrator || disposed) return;
    refreshing = true;
    try {
      const incoming = await settings.storage();
      if (disposed) return;
      const preserve = dirty && !reload;
      const completed = active && !incoming.backupStatus?.activeJobId && incoming.backupStatus?.lastSuccessAt &&
        incoming.backupStatus.lastSuccessAt !== status?.lastSuccessAt;
      data = incoming;
      if (incoming.backupPolicy) {
        if (!preserve) { saved = incoming.backupPolicy; draft = input(saved); changed = false; }
        else if (saved?.revision !== incoming.backupPolicy.revision) changed = true;
      }
      error = "";
      if (completed) toast.success("Backup completed.");
    } catch (cause) { error = message(cause, "Backup status could not be loaded."); }
    finally { refreshing = false; poll(); }
  }
  const scheduler = createRefreshScheduler(refresh);

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!draft || !saved || action || pending || !ready) return;
    action = "save";
    try {
      const result = await settings.saveBackupPolicy({ ...draft, expectedRevision: saved.revision });
      saved = result.backupPolicy;
      draft = input(saved);
      if (data) data = { ...data, ...result };
      changed = false;
      toast.success("Backup schedule saved.");
      await refresh();
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 409 && (cause.details as { error?: string })?.error === "backup_configuration_changed") changed = true;
      toast.error(message(cause, "Backup settings could not be saved."));
    } finally { action = ""; }
  }
  async function start() {
    if (action || active || pending || !ready || directoryUnavailable) return;
    action = "backup";
    try {
      const result = await settings.backup();
      if (data && result.jobId) data = { ...data, backupStatus: { state: "queued", directoryState: status?.directoryState ?? "unchecked",
        activeJobId: result.jobId, lastSuccessAt: status?.lastSuccessAt ?? null, nextRunAt: status?.nextRunAt ?? null,
        lastBytes: status?.lastBytes ?? null, lastFailure: status?.lastFailure ?? null } };
      polls = 0;
      toast.info(result.message ?? "Backup queued.");
      await refresh();
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 409 && (cause.details as { error?: string })?.error === "backup_running")
        toast.info("A backup is already queued or running.");
      else toast.error(message(cause, "The backup could not be queued."));
      await refresh();
    } finally { action = ""; poll(); }
  }
  async function restore() {
    if (!restoreFile || action || pending) return;
    action = "restore";
    restoreError = "";
    try {
      const result = await settings.restore(restoreFile);
      toast.success(result.message);
      restoreOpen = false;
      restoreFile = null;
      await refresh();
    } catch (cause) { restoreError = message(cause, "Backup restore could not be staged."); }
    finally { action = ""; }
  }
  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; clearTimeout(timer); scheduler.cancel(); unsubscribe(); };
  });
</script>

{#if administrator}
  {#if error}
    <div class="degraded-banner" role="status"><p><strong>Backup status is unavailable.</strong> {error}</p>
      <Button variant="secondary" disabled={refreshing} onclick={() => { polls = 0; void refresh(); }}>Retry</Button></div>
  {/if}
  {#if !data && !error}
    <Skeleton class="settings-card skeleton-panel" aria-label="Loading backups" aria-busy="true" />
  {:else if data}
    <article class="settings-card maintenance-card" aria-label="Database backup status">
      <header><div><h3>Database</h3><small>Durable application state, accounts and encryption keys</small></div>
        <Badge state={ready ? "healthy" : "degraded"}>{data.storage.readiness ?? "Unknown"}</Badge></header>
      {#if pending}<p class="notice-info" role="status">A restore is staged. Restart Allstarr to finish replacing saved state and credentials.</p>{/if}
      <div class="settings-row"><div><strong>Backup status</strong><small>Backups include saved sign-ins. Keep downloaded files private.</small></div>
        <output aria-label="Backup status">{stateLabel}</output></div>
      {#if status}
        <div class="settings-row"><div><strong>Last verified backup</strong><small>{size(status.lastBytes)}</small></div><output>{date(status.lastSuccessAt)}</output></div>
        <div class="settings-row"><div><strong>Next scheduled backup</strong><small>Result times use your browser's time zone.</small></div><output>{data.backupPolicy?.enabled ? date(status.nextRunAt) : "Scheduling off"}</output></div>
        {#if failed && status.lastFailure}<p class="notice-info" role="status">{status.lastFailure.message} · {date(status.lastFailure.at)}</p>{/if}
      {/if}
      <div class="settings-row"><div><strong>Create a backup</strong><small>Creation runs in the background and verifies the database and keys.</small></div>
        <Button disabled={Boolean(action) || !ready || pending || active || directoryUnavailable} onclick={() => void start()}>{action === "backup" ? "Queuing…" : "Back up now"}</Button></div>
      {#if active && polls >= 60}<p class="notice-info" role="status">The backup is still running. Refresh its status to check again.</p>{/if}
      {#if status}<div class="settings-row"><div><strong>Check progress</strong><small>Active work updates automatically.</small></div>
        <RefreshButton variant="secondary" onrefresh={() => { polls = 0; return refresh(); }} label="Refresh status" /></div>{/if}
    </article>

    {#if draft && saved}
      {#if changed}<div class="degraded-banner" role="status"><p><strong>Backup settings changed.</strong> Your unsaved edits are kept.</p>
        <Button variant="secondary" disabled={Boolean(action) || refreshing} onclick={() => void refresh(true)}>Reload server values</Button></div>{/if}
      <form class="settings-card settings-disclosure" aria-label="Backup schedule" onsubmit={(event) => void save(event)}>
        <header><h3>Backup schedule</h3></header>
        <fieldset class="settings-fields" disabled={Boolean(action) || pending || !ready}>
          <label class="setting-field settings-row"><div><strong>Daily backups</strong><small>Keep an automatic snapshot each day.</small></div>
            <span class="settings-toggle"><Checkbox aria-label="Daily backups" bind:checked={draft.enabled} /></span></label>
          <label class="setting-field settings-row"><div><strong>Backup time (UTC)</strong><small>Daily timing uses UTC, including during daylight saving changes.</small></div>
            <input type="time" required aria-label="Backup time (UTC)" bind:value={draft.timeUtc} /></label>
          <label class="setting-field settings-row"><div><strong>Backup folder</strong><small>Local path inside the container. Leave blank for the backup folder in the data volume. Mount synchronized storage before choosing it.</small></div>
            <input type="text" aria-label="Backup folder" maxlength="2000" placeholder="Default backup folder" bind:value={draft.directory} /></label>
          <label class="setting-field settings-row"><div><strong>Daily copies</strong><small>Newest scheduled snapshot from each day.</small></div>
            <input type="number" aria-label="Daily copies" required min="1" max="90" bind:value={draft.dailyRetentionCount} /></label>
          <label class="setting-field settings-row"><div><strong>Weekly copies</strong><small>Newest scheduled snapshot from each week. One file can count as daily and weekly.</small></div>
            <input type="number" aria-label="Weekly copies" required min="0" max="52" bind:value={draft.weeklyRetentionCount} /></label>
          <label class="setting-field settings-row"><div><strong>Manual copies</strong><small>Newest snapshots created with Back up now. Backups from earlier versions are preserved.</small></div>
            <input type="number" aria-label="Manual copies" required min="1" max="1000" bind:value={draft.manualRetentionCount} /></label>
          <p class="settings-impact">Changes apply to future backups; existing archives stay in their folders.</p>
          <footer>{#if dirty}<span>Unsaved changes</span>{/if}<Button type="submit" disabled={!dirty || pending || !ready || Boolean(action)}>{action === "save" ? "Saving…" : "Save schedule"}</Button></footer>
        </fieldset>
      </form>
    {/if}

    <article class="settings-card maintenance-card" aria-label="Saved backups">
      <header><div><h3>Saved backups</h3><small>Audio, caches and extension packages need separate backups.</small></div></header>
      {#if !ready}
        <p class="backup-empty" role="status">The backup list is unavailable until durable storage is ready.</p>
      {:else if data.backups.length}
        <ul class="backup-list" aria-label="Saved backup files">
          {#each data.backups as backup (backup.id)}
            {@const retainedAs = backup.retentionBuckets?.filter(bucket => bucket !== "manual").map(bucket => bucket === "daily" ? "Daily" : "Weekly") ?? []}
            <li><span><strong>{backup.fileName}</strong><small>{date(backup.createdAt)} · {size(backup.bytes)} · {backup.status === "verified" ? "Verified" : backup.status === "changed" ? "File changed" : backup.status === "invalid" ? "Verification failed" : "Verify on download"}</small>
              {#if backup.origin}<small>{backup.origin === "scheduled" ? "Scheduled" : backup.origin === "manual" ? "Manual" : "Existing backup"}{retainedAs.length ? ` · ${retainedAs.join(" · ")}` : ""}</small>{/if}</span>
              <Button variant="secondary" size="sm" disabled={backup.status === "changed" || backup.status === "invalid"} href={settings.backupDownloadUrl(backup.id)} download={backup.fileName}>Download</Button></li>
          {/each}
        </ul>
      {:else}<p class="backup-empty">No backups yet.</p>{/if}
      <div class="settings-row"><div><strong>Restore from a backup</strong><small>Choose a backup ZIP (up to 1 GiB). It replaces saved state the next time Allstarr restarts.</small></div>
        <div class="backup-restore"><FilePicker label="Backup ZIP file" accept=".zip,application/zip" buttonLabel="Choose backup" disabled={Boolean(action) || pending || active} onselect={(files) => { restoreFile = files[0] ?? null; }} />
          <Button variant="secondary" disabled={!restoreFile || Boolean(action) || pending || active} onclick={() => { restoreError = ""; restoreOpen = true; }}>Restore on restart</Button></div></div>
    </article>
  {/if}
  <ConfirmDialog bind:open={restoreOpen} title="Stage backup restore?"
    description="The next restart replaces saved state and credentials with the contents of this backup. Previous files are preserved. Allstarr will not restart automatically."
    confirmLabel={action === "restore" ? "Staging…" : "Restore on restart"} confirmVariant="destructive" disabled={action === "restore" || !restoreFile}
    closeOnConfirm={false} error={restoreError} onConfirm={restore} />
{/if}
