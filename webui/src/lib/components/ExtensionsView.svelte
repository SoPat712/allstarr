<script lang="ts">
  import { onMount } from "svelte";
  import { AlertDialog } from "$lib/components/ui/alert-dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { X } from "lucide-svelte";
  import {
    extensions,
    type ExtensionLog,
    type ExtensionPackage,
    type ExtensionPermission,
    type ExtensionRegistry,
    type ExtensionStoreItem,
  } from "$lib/api";
  import { Badge } from "$lib/components/ui/badge";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
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
  let activatePackage = $state<ExtensionPackage | null>(null);
  let removePackage = $state<ExtensionPackage | null>(null);
  let removeRegistry = $state<ExtensionRegistry | null>(null);
  let reviewAccessPackage = $state<ExtensionPackage | null>(null);
  let reviewingExisting = $state(false);
  let confirmOpen = $state(false);

  const installed = $derived(currentPackages(packages));
  const catalog = $derived(availablePackages(store, installed));
  const available = $derived(catalog
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

  function updateFor(item: ExtensionPackage) {
    return catalog.find((entry) =>
      entry.id.toLowerCase() === item.extensionId.toLowerCase());
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
    if (action) return false;
    action = key;
    try {
      await operation();
      feedback = message;
      await refresh();
      return true;
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : `${message} failed.`;
      return false;
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

  async function openReview(item: ExtensionPackage, existing = false) {
    if (action) return;
    action = `review-load:${item.id}`;
    try {
      previousPackage = packages.find((entry) => entry.id === item.previousPackageId) ?? null;
      [permissions, previousPermissions] = await Promise.all([
        extensions.permissions(item.id),
        previousPackage ? extensions.permissions(previousPackage.id) : Promise.resolve([]),
      ]);
      reviewPackage = item;
      reviewingExisting = existing;
      decisions = {};
      permissionConfirmed = false;
      reviewOpen = true;
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Permissions could not be loaded.";
    } finally {
      action = "";
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
      reviewPackage = null;
      reviewOpen = false;
      if (reviewed.state.toLowerCase() === "staged") {
        activatePackage = reviewed;
        feedback = "";
        confirmOpen = true;
      } else {
        feedback = "Extension permissions saved.";
        await refresh();
      }
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Permission review failed.";
    } finally {
      action = "";
    }
  }

  async function cancelReview() {
    if (!reviewPackage) return;
    if (reviewingExisting) {
      reviewPackage = null;
      reviewOpen = false;
      return;
    }
    await run(`cancel:${reviewPackage.id}`, () => extensions.cancelStaging(reviewPackage!), "Extension installation cancelled.");
    reviewPackage = null;
    reviewOpen = false;
  }

  function confirmRemoval(item: ExtensionPackage | ExtensionRegistry) {
    feedback = "";
    activatePackage = null;
    reviewAccessPackage = null;
    if ("extensionId" in item) {
      removePackage = item;
      removeRegistry = null;
    } else {
      removeRegistry = item;
      removePackage = null;
    }
    confirmOpen = true;
  }

  function confirmActivation(item: ExtensionPackage) {
    feedback = "";
    activatePackage = item;
    removePackage = null;
    removeRegistry = null;
    reviewAccessPackage = null;
    confirmOpen = true;
  }

  function confirmPermissionReview(item: ExtensionPackage) {
    feedback = "";
    activatePackage = null;
    removePackage = null;
    removeRegistry = null;
    reviewAccessPackage = item;
    confirmOpen = true;
  }

  async function confirm() {
    let succeeded = false;
    let resetPackage: ExtensionPackage | null = null;
    if (reviewAccessPackage)
      succeeded = await run(`review-access:${reviewAccessPackage.id}`, async () => {
        resetPackage = await extensions.revokePermissions(reviewAccessPackage!);
      }, "Permissions reset. Complete the review before re-enabling.");
    else if (activatePackage)
      succeeded = await run(`activate:${activatePackage.id}`, () => extensions.activate(activatePackage!), "Extension enabled.");
    else if (removePackage)
      succeeded = await run(`remove:${removePackage.id}`, () => extensions.uninstall(removePackage!), "Extension uninstalled. Saved Source accounts were retained.");
    else if (removeRegistry)
      succeeded = await run(`registry:${removeRegistry.id}`, () => extensions.removeRegistry(removeRegistry!), "Extension registry removed.");
    if (!succeeded) return;
    confirmOpen = false;
    reviewAccessPackage = null;
    activatePackage = null;
    removePackage = null;
    removeRegistry = null;
    if (resetPackage) await openReview(resetPackage, true);
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
  <Skeleton class="panel skeleton-panel extension-skeleton" aria-label="Loading extensions" aria-busy="true" />
{:else}
  <section class="extension-workspace">
    <header class="extension-header">
      <div><strong>Extension manager</strong><small>Verified packages, explicit permissions, and reversible lifecycle actions.</small></div>
      <Button onclick={() => { installOpen = true; }}>Install extension</Button>
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
          {@const update = updateFor(item)}
          <article class="panel extension-row">
            <ProviderArtwork id={item.extensionId} definition={definition(item)} />
            <div class="extension-copy"><span><strong>{item.displayName}</strong><small>v{item.version}{item.author ? ` · ${item.author}` : ""}</small></span><p>{item.description || "No description supplied."}</p><div>{#each item.capabilities ?? [] as capability}<Badge>{humanize(capability)}</Badge>{/each}</div></div>
            <Badge state={item.active ? "healthy" : item.state === "failed" ? "degraded" : "suggested"}>{humanize(item.state)}</Badge>
            <div class="extension-actions">
              {#if item.active}<Button variant="secondary" href={`#/sources?source=${encodeURIComponent(item.extensionId)}&section=configuration`}>Configure Source</Button>{/if}
              {#if update}<Button disabled={Boolean(action)} onclick={() => void stage(update)}>{action === `install:${update.id}` ? "Verifying…" : `Update ${item.version} → ${update.version}`}</Button>{/if}
              {#if item.permissionReviewRequired}<Button disabled={Boolean(action)} onclick={() => void openReview(item)}>{action === `review-load:${item.id}` ? "Loading review…" : "Review permissions"}</Button>
              {:else if ["staged", "disabled"].includes(item.state.toLowerCase())}<Button disabled={Boolean(action)} onclick={() => confirmActivation(item)}>Enable</Button>
              {/if}
              <DropdownMenu.Root>
                <DropdownMenu.Trigger class={buttonVariants({ variant: "secondary" })}>Manage extension</DropdownMenu.Trigger>
                <DropdownMenu.Portal>
                  <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                    {#if item.active}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void run(item.id, () => extensions.disable(item), "Extension disabled.")}>Disable</DropdownMenu.Item>{/if}
                    {#if item.active && item.previousPackageId}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void run(item.id, () => extensions.rollback(item), "Previous extension version restored.")}>Rollback</DropdownMenu.Item>{/if}
                    {#if item.hasPermissions && ["active", "disabled"].includes(item.state.toLowerCase())}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => confirmPermissionReview(item)}>Review access</DropdownMenu.Item>{/if}
                    <DropdownMenu.Separator />
                    <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action)} onSelect={() => confirmRemoval(item)}>Uninstall</DropdownMenu.Item>
                  </DropdownMenu.Content>
                </DropdownMenu.Portal>
              </DropdownMenu.Root>
            </div>
          </article>
        {:else}
          <div class="panel compact-empty"><strong>No extensions installed</strong><p>Install from a connected registry or a verified package URL.</p></div>
        {/each}
      </div>
    {:else if tab === "available"}
      <section class="panel extension-catalog">
        <header><div><strong>Available packages</strong><small>Updates appear beside new extensions.</small></div><SearchField class="extension-search" label="Search extensions" placeholder="Search extensions…" hiddenLabel bind:value={search} /></header>
        {#each storeErrors as item}<p class="error-text">{item.repository}: {item.message}</p>{/each}
        <div>
          {#each available as item}
            {@const installedVersion = installed.find((entry) => entry.extensionId.toLowerCase() === item.id.toLowerCase())?.version}
            <article>
              <ProviderArtwork id={item.id} definition={definition(item)} />
              <span><strong>{item.displayName}</strong><small>v{item.version}{item.author ? ` · ${item.author}` : ""}</small><p>{item.description || "No description supplied."}</p></span>
              <Button disabled={!item.sha256 || Boolean(action)} onclick={() => void stage(item)}>{action === `install:${item.id}` ? "Verifying…" : installedVersion ? `Update ${installedVersion} → ${item.version}` : "Install"}</Button>
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
          <Button type="submit" disabled={Boolean(action)}>{action === "registry:add" ? "Validating…" : "Validate and add"}</Button>
        </form>
        <div class="extension-registry-list">
          {#each registries as item}
            {@const usedBy = dependencies(item)}
            <article class="panel">
              <span><strong>{item.name}</strong><small>{item.registryUrl}</small>{#if usedBy.length}<em>{usedBy.length} installed package version{usedBy.length === 1 ? "" : "s"} must be removed first.</em>{/if}</span>
              <Badge state={item.enabled ? "healthy" : "suggested"}>{item.enabled ? "Enabled" : "Disabled"}</Badge>
              <div><Button variant="secondary" size="sm" disabled={Boolean(action)} onclick={() => void run(`registry:${item.id}`, () => extensions.setRegistryEnabled(item, !item.enabled), `Registry ${item.enabled ? "disabled" : "enabled"}.`)}>{action === `registry:${item.id}` ? "Saving…" : item.enabled ? "Disable" : "Enable"}</Button><Button variant="destructive" size="sm" disabled={usedBy.length > 0 || Boolean(action)} onclick={() => confirmRemoval(item)}>Remove</Button></div>
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
      <header class="dialog-heading"><div><Dialog.Title>Install extension</Dialog.Title><Dialog.Description>Use a registry package or provide a verified HTTPS package and checksum.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close installer"><X size={18} aria-hidden="true" /></Dialog.Close></header>
      <form class="settings-fields" onsubmit={(event) => void stageDirect(event)}>
        <label class="setting-field"><span><strong>Package URL</strong></span><input name="downloadUrl" type="url" required pattern="https://.*" autocomplete="off" /></label>
        <label class="setting-field"><span><strong>SHA-256 checksum</strong></span><input name="sha256" required minlength="64" maxlength="64" pattern="[A-Fa-f0-9]{64}" autocomplete="off" spellcheck="false" /></label>
        <label class="setting-field"><span><strong>Registry attribution</strong></span><SelectField name="registryId" label="Registry attribution" value="" options={[{ value: "", label: "Direct package" }, ...registries.filter((item) => item.enabled).map((item) => ({ value: item.id, label: item.name }))]} /></label>
        <footer><Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close><Button type="submit" disabled={Boolean(action)}>{action === "install:" ? "Verifying…" : "Verify package"}</Button></footer>
      </form>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <Dialog.Root bind:open={reviewOpen}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-review-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Review permissions</Dialog.Title><Dialog.Description>{reviewPackage?.displayName} needs explicit access before its runtime can start.</Dialog.Description></div></header>
      <div class="extension-review-body">
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
            <article><span><strong>{help[0]} {humanize(item.permissionKind)}</strong><small>{item.permissionValue} · {help[1]}</small><em>{change === "added" ? "New access" : previousPackage ? "Unchanged" : "New install"}{item.required ? " · Required" : ""}</em></span><div role="group" aria-label={`Decision for ${item.permissionKind}`}><Button variant={decisions[item.id] === true ? "default" : "outline"} onclick={() => { decisions = { ...decisions, [item.id]: true }; }}>Allow</Button><Button variant={decisions[item.id] === false ? "destructive" : "outline"} onclick={() => { decisions = { ...decisions, [item.id]: false }; }}>Deny</Button></div></article>
          {/each}
          {#each permissionChanges.filter((item) => item.change === "removed") as item}
            <article><span><strong>− Removed access</strong><small>{item.value.replace(":", " · ")}</small><em>No longer requested</em></span></article>
          {/each}
        </div>
        <label class="permission-confirm"><Checkbox bind:checked={permissionConfirmed} /><span>I understand the access requested by this extension.</span></label>
        <footer class="dialog-actions"><Button variant="secondary" disabled={Boolean(action)} onclick={() => void cancelReview()}>{reviewingExisting ? "Close review" : action.startsWith("cancel:") ? "Cancelling…" : "Cancel installation"}</Button><Button disabled={!permissionConfirmed || permissions.some((item) => decisions[item.id] === undefined) || Boolean(action)} onclick={() => void approve()}>{action.startsWith("review:") ? "Saving…" : "Save review"}</Button></footer>
      </div>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <AlertDialog.Root bind:open={confirmOpen}>
    <AlertDialog.Portal><AlertDialog.Overlay class="dialog-overlay" /><AlertDialog.Content class="confirm-dialog">
      <AlertDialog.Title>{reviewAccessPackage ? `Review access for ${reviewAccessPackage.displayName}?` : activatePackage ? `Activate ${activatePackage.displayName}?` : removePackage ? `Uninstall ${removePackage.displayName}?` : `Remove ${removeRegistry?.name ?? "this registry"}?`}</AlertDialog.Title>
      <AlertDialog.Description>{reviewAccessPackage ? reviewAccessPackage.active ? "The extension runtime will stop now and remain disabled until you save a fresh permission review and reactivate it." : "The extension will remain disabled until you save a fresh permission review and reactivate it." : activatePackage ? "The reviewed runtime will start with only the approved capabilities and permissions." : removePackage ? "The package and runtime are removed. Encrypted Source accounts remain available for a later reinstall." : "You can add this registry URL again later."}</AlertDialog.Description>
      {#if feedback}<p class="error-text" role="alert">{feedback}</p>{/if}
      <footer><AlertDialog.Cancel class={buttonVariants({ variant: "secondary" })} disabled={Boolean(action)}>Cancel</AlertDialog.Cancel><Button variant={activatePackage || reviewAccessPackage ? "default" : "destructive"} disabled={Boolean(action)} onclick={() => void confirm()}>{action ? reviewAccessPackage ? "Stopping…" : activatePackage ? "Activating…" : "Removing…" : reviewAccessPackage ? "Stop and review" : activatePackage ? "Activate extension" : removePackage ? "Uninstall" : "Remove registry"}</Button></footer>
    </AlertDialog.Content></AlertDialog.Portal>
  </AlertDialog.Root>
{/if}
