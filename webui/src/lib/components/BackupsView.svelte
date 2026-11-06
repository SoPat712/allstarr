<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { formatLocalDateTime } from "$lib/dates";
  import { onMount } from "svelte";
  import { ApiError, settings, type BackupPolicy, type BackupPolicyInput, type StorageOverview, type StorageBackup } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import FilePicker from "$lib/components/FilePicker.svelte";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Archive, MoreHorizontal, X } from "@lucide/svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  let pickerRevision = $state(0);
  let selectedBackup = $state<StorageBackup | null>(null), restoreText = $state(""), backupFirst = $state(true);
  let form = $state<HTMLFormElement>();
  let saveTimer: ReturnType<typeof setTimeout>;
  function scheduleSave() { clearTimeout(saveTimer); saveTimer = setTimeout(() => { if (form?.checkValidity()) void save(); }, 150); }
  function openRestore(backup: StorageBackup | null = null) { selectedBackup = backup; restoreText = ""; backupFirst = true; restoreError = ""; restoreOpen = true; }

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
    return formatLocalDateTime(value, "Not available");
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

  async function save(undo = true) {
    if (!draft || !saved || action || pending || !ready) return;
    if (!dirty || changed) return;
    const previous = input(saved);
    action = "save";
    try {
      const result = await settings.saveBackupPolicy({ ...draft, expectedRevision: saved.revision });
      saved = result.backupPolicy;
      draft = input(saved);
      if (data) data = { ...data, ...result };
      changed = false;
      toast.success("Backup schedule saved.", undo ? { label: "Undo", run: async () => { draft = previous; await save(false); } } : undefined);
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
    if ((!restoreFile && !selectedBackup) || action || pending || restoreText !== "RESTORE") return;
    action = "restore"; restoreError = "";
    try {
      if (backupFirst) {
        const before = status?.lastSuccessAt;
        await settings.backup();
        let verified = false;
        for (let attempt = 0; attempt < 60 && !disposed; attempt++) {
          const next = await settings.storage();
          data = next;
          if (next.backupStatus?.lastSuccessAt && next.backupStatus.lastSuccessAt !== before && !next.backupStatus.activeJobId) { verified = true; break; }
          if (next.backupStatus?.lastFailure && !next.backupStatus.activeJobId) throw new Error(next.backupStatus.lastFailure.message);
          await new Promise(resolve => setTimeout(resolve, 2000));
        }
        if (!verified) throw new Error("The safety backup has not finished. Nothing was restored. Check its status and try again.");
      }
      let file = restoreFile;
      if (selectedBackup) {
        const response = await fetch(settings.backupDownloadUrl(selectedBackup.id), { credentials: "same-origin", cache: "no-store" });
        if (!response.ok) throw new Error("This backup could not be downloaded and verified. Nothing was restored.");
        if (Number(response.headers.get("content-length")) > 1024 ** 3) throw new Error("Choose a backup ZIP smaller than 1 GiB.");
        const blob = await response.blob();
        file = new File([blob], selectedBackup.fileName, { type: "application/zip" });
      }
      if (!file || file.size > 1024 ** 3) throw new Error("Choose a backup ZIP smaller than 1 GiB.");
      const result = await settings.restore(file);
      toast.success(result.message);
      restoreOpen = false; restoreFile = null; selectedBackup = null;
      await refresh();
    } catch (cause) { restoreError = message(cause, "Backup restore could not be staged."); }
    finally { action = ""; }
  }
  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { disposed = true; clearTimeout(timer); clearTimeout(saveTimer); scheduler.cancel(); unsubscribe(); };
  });
</script>

{#if administrator}
  <div class="settings-stack">
    <PageStatus items={[{ label: status?.lastSuccessAt ? `Last backup verified ${date(status.lastSuccessAt)}` : "No verified backup yet" }, ...(status?.lastBytes ? [{ label: size(status.lastBytes) }] : []), { label: data?.backupPolicy?.enabled ? `Next ${date(status?.nextRunAt)}` : "Scheduling off" }]} />
    {#if error}<ErrorState title="Backup status is unavailable" detail={error} onretry={() => { polls = 0; return refresh(); }} />{/if}
    {#if !data && !error}<RowsSkeleton count={4} />
    {:else if data}
      <div class="settings-toolbar"><output aria-label="Backup status" class:sr-only={stateLabel === "Ready"}>{stateLabel}</output><div class="backup-actions"><RefreshButton disabled={Boolean(action)} onrefresh={() => { polls = 0; return refresh(); }} label="Refresh status" /><Button disabled={Boolean(action) || !ready || pending || active || directoryUnavailable} onclick={() => start()}>{action === "backup" ? "Queuing…" : "Back up now"}</Button></div></div>
      {#if pending}<p class="notice-info" role="status">A restore is staged. Restart Allstarr to finish replacing saved state and credentials.</p>{/if}
      {#if failed && status?.lastFailure}<ErrorState title="Last backup failed" detail={`${status.lastFailure.message} · ${date(status.lastFailure.at)}`} />{/if}
      {#if active && polls >= 60}<p role="status">The backup is still running. Refresh its status to check again.</p>{/if}
      {#if draft && saved}
        {#if changed}<div class="degraded-banner" role="status"><p><strong>Backup settings changed.</strong> Your unsaved edits are kept.</p><Button variant="outline" disabled={Boolean(action) || refreshing} onclick={() => refresh(true)}>Reload server values</Button></div>{/if}
        <form bind:this={form} class="settings-card" aria-label="Backup schedule" onchange={scheduleSave} onsubmit={event => { event.preventDefault(); scheduleSave(); }}>
          <header><h3>Backup schedule</h3></header>
          <fieldset class="settings-fields" disabled={Boolean(action) || pending || !ready}>
            <label class="setting-field settings-row"><div><strong>Daily backups</strong><small>Keep an automatic snapshot each day.</small></div><span class="settings-toggle"><Switch aria-label="Daily backups" bind:checked={draft.enabled} /></span></label>
            <label class="setting-field settings-row"><div><strong>Backup time (UTC)</strong><small>Uses UTC, including during daylight saving changes.</small></div><input type="time" required aria-label="Backup time (UTC)" bind:value={draft.timeUtc} /></label>
            <label class="setting-field settings-row"><div><strong>Daily copies</strong></div><input type="number" aria-label="Daily copies" required min="1" max="90" bind:value={draft.dailyRetentionCount} /></label>
            <label class="setting-field settings-row"><div><strong>Weekly copies</strong><small>One file can count as both daily and weekly.</small></div><input type="number" aria-label="Weekly copies" required min="0" max="52" bind:value={draft.weeklyRetentionCount} /></label>
            <AdvancedFold description="folder and manual copies">
              <label class="setting-field settings-row"><div><strong>Backup folder</strong><small>Local path inside the container. Leave blank to use the data folder. Mount synchronized storage before choosing it.</small></div><input type="text" aria-label="Backup folder" maxlength="2000" placeholder="Default backup folder" bind:value={draft.directory} /></label>
              <label class="setting-field settings-row"><div><strong>Manual copies</strong><small>Newest snapshots created with Back up now. Backups from earlier versions are preserved.</small></div><input type="number" aria-label="Manual copies" required min="1" max="1000" bind:value={draft.manualRetentionCount} /></label>
            </AdvancedFold>
          </fieldset>
        </form>
      {/if}
      <article class="settings-card" aria-label="Saved backups"><header><h3>Saved backups</h3></header>
        {#if !ready}<p class="settings-impact" role="status">The backup list is unavailable until durable storage is ready.</p>
        {:else if data.backups.length}
          <table class="settings-table"><thead><tr><th>Made</th><th>Size</th><th>State</th><th></th></tr></thead><tbody>
            {#each data.backups as backup (backup.id)}<tr title={backup.fileName}><td>{date(backup.createdAt)}<small>{backup.origin === "scheduled" ? "Scheduled" : backup.origin === "manual" ? "Manual" : "Existing backup"}{backup.retentionBuckets?.length ? ` · ${backup.retentionBuckets.join(", ")}` : ""}</small></td><td class="number">{size(backup.bytes)}</td><td><StatePill tone={backup.status === "verified" ? "ok" : backup.status === "changed" || backup.status === "invalid" ? "warn" : "neutral"} text={backup.status === "verified" ? "Verified" : backup.status === "changed" ? "File changed" : backup.status === "invalid" ? "Verification failed" : "Verify on download"} /></td><td><div class="backup-actions"><Button variant="text" size="sm" disabled={backup.status === "changed" || backup.status === "invalid"} href={settings.backupDownloadUrl(backup.id)} download={backup.fileName}>Download</Button><DropdownMenu.Root><DropdownMenu.Trigger class="icon-button" aria-label={`More actions for ${date(backup.createdAt)}`}><MoreHorizontal size={18} /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content class="menu-content"><DropdownMenu.Item class="menu-item settings-danger" disabled={Boolean(action) || pending || active || backup.status === "changed" || backup.status === "invalid"} onSelect={() => openRestore(backup)}>Restore…</DropdownMenu.Item></DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root></div></td></tr>{/each}
          </tbody></table>
        {:else}<EmptyState title="No backups yet" description="Back up now to save your settings, accounts and history.">{#snippet icon()}<Archive size={24} />{/snippet}</EmptyState>{/if}
        <footer class="restore-upload" class:has-file={Boolean(restoreFile)}><div class="settings-row"><div><strong>Restore from a file</strong><small>Choose a backup ZIP. Saved sign-ins are included; audio and caches need separate backups.</small></div>{#key pickerRevision}<FilePicker label="Backup ZIP file" accept=".zip,application/zip" buttonLabel="Choose a backup ZIP" disabled={Boolean(action) || pending || active} onselect={files => { restoreFile = files[0] ?? null; if (restoreFile) openRestore(); }} />{/key}</div></footer>
      </article>
    {/if}
  </div>
  <Dialog.Root bind:open={restoreOpen} onOpenChange={open => { if (!open && action !== "restore") { restoreFile = null; selectedBackup = null; restoreText = ""; pickerRevision++; } }}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog settings-flow-dialog" onEscapeKeydown={event => { if (action === "restore") event.preventDefault(); }} onInteractOutside={event => { if (action === "restore") event.preventDefault(); }}>
      <header><div><Dialog.Title>{selectedBackup ? `Restore the ${date(selectedBackup.createdAt)} backup?` : "Restore a backup?"}</Dialog.Title><Dialog.Description>{selectedBackup ? `${size(selectedBackup.bytes)} · ${selectedBackup.status}.` : restoreFile?.name} Allstarr goes back to how it was when this backup was made.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close restore" disabled={action === "restore"}><X size={18} /></Dialog.Close></header>
      <form class="settings-dialog-content" onsubmit={event => { event.preventDefault(); void restore(); }}>
        <div class="restore-columns"><div><h3>Replaced</h3><ul><li>Accounts and sign-ins</li><li>Playlists, matches and your choices</li><li>Listening history and mixes</li><li>Settings</li></ul></div><div><h3>Kept as they are</h3><ul><li>Audio files and kept songs</li><li>The track cache</li><li>Installed extensions</li></ul></div></div>
        <p class="notice-info">Changes since this backup are lost when the restore takes effect. The backup is staged for the next restart. Allstarr does not restart automatically.</p>
        <label class="backup-first"><Checkbox aria-label="Back up the current state first" bind:checked={backupFirst} disabled={action === "restore"} /><span>Back up the current state first</span></label>
        <label class="field"><span>Type RESTORE to confirm</span><input aria-label="Type RESTORE to confirm" bind:value={restoreText} autocomplete="off" spellcheck="false" disabled={action === "restore"} /></label>
        {#if restoreError}<p class="notice-error" role="alert">{restoreError}</p>{/if}
        <footer class="dialog-actions"><Dialog.Close type="button" class={buttonVariants({ variant: "text" })} disabled={action === "restore"}>Cancel</Dialog.Close><Button variant="destructive" type="submit" disabled={restoreText !== "RESTORE" || Boolean(action) || (!restoreFile && !selectedBackup)}>{action === "restore" ? backupFirst ? "Backing up and staging…" : "Staging…" : "Restore on restart"}</Button></footer>
      </form>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>
{/if}
<style>
  .restore-upload .settings-row { padding: 0; width: 100%; }
  .restore-upload:not(.has-file) :global(.file-picker-name) { display: none; }

  .backup-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: end; gap: var(--space-2); }
  .restore-upload { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); }
  .restore-upload small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .restore-columns { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-4); }
  .restore-columns h3 { margin: 0; font-size: var(--text-sm); }
  .restore-columns ul { margin: var(--space-2) 0 0; padding-left: var(--space-4); color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.7; }
  .backup-first { display: flex; align-items: center; gap: var(--space-3); min-height: 44px; font-size: var(--text-sm); }
  @media (max-width: 650px) { .restore-columns { grid-template-columns: minmax(0, 1fr); } .settings-table :is(td, th):nth-child(2) { display: none; } .backup-actions { gap: var(--space-1); } }
</style>
