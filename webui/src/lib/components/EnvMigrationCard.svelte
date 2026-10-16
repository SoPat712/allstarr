<script lang="ts">
  import ServiceAccessRestore from "$lib/components/ServiceAccessRestore.svelte";
  import { waitForRestart } from "$lib/restart";
  import { humanize } from "$lib/sources";
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { settings, type EnvMigrationPreview } from "$lib/api";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import FilePicker from "$lib/components/FilePicker.svelte";

  let status = $state<Awaited<ReturnType<typeof settings.migrationStatus>> | null>(null);
  let preview = $state<EnvMigrationPreview | null>(null);
  let file = $state<File | null>(null);
  let confirmed = $state(false);
  let action = $state("");
  let feedback = $state("");
  let failed = $state(false);
  let checks = $state<Array<{ providerId: string; reason: string; enabled: boolean }>>([]);
  let mediaQueued = $state(false);

  async function loadStatus() {
    try {
      status = await settings.migrationStatus();
    } catch (cause) {
      failed = true;
      feedback = cause instanceof Error ? cause.message : "Migration status is unavailable.";
    }
  }

  async function inspect(mounted = false) {
    if ((!mounted && !file) || action) return;
    action = "preview";
    feedback = "";
    failed = false;
    try {
      preview = mounted ? await settings.previewMountedMigration() : await settings.previewMigration(file!);
      confirmed = false;
    } catch (cause) {
      failed = true;
      feedback = cause instanceof Error ? cause.message : "The legacy environment could not be inspected.";
    } finally {
      action = "";
    }
  }

  async function apply() {
    if (!preview || !confirmed || action) return;
    action = "apply";
    feedback = "";
    failed = false;
    try {
      const result = await settings.applyMigration(preview);
      checks = result.accountChecks ?? [];
      mediaQueued = Boolean(result.mediaFilesQueued);
      toast.success(result.alreadyApplied ? "This migration was already applied." : "Legacy settings imported.");
      feedback = `${result.mediaFilesQueued ?? 0} original files queued for verification. Accounts that passed their connection check are ready; review unavailable accounts in Sources.`;
      preview = null;
      file = null;
      if (result.requiresRestart) {
        feedback += " Settings saved. Waiting for restart…";
        await waitForRestart();
        window.location.reload();
        return;
      }
      await loadStatus();
    } catch (cause) {
      failed = true;
      feedback = cause instanceof Error ? cause.message : "The legacy import could not be applied.";
    } finally {
      action = "";
    }
  }

  async function reset() {
    if (!preview || action) return;
    action = "reset";
    failed = false;
    try {
      await settings.resetMigration(preview.previewToken);
      preview = null;
      file = null;
      confirmed = false;
      toast.info("Preview discarded. Choose the corrected file to retry.");
    } catch (cause) {
      failed = true;
      feedback = cause instanceof Error ? cause.message : "The preview could not be reset.";
    } finally {
      action = "";
    }
  }

  function category(action: string) {
    if (/choose|setup_|review_|manual|target/.test(action)) return "Needs your choice";
    if (/import|create|save|adopt/.test(action)) return "Ready to import";
    return "Left unchanged";
  }
  onMount(() => void loadStatus());
</script>

<ServiceAccessRestore />
<article class="settings-card maintenance-card env-migration-card">
  <header>
    <div><strong>Import from Allstarr v2</strong><small>Bring settings, accounts, playlists and schedules over from an old <code>.env</code> file. Secrets are never shown.</small></div>
    <Badge state={status?.completed ? "healthy" : "suggested"}>
      {status?.completed ? "Imported" : "Optional"}
    </Badge>
  </header>

  {#if preview}
    <dl class="migration-summary">
      <div><dt>Settings</dt><dd>{preview.importedSettingCount}</dd></div>
      <div><dt>Accounts</dt><dd>{preview.providerAccountCount}</dd></div>
      <div><dt>Playlists</dt><dd>{preview.playlistLinkCount}</dd></div>
      <div><dt>Schedules</dt><dd>{preview.scheduleCount}</dd></div>
      <div><dt>Needs review</dt><dd>{preview.manualCount}</dd></div>
    </dl>
    <div class="migration-notes">
      {#each preview.providerAccounts ?? [] as account}<p><strong>{humanize(account.providerId)}:</strong> {account.reason}</p>{/each}
      {#if preview.backend}<p>{preview.backend.explanation}</p>{/if}
      {#if preview.media}<p>{preview.media.files} original audio files found; {preview.media.identified} have recognized source identities. Files stay in place. {preview.media.needsIdentification} remain available as read-only originals without an invented match.</p>{#each preview.media.notices as notice}<p>{notice}</p>{/each}{/if}
      {#each ["Ready to import", "Needs your choice", "Left unchanged"] as label}
        {@const items = preview.items.filter((item) => category(item.action) === label)}
        {#if items.length}<section><h4>{label} ({items.length})</h4><ul class="migration-items">{#each items as item}<li><span>{item.reason}</span><small>{item.key}{#if !item.sensitive && item.valuePreview} · {item.valuePreview}{/if}</small></li>{/each}</ul></section>{/if}
      {/each}
      {#each preview.conflicts as item}<p class="notice-error">{item}</p>{/each}
      {#each preview.warnings as item}<p class="credential-safety">{item}</p>{/each}
      <details class="migration-details">
        <summary>Review {preview.items.length} parsed settings</summary>
        <ul class="migration-items">
          {#each preview.items as item}
            <li><strong>{item.key}</strong><small>Line {item.sourceLine} · {item.action}</small><span>{item.reason}</span></li>
          {/each}
        </ul>
      </details>
      <label class="permission-confirm">
        <Checkbox bind:checked={confirmed} />
        <span>I reviewed these changes. Accounts are enabled only after a successful connection check. Existing saved settings win, and original files stay read-only.</span>
      </label>
    </div>
    <footer>
      <Button variant="secondary" disabled={Boolean(action)} onclick={() => void reset()}>{action === "reset" ? "Discarding…" : "Discard and retry"}</Button>
      <Button disabled={!confirmed || !preview.canApply || Boolean(action)} onclick={() => void apply()}>{action === "apply" ? "Importing…" : "Import preview"}</Button>
    </footer>
  {:else}
    <form onsubmit={(event) => { event.preventDefault(); void inspect(); }}>
      <div class="settings-row">
        <div><strong>Legacy environment file</strong><small>Nothing is imported until you review a preview.</small></div>
        <FilePicker label="Legacy environment file" onselect={(files) => { file = files[0] ?? null; }} />
      </div>
      <footer><Button variant="secondary" disabled={Boolean(action) || !status?.sourcePresent} onclick={() => void inspect(true)}>Preview mounted settings</Button><Button variant="secondary" type="submit" disabled={!file || Boolean(action)}>{action === "preview" ? "Inspecting…" : status?.completed ? "Preview revision" : "Preview import"}</Button></footer>
    </form>
  {/if}
  {#each checks as check}<p><strong>{humanize(check.providerId)} · {check.enabled ? "Enabled" : "Needs review"}</strong> {check.reason}</p>{/each}
  {#if mediaQueued}<a class="button secondary" href="#/activity">Follow file import in Activity</a>{/if}
  {#if feedback}<p class:notice-error={failed} class="migration-feedback" role={failed ? "alert" : "status"}>{feedback}</p>{/if}
</article>
