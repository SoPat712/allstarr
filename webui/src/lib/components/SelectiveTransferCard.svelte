<script lang="ts">
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import {
    settings,
    type SelectiveTransferOptions,
    type SelectiveTransferPreview,
    type SelectiveTransferReport,
  } from "$lib/api";
  import SelectField from "$lib/components/SelectField.svelte";

  const maximumBytes = 128 * 1024 * 1024;
  const categories: Array<{ key: keyof SelectiveTransferOptions; label: string }> = [
    { key: "settings", label: "Settings" },
    { key: "accounts", label: "Accounts" },
    { key: "playlists", label: "Playlists and mappings" },
    { key: "intelligence", label: "Intelligence" },
    { key: "extensions", label: "Extensions" },
  ];

  let options = $state<SelectiveTransferOptions>({
    settings: true,
    accounts: true,
    playlists: true,
    intelligence: true,
    extensions: true,
  });
  let mode = $state<"Conflict" | "Merge" | "Replace">("Conflict");
  let file = $state<File | null>(null);
  let preview = $state<SelectiveTransferPreview | null>(null);
  let result = $state<SelectiveTransferReport | null>(null);
  let busy = $state<"" | "export" | "preview" | "import">("");
  let feedback = $state("");
  let failed = $state(false);
  let confirmOpen = $state(false);
  let controller: AbortController | null = null;

  const selectedCount = $derived(Object.values(options).filter(Boolean).length);

  function resetPreview() {
    preview = null;
    result = null;
  }

  function setCategory(key: keyof SelectiveTransferOptions, included: boolean) {
    options = { ...options, [key]: included };
    resetPreview();
  }

  function selectFile(next?: File) {
    file = next ?? null;
    feedback = "";
    failed = false;
    resetPreview();
    if (file && file.size > maximumBytes) {
      feedback = "The archive exceeds the 128 MiB upload limit.";
      failed = true;
      file = null;
    }
  }

  function begin(name: typeof busy) {
    busy = name;
    feedback = "";
    failed = false;
    controller = new AbortController();
    return controller.signal;
  }

  function finish(cause?: unknown) {
    if (cause) {
      failed = (cause as Error).name !== "AbortError";
      feedback = failed
        ? cause instanceof Error ? cause.message : "State transfer failed."
        : "State transfer cancelled.";
    }
    busy = "";
    controller = null;
  }

  async function exportState() {
    if (!selectedCount || busy) return;
    try {
      const exported = await settings.exportState(options, begin("export"));
      const url = URL.createObjectURL(exported.blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = exported.filename;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url));
      feedback = "Selective archive downloaded.";
    } catch (cause) {
      finish(cause);
      return;
    }
    finish();
  }

  async function previewState() {
    if (!file || !selectedCount || busy) return;
    try {
      preview = await settings.previewState(file, mode, options, begin("preview"));
      feedback = preview.canImport
        ? "Archive validated. Review the report before importing."
        : "Archive validation found blockers.";
      failed = !preview.canImport;
    } catch (cause) {
      finish(cause);
      return;
    }
    finish();
  }

  async function importState() {
    confirmOpen = false;
    if (!file || !preview?.canImport || busy) return;
    try {
      const imported = await settings.importState(file, mode, options, begin("import"));
      result = imported.report;
      preview = null;
      feedback = imported.message;
    } catch (cause) {
      finish(cause);
      return;
    }
    finish();
  }

  function cancel() {
    controller?.abort();
  }
</script>

<article class="panel maintenance-card transfer-card">
  <header>
    <div>
      <strong>Selective state transfer</strong>
      <small>Move bounded durable state between compatible Allstarr installations.</small>
    </div>
    <Badge state="suggested">128 MiB max</Badge>
  </header>

  <fieldset class="transfer-categories" disabled={Boolean(busy)}>
    <legend>Categories</legend>
    {#each categories as category}
      <label>
        <Checkbox
          checked={options[category.key]}
          onCheckedChange={(checked) => setCategory(category.key, checked)}
        />
        <span>{category.label}</span>
      </label>
    {/each}
  </fieldset>

  <div class="transfer-actions">
    <Button variant="secondary" disabled={!selectedCount || Boolean(busy)} onclick={() => void exportState()}>
      {busy === "export" ? "Preparing export…" : "Export selected categories"}
    </Button>
    <label class={`${buttonVariants({ variant: "secondary" })} transfer-file`}>
      <span>{file ? "Choose another archive" : "Choose import archive"}</span>
      <input
        type="file"
        accept=".zip,application/zip"
        disabled={Boolean(busy)}
        onchange={(event) => selectFile(event.currentTarget.files?.[0])}
      />
    </label>
  </div>

  {#if file}
    <div class="transfer-file-summary">
      <span><strong>{file.name}</strong><small>{(file.size / 1024 / 1024).toFixed(1)} MiB of 128 MiB</small></span>
      <div class="filter-field">
        <span>Import behavior</span>
        <SelectField bind:value={mode} label="Import behavior" disabled={Boolean(busy)} onchange={resetPreview} options={[
          { value: "Conflict", label: "Require empty target" }, { value: "Merge", label: "Merge compatible rows" },
          { value: "Replace", label: "Replace selected categories" },
        ]} />
      </div>
      <Button disabled={!selectedCount || Boolean(busy)} onclick={() => void previewState()}>
        {busy === "preview" ? "Validating…" : "Validate archive"}
      </Button>
    </div>
  {/if}

  {#if busy}
    <div class="transfer-progress" role="status">
      <progress aria-label={`${busy} in progress`}></progress>
      <span>{busy === "preview" ? "Server is validating dependencies and conflicts." : busy === "import" ? "Applying the validated archive atomically." : "Server is creating the archive."}</span>
      <Button variant="secondary" size="sm" onclick={cancel}>Cancel</Button>
    </div>
  {/if}

  {#if feedback}
    <p class:notice-error={failed} class="transfer-feedback" role={failed ? "alert" : "status"}>{feedback}</p>
  {/if}

  {#if preview}
    <section class="transfer-report" aria-label="Selective transfer preview">
      <header><strong>Validated preview</strong><Badge state={preview.canImport ? "healthy" : "degraded"}>{preview.canImport ? "Ready" : "Blocked"}</Badge></header>
      <p>{preview.report.totalRows} rows across {preview.report.includedCategories.length} selected categories.</p>
      {#if preview.dependencies.length}<p><strong>Dependencies:</strong> {preview.dependencies.join(", ")}</p>{/if}
      {#if preview.conflicts.length}
        <ul>{#each preview.conflicts as conflict}<li>{conflict}</li>{/each}</ul>
      {/if}
      <Button variant={mode === "Replace" ? "destructive" : "default"} disabled={!preview.canImport || Boolean(busy)} onclick={() => { confirmOpen = true; }}>
        Import validated archive
      </Button>
    </section>
  {/if}

  {#if result}
    <section class="transfer-report" aria-label="Selective transfer result">
      <header><strong>Import complete</strong><Badge state="healthy">{result.totalRows} rows</Badge></header>
      <dl class="transfer-rows">
        {#each Object.entries(result.rowsByEntry) as [entry, rows]}
          <div><dt>{entry}</dt><dd>{rows}</dd></div>
        {/each}
      </dl>
    </section>
  {/if}
</article>

{#if confirmOpen}
  <ConfirmDialog
    bind:open={confirmOpen}
    title={mode === "Replace" ? "Replace selected state?" : "Import validated state?"}
    description={mode === "Replace"
      ? "Selected target categories will be replaced atomically. This cannot be undone without a backup."
      : "The validated rows will be imported atomically using the selected conflict policy."}
    confirmLabel={mode === "Replace" ? "Replace and import" : "Import archive"}
    confirmVariant={mode === "Replace" ? "destructive" : "default"}
    disabled={Boolean(busy)}
    onConfirm={importState}
  />
{/if}
