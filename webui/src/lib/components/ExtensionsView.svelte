<script lang="ts">
  import { onMount } from "svelte";
  import { AlertDialog, Dialog } from "bits-ui";
  import {
    extensions,
    type ExtensionLog,
    type ExtensionPackage,
    type ExtensionPermission,
    type ExtensionRegistry,
    type ExtensionStoreItem,
  } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import { availablePackages, currentPackages, valueChanges } from "$lib/extensions";
  import { humanize } from "$lib/sources";

  const tabs = ["installed", "available", "registries", "activity"] as const;
  let tab = $state<(typeof tabs)[number]>("installed");
  let packages = $state<ExtensionPackage[]>([]);
  let registries = $state<ExtensionRegistry[]>([]);
  let store = $state<ExtensionStoreItem[]>([]);
  let storeErrors = $state<Array<{ repository: string; message: string }>>([]);
  let logs = $state<ExtensionLog[]>([]);
  let loading = $state(true);
  let action = $state("");
  let feedback = $state("");
  let search = $state("");
  let installOpen = $state(false);
  let reviewOpen = $state(false);
  let reviewPackage = $state<ExtensionPackage | null>(null);
  let previousPackage = $state<ExtensionPackage | null>(null);
  let permissions = $state<ExtensionPermission[]>([]);
  let previousPermissions = $state<ExtensionPermission[]>([]);
  let decisions = $state<Record<string, boolean>>({});
  let permissionConfirmed = $state(false);
  let removePackage = $state<ExtensionPackage | null>(null);
  let removeRegistry = $state<ExtensionRegistry | null>(null);
  let removeOpen = $state(false);

  const installed = $derived(currentPackages(packages));
  const available = $derived(availablePackages(store, installed)
    .filter((item) => `${item.displayName} ${item.description ?? ""}`.toLowerCase().includes(search.toLowerCase())));
  const extensionTabs = $derived(tabs.map((id) => ({
    id,
    label: humanize(id),
    count: id === "installed" ? installed.length :
      id === "available" ? available.length :
        id === "registries" ? registries.length : undefined,
  })));
  const permissionChanges = $derived(valueChanges(
    permissions.map((item) => `${item.permissionKind}:${item.permissionValue}`),
    previousPermissions.map((item) => `${item.permissionKind}:${item.permissionValue}`),
  ));
  const capabilityChanges = $derived(valueChanges(
    reviewPackage?.capabilities ?? [],
    previousPackage?.capabilities ?? [],
  ));

  function definition(item: ExtensionPackage | ExtensionStoreItem) {
    return {
      id: "extensionId" in item ? item.extensionId : item.id,
      name: item.displayName,
      logoUrl: item.iconUrl,
      categories: "extensionId" in item ? item.capabilities : item.types,
    };
  }

  async function refresh() {
    const results = await Promise.allSettled([
      extensions.packages(), extensions.registries(), extensions.store(), extensions.logs(),
    ]);
    if (results[0].status === "fulfilled") packages = results[0].value;
    if (results[1].status === "fulfilled") registries = results[1].value;
    if (results[2].status === "fulfilled") {
      store = results[2].value.items;
      storeErrors = results[2].value.errors;
    }
    if (results[3].status === "fulfilled") logs = results[3].value;
    const failed = results.find((result) => result.status === "rejected");
    if (failed?.status === "rejected")
      feedback = failed.reason instanceof Error ? failed.reason.message : "Some extension data is unavailable.";
    loading = false;
  }

  async function run(key: string, operation: () => Promise<unknown>, message: string) {
    if (action) return;
    action = key;
    try {
      await operation();
      feedback = message;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : `${message} failed.`;
    } finally {
      action = "";
    }
  }

  async function stage(item: ExtensionStoreItem) {
    if (action) return;
    action = `install:${item.id}`;
    try {
      const result = await extensions.install(item);
      installOpen = false;
      await refresh();
      const staged = packages.find((entry) => entry.id === result.packageId);
      feedback = result.message;
      if (staged?.permissionReviewRequired) await openReview(staged);
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Extension could not be staged.";
    } finally {
      action = "";
    }
  }

  async function stageDirect(event: SubmitEvent) {
    event.preventDefault();
    const data = new FormData(event.currentTarget as HTMLFormElement);
    await stage({
      id: "",
      displayName: "Direct package",
      version: "",
      downloadUrl: String(data.get("downloadUrl") ?? ""),
      sha256: String(data.get("sha256") ?? ""),
      registryId: String(data.get("registryId") ?? "") || null,
    });
  }

  async function openReview(item: ExtensionPackage) {
    try {
      previousPackage = packages.find((entry) => entry.id === item.previousPackageId) ?? null;
      [permissions, previousPermissions] = await Promise.all([
        extensions.permissions(item.id),
        previousPackage ? extensions.permissions(previousPackage.id) : Promise.resolve([]),
      ]);
      reviewPackage = item;
      decisions = {};
      permissionConfirmed = false;
      reviewOpen = true;
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Permissions could not be loaded.";
    }
  }

  function permissionHelp(kind: string) {
    if (kind.toLowerCase() === "network") return ["↗", "Can contact only this approved HTTPS origin."];
    if (kind.toLowerCase() === "secret") return ["◆", "Can read this named encrypted account setting."];
    return ["▣", "Can use this named, quota-limited extension cache."];
  }

  async function approve() {
    if (!reviewPackage || permissions.some((item) => decisions[item.id] === undefined)) return;
    action = `review:${reviewPackage.id}`;
    try {
      const reviewed = await extensions.review(reviewPackage, permissions.map((item) => ({
        kind: item.permissionKind,
        value: item.permissionValue,
        approved: decisions[item.id],
      })));
      if (reviewed.state.toLowerCase() === "staged") await extensions.activate(reviewed);
      reviewPackage = null;
      reviewOpen = false;
      feedback = "Extension permissions saved and runtime enabled.";
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Permission review failed.";
    } finally {
      action = "";
    }
  }

  async function cancelReview() {
    if (!reviewPackage) return;
    await run(`cancel:${reviewPackage.id}`, () => extensions.cancelStaging(reviewPackage!), "Extension installation cancelled.");
    reviewPackage = null;
    reviewOpen = false;
  }

  function confirmRemoval(item: ExtensionPackage | ExtensionRegistry) {
    if ("extensionId" in item) {
      removePackage = item;
      removeRegistry = null;
    } else {
      removeRegistry = item;
      removePackage = null;
    }
    removeOpen = true;
  }

  async function remove() {
    if (removePackage)
      await run(`remove:${removePackage.id}`, () => extensions.uninstall(removePackage!), "Extension uninstalled. Saved Source accounts were retained.");
    else if (removeRegistry)
      await run(`registry:${removeRegistry.id}`, () => extensions.removeRegistry(removeRegistry!), "Extension registry removed.");
    removeOpen = false;
    removePackage = null;
    removeRegistry = null;
  }

  async function addRegistry(event: SubmitEvent) {
    event.preventDefault();
    const data = new FormData(event.currentTarget as HTMLFormElement);
    await run("registry:add", () => extensions.addRegistry(
      String(data.get("name") ?? ""), String(data.get("registryUrl") ?? ""),
    ), "Registry validated and added.");
    (event.currentTarget as HTMLFormElement).reset();
  }

  function dependencies(registry: ExtensionRegistry) {
    return installed.filter((item) => item.registryId === registry.id);
  }

  onMount(() => void refresh());
</script>

{#if loading}
  <section class="panel skeleton-panel extension-skeleton" aria-label="Loading extensions" aria-busy="true"></section>
{:else}
  <section class="extension-workspace">
    <header class="extension-header">
      <div><strong>Extension manager</strong><small>Verified packages, explicit permissions, and reversible lifecycle actions.</small></div>
      <button class="button-primary" type="button" onclick={() => { installOpen = true; }}>Install extension</button>
    </header>
    <SegmentedNav
      items={extensionTabs}
      active={tab}
      label="Extension views"
      class="extension-tabs"
      onchange={(id) => { tab = id as typeof tab; }}
    />
    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

    {#if tab === "installed"}
      <div class="extension-list">
        {#each installed as item}
          <article class="panel extension-row">
            <ProviderMark id={item.extensionId} definition={definition(item)} />
            <div class="extension-copy"><span><strong>{item.displayName}</strong><small>v{item.version}{item.author ? ` · ${item.author}` : ""}</small></span><p>{item.description || "No description supplied."}</p><div>{#each item.capabilities ?? [] as capability}<span class="chip">{humanize(capability)}</span>{/each}</div></div>
            <span class={`status-pill ${item.active ? "healthy" : item.state === "failed" ? "degraded" : "suggested"}`}>{humanize(item.state)}</span>
            <div class="extension-actions">
              {#if item.permissionReviewRequired}<button class="button-primary" type="button" onclick={() => void openReview(item)}>Review permissions</button>
              {:else if ["staged", "disabled"].includes(item.state.toLowerCase())}<button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void run(item.id, () => extensions.activate(item), "Extension enabled.")}>Enable</button>
              {:else if item.active}<button type="button" disabled={Boolean(action)} onclick={() => void run(item.id, () => extensions.disable(item), "Extension disabled.")}>Disable</button>{/if}
              {#if item.active && item.previousPackageId}<button type="button" disabled={Boolean(action)} onclick={() => void run(item.id, () => extensions.rollback(item), "Previous extension version restored.")}>Rollback</button>{/if}
              {#if ["active", "disabled"].includes(item.state.toLowerCase())}<button type="button" disabled={Boolean(action)} onclick={() => void run(item.id, () => extensions.revokePermissions(item), "Permissions revoked. Review is required before re-enabling.")}>Review access</button>{/if}
              <button class="danger-text" type="button" onclick={() => confirmRemoval(item)}>Uninstall</button>
            </div>
          </article>
        {:else}
          <div class="panel compact-empty"><strong>No extensions installed</strong><p>Install from a connected registry or a verified package URL.</p></div>
        {/each}
      </div>
    {:else if tab === "available"}
      <section class="panel extension-catalog">
        <header><div><strong>Available packages</strong><small>Updates appear beside new extensions.</small></div><input aria-label="Search extensions" placeholder="Search extensions…" bind:value={search} /></header>
        {#each storeErrors as item}<p class="error-text">{item.repository}: {item.message}</p>{/each}
        <div>
          {#each available as item}
            <article>
              <ProviderMark id={item.id} definition={definition(item)} />
              <span><strong>{item.displayName}</strong><small>v{item.version}{item.author ? ` · ${item.author}` : ""}</small><p>{item.description || "No description supplied."}</p></span>
              <button class="button-primary" type="button" disabled={!item.sha256 || Boolean(action)} onclick={() => void stage(item)}>{action === `install:${item.id}` ? "Verifying…" : installed.some((entry) => entry.extensionId.toLowerCase() === item.id.toLowerCase()) ? "Review update" : "Install"}</button>
            </article>
          {:else}<div class="compact-empty"><strong>No matching packages</strong><p>Everything may already be current.</p></div>{/each}
        </div>
      </section>
    {:else if tab === "registries"}
      <div class="extension-registry-layout">
        <form class="panel extension-registry-form" onsubmit={(event) => void addRegistry(event)}>
          <header><strong>Add registry</strong><small>The URL is validated before it is saved.</small></header>
          <label><span>Name</span><input name="name" required maxlength="200" placeholder="Community catalog" /></label>
          <label><span>Registry JSON URL</span><input name="registryUrl" type="url" required pattern="https://.*" placeholder="https://example.org/registry.json" /></label>
          <button class="button-primary" type="submit" disabled={Boolean(action)}>{action === "registry:add" ? "Validating…" : "Validate and add"}</button>
        </form>
        <div class="extension-registry-list">
          {#each registries as item}
            {@const usedBy = dependencies(item)}
            <article class="panel">
              <span><strong>{item.name}</strong><small>{item.registryUrl}</small>{#if usedBy.length}<em>{usedBy.length} installed package version{usedBy.length === 1 ? "" : "s"} must be removed first.</em>{/if}</span>
              <span class={`status-pill ${item.enabled ? "healthy" : "suggested"}`}>{item.enabled ? "Enabled" : "Disabled"}</span>
              <div><button type="button" disabled={Boolean(action)} onclick={() => void run(`registry:${item.id}`, () => extensions.setRegistryEnabled(item, !item.enabled), `Registry ${item.enabled ? "disabled" : "enabled"}.`)}>{item.enabled ? "Disable" : "Enable"}</button><button class="danger-text" type="button" disabled={usedBy.length > 0} onclick={() => confirmRemoval(item)}>Remove</button></div>
            </article>
          {:else}<div class="panel compact-empty"><strong>No registries connected</strong></div>{/each}
        </div>
      </div>
    {:else}
      <section class="panel extension-activity">
        <header><strong>Extension activity</strong><small>Install, permission, runtime, and lifecycle events.</small></header>
        {#each logs as item}
          <details><summary><span class={`activity-dot level-${item.level.toLowerCase()}`}></span><span><strong>{item.summary}</strong><small>{item.extensionId || "Extension runtime"}</small></span><time>{new Date(item.createdAt).toLocaleString()}</time></summary><p>{item.message || "No additional details were recorded."}</p></details>
        {:else}<div class="compact-empty"><strong>No extension activity recorded</strong></div>{/each}
      </section>
    {/if}
  </section>

  <Dialog.Root bind:open={installOpen}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-install-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Install extension</Dialog.Title><Dialog.Description>Use a registry package or provide a verified HTTPS package and checksum.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close installer">×</Dialog.Close></header>
      <form class="settings-fields" onsubmit={(event) => void stageDirect(event)}>
        <label class="setting-field"><span><strong>Package URL</strong></span><input name="downloadUrl" type="url" required pattern="https://.*" autocomplete="off" /></label>
        <label class="setting-field"><span><strong>SHA-256 checksum</strong></span><input name="sha256" required minlength="64" maxlength="64" pattern="[A-Fa-f0-9]{64}" autocomplete="off" spellcheck="false" /></label>
        <label class="setting-field"><span><strong>Registry attribution</strong></span><select name="registryId"><option value="">Direct package</option>{#each registries.filter((item) => item.enabled) as item}<option value={item.id}>{item.name}</option>{/each}</select></label>
        <footer><Dialog.Close class="button-secondary">Cancel</Dialog.Close><button class="button-primary" type="submit" disabled={Boolean(action)}>Verify package</button></footer>
      </form>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <Dialog.Root open={reviewOpen}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-review-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Review permissions</Dialog.Title><Dialog.Description>{reviewPackage?.displayName} needs explicit access before its runtime can start.</Dialog.Description></div></header>
      {#if previousPackage}
        <p class="credential-safety">Update {previousPackage.version} → {reviewPackage?.version}. Capability and permission changes are shown below.</p>
        <div class="source-capabilities" aria-label={`Capability changes from ${previousPackage.version} to ${reviewPackage?.version}`}>
          {#each capabilityChanges as item}<span class:ready={item.change === "added"}>{item.change === "added" ? "+" : item.change === "removed" ? "−" : "="} {humanize(item.value)}</span>{/each}
        </div>
      {/if}
      <div class="extension-permissions">
        {#each permissions as item}
          {@const help = permissionHelp(item.permissionKind)}
          {@const change = permissionChanges.find((entry) => entry.value === `${item.permissionKind}:${item.permissionValue}`)?.change}
          <article><span><strong>{help[0]} {humanize(item.permissionKind)}</strong><small>{item.permissionValue} · {help[1]}</small><em>{change === "added" ? "New access" : previousPackage ? "Unchanged" : "New install"}{item.required ? " · Required" : ""}</em></span><div role="group" aria-label={`Decision for ${item.permissionKind}`}><button class:button-primary={decisions[item.id] === true} type="button" onclick={() => { decisions = { ...decisions, [item.id]: true }; }}>Allow</button><button class:button-danger={decisions[item.id] === false} type="button" onclick={() => { decisions = { ...decisions, [item.id]: false }; }}>Deny</button></div></article>
        {/each}
        {#each permissionChanges.filter((item) => item.change === "removed") as item}
          <article><span><strong>− Removed access</strong><small>{item.value.replace(":", " · ")}</small><em>No longer requested</em></span></article>
        {/each}
      </div>
      <label class="permission-confirm"><input type="checkbox" bind:checked={permissionConfirmed} /><span>I understand the access requested by this extension.</span></label>
      <footer class="dialog-actions"><button class="button-secondary" type="button" onclick={() => void cancelReview()}>Cancel installation</button><button class="button-primary" type="button" disabled={!permissionConfirmed || permissions.some((item) => decisions[item.id] === undefined) || Boolean(action)} onclick={() => void approve()}>Approve and enable</button></footer>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <AlertDialog.Root bind:open={removeOpen}>
    <AlertDialog.Portal><AlertDialog.Overlay class="dialog-overlay" /><AlertDialog.Content class="confirm-dialog">
      <AlertDialog.Title>{removePackage ? `Uninstall ${removePackage.displayName}?` : `Remove ${removeRegistry?.name ?? "this registry"}?`}</AlertDialog.Title>
      <AlertDialog.Description>{removePackage ? "The package and runtime are removed. Encrypted Source accounts remain available for a later reinstall." : "You can add this registry URL again later."}</AlertDialog.Description>
      <footer><AlertDialog.Cancel class="button-secondary">Cancel</AlertDialog.Cancel><AlertDialog.Action class="button-danger" onclick={() => void remove()}>{removePackage ? "Uninstall" : "Remove registry"}</AlertDialog.Action></footer>
    </AlertDialog.Content></AlertDialog.Portal>
  </AlertDialog.Root>
{/if}
