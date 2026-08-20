<script lang="ts">
  import { onMount } from "svelte";
  import { settings, type EnvMigrationPreview } from "$lib/api";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";

  let status = $state<Awaited<ReturnType<typeof settings.migrationStatus>> | null>(null);
  let preview = $state<EnvMigrationPreview | null>(null);
  let file = $state<File | null>(null);
  let confirmed = $state(false);
  let action = $state("");
  let feedback = $state("");
  let failed = $state(false);

  async function loadStatus() {
    try {
      status = await settings.migrationStatus();
    } catch (cause) {
      failed = true;
      feedback = cause instanceof Error ? cause.message : "Migration status is unavailable.";
    }
  }

  async function inspect() {
    if (!file || action) return;
    action = "preview";
    feedback = "";
    failed = false;
    try {
      preview = await settings.previewMigration(file);
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
      feedback = result.alreadyApplied ? "This migration was already applied." : "Legacy settings imported.";
      preview = null;
      file = null;
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
      feedback = "Preview discarded. Choose the corrected file to retry.";
    } catch (cause) {
      failed = true;
      feedback = cause instanceof Error ? cause.message : "The preview could not be reset.";
    } finally {
      action = "";
    }
  }

  onMount(() => void loadStatus());
</script>

<article class="panel maintenance-card">
  <header>
    <div><strong>Legacy v2 import</strong><small>One-time durable migration</small></div>
    <Badge state={status?.completed ? "healthy" : "suggested"}>
      {status?.completed ? "Imported" : "Optional"}
    </Badge>
  </header>
  <p>Preview a legacy <code>.env</code> locally, then import supported settings, accounts, playlists, and schedules into PostgreSQL. Secrets are never echoed.</p>

  {#if preview}
    <dl>
      <div><dt>Runtime settings</dt><dd>{preview.importedSettingCount}</dd></div>
      <div><dt>Provider accounts</dt><dd>{preview.providerAccountCount}</dd></div>
      <div><dt>Playlist links</dt><dd>{preview.playlistLinkCount}</dd></div>
      <div><dt>Schedules</dt><dd>{preview.scheduleCount}</dd></div>
      <div><dt>Needs review</dt><dd>{preview.manualCount}</dd></div>
    </dl>
    {#each preview.conflicts as item}<p class="notice-error">{item}</p>{/each}
    {#each preview.warnings as item}<p class="credential-safety">{item}</p>{/each}
    <details>
      <summary class="disclosure-summary"><DisclosureLabel title={`Review ${preview.items.length} parsed settings`} description="Inspect every detected value before importing" /></summary>
      <ul class="migration-items">
        {#each preview.items as item}
          <li><strong>{item.key}</strong><small>Line {item.sourceLine} · {item.action}</small><span>{item.reason}</span></li>
        {/each}
      </ul>
    </details>
    <label class="permission-confirm">
      <Checkbox bind:checked={confirmed} />
      <span>I reviewed this preview and understand that imported accounts remain disabled when required.</span>
    </label>
    <div class="maintenance-actions">
      <Button variant="secondary" disabled={Boolean(action)} onclick={() => void reset()}>{action === "reset" ? "Discarding…" : "Discard and retry"}</Button>
      <Button disabled={!confirmed || !preview.canApply || Boolean(action)} onclick={() => void apply()}>{action === "apply" ? "Importing…" : "Import preview"}</Button>
    </div>
  {:else}
    <form class="settings-fields" onsubmit={(event) => { event.preventDefault(); void inspect(); }}>
      <label class="setting-field"><span><strong>Legacy environment file</strong></span><input type="file" onchange={(event) => { file = event.currentTarget.files?.[0] ?? null; }} /></label>
      <Button variant="secondary" type="submit" disabled={!file || Boolean(action)}>{action === "preview" ? "Inspecting…" : status?.completed ? "Preview revision" : "Preview import"}</Button>
    </form>
  {/if}
  {#if feedback}<p class={failed ? "notice-error" : "action-feedback"} role={failed ? "alert" : "status"}>{feedback}</p>{/if}
</article>
