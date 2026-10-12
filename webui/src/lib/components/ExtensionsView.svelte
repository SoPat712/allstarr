<script lang="ts">
  import { onMount } from "svelte";
  import { AlertDialog } from "$lib/components/ui/alert-dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { ChevronDown, CircleAlert, Database, Globe, KeyRound, MoreHorizontal, Plus, X } from "@lucide/svelte";
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
  import ExtensionSessionPanel from "$lib/components/ExtensionSessionPanel.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { availablePackages, currentPackages, extensionFailureReason, valueChanges } from "$lib/extensions";
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
  let dialogError = $state("");
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
  let confirmOpen = $state(false);

  const extensionDataUnavailable = "Some extension data is unavailable.";

  const installed = $derived(currentPackages(packages));
  const catalog = $derived(availablePackages(store, installed));
  const available = $derived(catalog
    .filter((item) => `${item.displayName} ${item.description ?? ""}`.toLowerCase().includes(search.toLowerCase())));
  const extensionTabs = $derived(tabs.map((id) => ({
    id,
    label: id === "registries" ? "Catalogs" : humanize(id),
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
    if (failed?.status === "rejected") feedback = extensionDataUnavailable;
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

  async function retryPackage(item: ExtensionPackage) {
    if (action || item.state.toLowerCase() !== "failed") return;
    action = `retry:${item.id}`;
    feedback = "";
    try {
      const retried = await extensions.retry(item);
      feedback = retried.permissionReviewRequired || retried.state.toLowerCase() === "reviewrequired"
        ? "Package staged. Review permissions before enabling."
        : "Package staged. Enable it when ready.";
      void refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The extension package could not be retried.";
    } finally {
      action = "";
    }
  }

  async function openReview(item: ExtensionPackage) {
    if (action) return;
    action = `review-load:${item.id}`;
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
    } finally {
      action = "";
    }
  }

  function permissionCopy(kind: string, value: string) {
    const normalized = kind.toLowerCase();
    if (normalized === "network") {
      let host = value;
      try { host = new URL(value).host; } catch { /* keep the raw value */ }
      return { icon: Globe, title: `Connect to ${host}`, detail: `Only this address: ${value}` };
    }
    if (normalized === "secret")
      return { icon: KeyRound, title: `Read the saved “${value}” setting`, detail: "Only this one encrypted account setting." };
    return { icon: Database, title: `Store “${value}” data in its own cache`, detail: "Uses a size-limited cache only this extension can see." };
  }

  function stateLabel(item: ExtensionPackage) {
    const state = item.state.toLowerCase();
    if (item.active) return "Running";
    if (item.permissionReviewRequired || state === "reviewrequired") return "Needs review";
    if (state === "failed") return "Failed";
    if (state === "staged") return "Ready to enable";
    if (state === "disabled") return "Turned off";
    return humanize(item.state);
  }

  function decideAll(approved: boolean) {
    decisions = Object.fromEntries(permissions.map((item) => [item.id, approved]));
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
        dialogError = "";
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

  function closeReview() {
    reviewPackage = null;
    reviewOpen = false;
  }

  function confirmRemoval(item: ExtensionPackage | ExtensionRegistry) {
    feedback = "";
    dialogError = "";
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
    dialogError = "";
    activatePackage = item;
    removePackage = null;
    removeRegistry = null;
    reviewAccessPackage = null;
    confirmOpen = true;
  }

  function confirmPermissionReview(item: ExtensionPackage) {
    feedback = "";
    dialogError = "";
    activatePackage = null;
    removePackage = null;
    removeRegistry = null;
    reviewAccessPackage = item;
    confirmOpen = true;
  }

  async function confirm() {
    if (action) return;
    let operation: (() => Promise<unknown>) | null = null;
    let operationKey = "";
    let successMessage = "";
    let resetPackage: ExtensionPackage | null = null;
    if (reviewAccessPackage) {
      const target = reviewAccessPackage;
      operationKey = `review-access:${target.id}`;
      successMessage = "Permissions reset. Complete the review before re-enabling.";
      operation = async () => { resetPackage = await extensions.revokePermissions(target); };
    } else if (activatePackage) {
      const target = activatePackage;
      operationKey = `activate:${target.id}`;
      successMessage = "Extension enabled.";
      operation = () => extensions.activate(target);
    } else if (removePackage) {
      const target = removePackage;
      operationKey = `remove:${target.id}`;
      successMessage = "Extension uninstalled. Saved Source accounts were retained.";
      operation = () => extensions.uninstall(target);
    } else if (removeRegistry) {
      const target = removeRegistry;
      operationKey = `registry:${target.id}`;
      successMessage = "Extension registry removed.";
      operation = () => extensions.removeRegistry(target);
    }
    if (!operation) return;

    action = operationKey;
    dialogError = "";
    let succeeded = false;
    try {
      await operation();
      confirmOpen = false;
      reviewAccessPackage = null;
      activatePackage = null;
      removePackage = null;
      removeRegistry = null;
      feedback = successMessage;
      void refresh();
      succeeded = true;
    } catch (cause) {
      dialogError = cause instanceof Error ? cause.message : `${successMessage} failed.`;
    } finally {
      action = "";
    }
    if (succeeded && resetPackage) await openReview(resetPackage);
  }

  async function addRegistry(event: SubmitEvent) {
    event.preventDefault();
    const data = new FormData(event.currentTarget as HTMLFormElement);
    const saved = await run("registry:add", () => extensions.addRegistry(
      String(data.get("name") ?? ""), String(data.get("registryUrl") ?? ""),
    ), "Registry validated and added.");
    if (saved) (event.currentTarget as HTMLFormElement).reset();
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
    <header class="sources-heading extension-header">
      <div>
        <h2>Extensions</h2>
        <p>Add more music sources from SpotiFLAC-compatible catalogs. An extension can only use the access you approve.</p>
      </div>
      <div class="sources-heading-actions">
        <Button onclick={() => { installOpen = true; }}><Plus size={16} aria-hidden="true" />Install extension</Button>
      </div>
    </header>
    <SegmentedNav
      items={extensionTabs}
      active={tab}
      label="Extension views"
      class="route-tabs extension-tabs"
      onchange={(id) => { tab = id as typeof tab; }}
    />
    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

    {#if tab === "installed"}
      <ul class="source-list extension-list" aria-label="Installed extensions">
        {#each installed as item}
          {@const update = updateFor(item)}
          <li class="extension-row" data-state={item.state.toLowerCase()}>
            <ProviderArtwork id={item.extensionId} definition={definition(item)} />
            <div class="extension-copy">
              <span class="extension-title"><strong>{item.displayName}</strong><small>Version {item.version}{item.author ? ` · ${item.author}` : ""}</small></span>
              <p>{item.description || "No description from the publisher."}</p>
              {#if item.state.toLowerCase() === "failed"}<p class="extension-failure" role="status"><CircleAlert size={14} aria-hidden="true" />{extensionFailureReason(item.failureCode)}</p>{/if}
              <div class="source-row-capabilities">{#each item.capabilities ?? [] as capability}<span>{humanize(capability)}</span>{/each}</div>
            </div>
            <Badge state={item.active ? "healthy" : item.state.toLowerCase() === "failed" ? "degraded" : "suggested"}>{stateLabel(item)}</Badge>
            <div class="extension-actions">
              {#if update}<Button disabled={Boolean(action)} onclick={() => void stage(update)}>{action === `install:${update.id}` ? "Verifying…" : `Update ${item.version} → ${update.version}`}</Button>{/if}
              {#if item.permissionReviewRequired}<Button disabled={Boolean(action)} onclick={() => void openReview(item)}>{action === `review-load:${item.id}` ? "Loading review…" : "Review permissions"}</Button>
              {:else if item.state.toLowerCase() === "failed"}<Button disabled={Boolean(action)} onclick={() => void retryPackage(item)}>{action === `retry:${item.id}` ? "Retrying…" : "Retry"}</Button>
              {:else if item.state.toLowerCase() === "staged"}<Button disabled={Boolean(action)} onclick={() => confirmActivation(item)}>Enable</Button>
              {:else if item.state.toLowerCase() === "disabled" && item.hasPermissions}<Button disabled={Boolean(action)} onclick={() => confirmPermissionReview(item)}>Review and enable</Button>
              {:else if item.state.toLowerCase() === "disabled"}<Button disabled={Boolean(action)} onclick={() => confirmActivation(item)}>Enable</Button>
              {:else if item.active}<Button variant="secondary" href={`#/integrations/services?source=${encodeURIComponent(item.extensionId)}&section=configuration`}>Configure Service</Button>
              {/if}
              <DropdownMenu.Root>
                <DropdownMenu.Trigger class="icon-button" aria-label="Manage extension" title={`More actions for ${item.displayName}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                <DropdownMenu.Portal>
                  <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                    {#if item.active}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void run(item.id, () => extensions.disable(item), "Extension disabled.")}>Disable</DropdownMenu.Item>{/if}
                    {#if item.hasPermissions && item.state.toLowerCase() === "active"}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => confirmPermissionReview(item)}>Review access</DropdownMenu.Item>{/if}
                    {#if item.hasPermissions && item.state.toLowerCase() === "disabled"}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => confirmPermissionReview(item)}>Review and enable</DropdownMenu.Item>{/if}
                    {#if item.state.toLowerCase() === "failed"}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void retryPackage(item)}>Retry</DropdownMenu.Item>{/if}
                    <DropdownMenu.Separator />
                    <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action)} onSelect={() => confirmRemoval(item)}>Uninstall</DropdownMenu.Item>
                  </DropdownMenu.Content>
                </DropdownMenu.Portal>
              </DropdownMenu.Root>
            </div>
            {#if item.active && item.usesSignedSession}<ExtensionSessionPanel {item} onchanged={refresh} />{/if}
          </li>
        {:else}
          <li class="compact-empty"><strong>No extensions installed</strong><p>Browse Available to install one from a catalog, or install a package from a URL.</p></li>
        {/each}
      </ul>
    {:else if tab === "available"}
      <section class="extension-catalog">
        <header><SearchField class="extension-search" label="Search extensions" placeholder="Search extensions" hiddenLabel bind:value={search} /></header>
        {#each storeErrors as item}<p class="notice-error">{item.repository}: {item.message}</p>{/each}
        <ul class="source-list" aria-label="Available extensions">
          {#each available as item}
            {@const installedVersion = installed.find((entry) => entry.extensionId.toLowerCase() === item.id.toLowerCase())?.version}
            <li class="extension-row">
              <ProviderArtwork id={item.id} definition={definition(item)} />
              <div class="extension-copy">
                <span class="extension-title"><strong>{item.displayName}</strong><small>Version {item.version}{item.author ? ` · ${item.author}` : ""}</small></span>
                <p>{item.description || "No description from the publisher."}</p>
              </div>
              <span></span>
              <div class="extension-actions">
                <Button
                  disabled={!item.sha256 || Boolean(action)}
                  title={!item.sha256 ? "This registry did not publish the SHA-256 checksum required for a safe install." : undefined}
                  aria-label={!item.sha256 ? `Cannot install ${item.displayName}: checksum unavailable` : undefined}
                  onclick={() => void stage(item)}
                >{!item.sha256 ? "Checksum unavailable" : action === `install:${item.id}` ? "Verifying…" : installedVersion ? `Update ${installedVersion} → ${item.version}` : "Install"}</Button>
              </div>
            </li>
          {:else}<li class="compact-empty"><strong>Nothing new to install</strong><p>Everything from your catalogs is installed and up to date.</p></li>{/each}
        </ul>
      </section>
    {:else if tab === "registries"}
      <div class="extension-registry-layout">
        <form class="settings-card extension-registry-form" onsubmit={(event) => void addRegistry(event)}>
          <header><div><h3>Add a catalog</h3><small>Allstarr checks the address before saving it.</small></div></header>
          <label class="settings-row"><div><strong>Name</strong></div><input name="name" required maxlength="200" placeholder="Community catalog" /></label>
          <label class="settings-row"><div><strong>Registry JSON URL</strong></div><input name="registryUrl" type="url" required pattern="https://.*" placeholder="https://example.org/registry.json" /></label>
          <footer><Button type="submit" disabled={Boolean(action)}>{action === "registry:add" ? "Validating…" : "Validate and add"}</Button></footer>
        </form>
        <ul class="source-list extension-registry-list" aria-label="Catalogs">
          {#each registries as item}
            {@const usedBy = dependencies(item)}
            <li class="extension-row">
              <span class="extension-registry-icon" aria-hidden="true"><Globe size={18} /></span>
              <div class="extension-copy"><span class="extension-title"><strong>{item.name}</strong></span><p>{item.registryUrl}</p>{#if usedBy.length}<p class="extension-failure">Remove {usedBy.length} installed package version{usedBy.length === 1 ? "" : "s"} from this catalog first.</p>{/if}</div>
              <Badge state={item.enabled ? "healthy" : "suggested"}>{item.enabled ? "On" : "Off"}</Badge>
              <div class="extension-actions"><Button variant="secondary" size="sm" disabled={Boolean(action)} onclick={() => void run(`registry:${item.id}`, () => extensions.setRegistryEnabled(item, !item.enabled), `Registry ${item.enabled ? "disabled" : "enabled"}.`)}>{action === `registry:${item.id}` ? "Saving…" : item.enabled ? "Disable" : "Enable"}</Button><Button variant="destructive" size="sm" disabled={usedBy.length > 0 || Boolean(action)} onclick={() => confirmRemoval(item)}>Remove</Button></div>
            </li>
          {:else}<li class="compact-empty"><strong>No catalogs connected</strong><p>Add a catalog's registry address to browse its extensions.</p></li>{/each}
        </ul>
      </div>
    {:else}
      <section class="extension-activity">
        <ul class="source-list" aria-label="Extension activity">
          {#each logs as item}
            <li><details><summary><span class={`activity-dot level-${item.level.toLowerCase()}`}></span><span><strong>{item.summary}</strong><small>{item.extensionId || "Extension runtime"}</small></span><time>{new Date(item.createdAt).toLocaleString()}</time><ChevronDown class="disclosure-chevron" size={16} aria-hidden="true" /></summary><p>{item.message || "No additional details were recorded."}</p></details></li>
          {:else}<li class="compact-empty"><strong>No extension activity yet</strong><p>Installs, permission changes and runtime events show up here.</p></li>{/each}
        </ul>
      </section>
    {/if}
  </section>

  <Dialog.Root bind:open={installOpen}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-install-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Install extension</Dialog.Title><Dialog.Description>Install a package from an HTTPS address. Allstarr checks it against the SHA-256 checksum before anything runs.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close installer"><X size={18} aria-hidden="true" /></Dialog.Close></header>
      <form class="settings-fields extension-install-form" onsubmit={(event) => void stageDirect(event)}>
        <label class="form-field"><span>Package URL</span><input name="downloadUrl" type="url" required pattern="https://.*" autocomplete="off" placeholder="https://example.org/extension.zip" /></label>
        <label class="form-field"><span>SHA-256 checksum</span><input name="sha256" required minlength="64" maxlength="64" pattern="[A-Fa-f0-9]{64}" autocomplete="off" spellcheck="false" placeholder="64 hexadecimal characters" /><small>The publisher lists this next to the download.</small></label>
        <label class="form-field"><span>Catalog</span><SelectField name="registryId" label="Registry attribution" value="" options={[{ value: "", label: "None (direct package)" }, ...registries.filter((item) => item.enabled).map((item) => ({ value: item.id, label: item.name }))]} /></label>
        <footer class="dialog-actions"><Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close><Button type="submit" disabled={Boolean(action)}>{action === "install:" ? "Verifying…" : "Verify package"}</Button></footer>
      </form>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <Dialog.Root bind:open={reviewOpen}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-review-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Review permissions</Dialog.Title><Dialog.Description>{reviewPackage?.displayName} {reviewPackage?.version} needs your approval before it can run.</Dialog.Description></div></header>
      <div class="extension-review-body">
        {#if previousPackage}
          <div class="extension-review-update">
            <strong>Update from {previousPackage.version} to {reviewPackage?.version}</strong>
            <div class="source-capabilities" aria-label={`Capability changes from ${previousPackage.version} to ${reviewPackage?.version}`}>
              {#each capabilityChanges as item}<span class:ready={item.change === "added"} class:removed={item.change === "removed"}>{item.change === "added" ? "Adds " : item.change === "removed" ? "Drops " : ""}{humanize(item.value)}</span>{/each}
            </div>
          </div>
        {/if}
        {#if permissions.length}
          <div class="permission-bulk">
            <strong>This extension asks to</strong>
            <div><Button size="sm" variant="ghost" onclick={() => decideAll(true)}>Allow all</Button><Button size="sm" variant="ghost" onclick={() => decideAll(false)}>Deny all</Button></div>
          </div>
        {/if}
        <ul class="extension-permissions">
          {#each permissions as item}
            {@const copy = permissionCopy(item.permissionKind, item.permissionValue)}
            {@const Icon = copy.icon}
            {@const change = permissionChanges.find((entry) => entry.value === `${item.permissionKind}:${item.permissionValue}`)?.change}
            <li>
              <span class="permission-icon" aria-hidden="true"><Icon size={18} /></span>
              <span class="permission-copy">
                <strong>{copy.title}</strong>
                <small>{copy.detail}</small>
                <span class="permission-tags">
                  {#if change === "added" && previousPackage}<em class="new">New access</em>{/if}
                  {#if item.required}<em>Required</em>{/if}
                </span>
              </span>
              <div class="permission-decision" role="group" aria-label={`Decision for ${item.permissionKind}`}>
                <button type="button" aria-pressed={decisions[item.id] === true} onclick={() => { decisions = { ...decisions, [item.id]: true }; }}>Allow</button>
                <button type="button" class="deny" aria-pressed={decisions[item.id] === false} onclick={() => { decisions = { ...decisions, [item.id]: false }; }}>Deny</button>
              </div>
            </li>
          {/each}
          {#each permissionChanges.filter((item) => item.change === "removed") as item}
            {@const [kind, ...rest] = item.value.split(":")}
            {@const copy = permissionCopy(kind, rest.join(":"))}
            <li class="removed">
              <span class="permission-icon" aria-hidden="true"><X size={18} /></span>
              <span class="permission-copy"><strong>Removed access</strong><small>No longer asks to {copy.title.charAt(0).toLowerCase() + copy.title.slice(1)}</small></span>
            </li>
          {/each}
        </ul>
      </div>
      <div class="extension-review-footer">
        <label class="permission-confirm"><Checkbox bind:checked={permissionConfirmed} /><span>I understand what this extension can access.</span></label>
        <footer class="dialog-actions"><Button variant="secondary" disabled={Boolean(action)} onclick={closeReview}>Close review</Button><Button disabled={!permissionConfirmed || permissions.some((item) => decisions[item.id] === undefined) || Boolean(action)} onclick={() => void approve()}>{action.startsWith("review:") ? "Saving…" : "Save review"}</Button></footer>
      </div>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <AlertDialog.Root bind:open={confirmOpen}>
    <AlertDialog.Portal><AlertDialog.Overlay class="dialog-overlay" /><AlertDialog.Content class="confirm-dialog">
      <AlertDialog.Title>{reviewAccessPackage ? reviewAccessPackage.state.toLowerCase() === "disabled" ? `Review and enable ${reviewAccessPackage.displayName}?` : `Review access for ${reviewAccessPackage.displayName}?` : activatePackage ? `Activate ${activatePackage.displayName}?` : removePackage ? `Uninstall ${removePackage.displayName}?` : `Remove ${removeRegistry?.name ?? "this registry"}?`}</AlertDialog.Title>
      <AlertDialog.Description>{reviewAccessPackage ? reviewAccessPackage.active ? "The extension runtime will stop now and remain disabled until you save a fresh permission review and reactivate it." : "The extension will remain disabled until you save a fresh permission review and reactivate it." : activatePackage ? "The reviewed runtime will start with only the approved capabilities and permissions." : removePackage ? "The package and runtime are removed. Encrypted Source accounts remain available for a later reinstall." : "You can add this registry URL again later."}</AlertDialog.Description>
      {#if dialogError}<p class="notice-error" role="alert">{dialogError}</p>{/if}
      <footer class="dialog-actions"><AlertDialog.Cancel class={buttonVariants({ variant: "secondary" })} disabled={Boolean(action)}>Cancel</AlertDialog.Cancel><Button variant={activatePackage || reviewAccessPackage ? "default" : "destructive"} disabled={Boolean(action)} onclick={() => void confirm()}>{action ? reviewAccessPackage ? "Stopping…" : activatePackage ? "Activating…" : "Removing…" : reviewAccessPackage ? "Stop and review" : activatePackage ? "Activate extension" : removePackage ? "Uninstall" : "Remove registry"}</Button></footer>
    </AlertDialog.Content></AlertDialog.Portal>
  </AlertDialog.Root>
{/if}
