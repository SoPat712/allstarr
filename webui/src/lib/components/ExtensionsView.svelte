<script lang="ts">
  import { toast } from "$lib/toasts";
  import { goto } from "$app/navigation";
  import { onMount } from "svelte";
  import { AlertDialog } from "$lib/components/ui/alert-dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Progress } from "$lib/components/ui/progress";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Check, CircleAlert, Database, Globe, KeyRound, MoreHorizontal, X } from "@lucide/svelte";
  import {
    extensions,
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
  import { humanize, sourceDescription } from "$lib/sources";

  let {
    view = "attention",
    extensionId = "",
    open = $bindable(false),
    onchanged,
  }: {
    view?: "attention" | "catalog" | "package" | "remove";
    extensionId?: string;
    open?: boolean;
    onchanged?: () => void | Promise<void>;
  } = $props();

  let catalogTab = $state<"catalog" | "url" | "catalogs">("catalog");
  let catalogDialogOpen = $state(true);
  let packages = $state<ExtensionPackage[]>([]);
  let registries = $state<ExtensionRegistry[]>([]);
  let store = $state<ExtensionStoreItem[]>([]);
  let storeErrors = $state<Array<{ repository: string; message: string }>>([]);
  let dataUnavailable = $state(false);
  let loading = $state(true);
  let action = $state("");
  let dialogError = $state("");
  let catalogError = $state("");
  let reviewError = $state("");
  function reportActionError(message: string) {
    if (reviewOpen) reviewError = message;
    else if (view === "catalog" && open) catalogError = message;
    else toast.error(message);
  }
  let search = $state("");
  let reviewOpen = $state(false);
  let reviewPackage = $state<ExtensionPackage | null>(null);
  let reviewOrigin = $state("");
  let previousPackage = $state<ExtensionPackage | null>(null);
  let permissions = $state<ExtensionPermission[]>([]);
  let previousPermissions = $state<ExtensionPermission[]>([]);
  let decisions = $state<Record<string, boolean>>({});
  let permissionConfirmed = $state(false);
  let activatePackage = $state<ExtensionPackage | null>(null);
  let activationPermissions = $state<ExtensionPermission[]>([]);
  let activationAccessLoading = $state(false);
  let activationAccessReady = $state(true);
  let activationAccessRequest = 0;
  let removePackage = $state<ExtensionPackage | null>(null);
  let removeRegistry = $state<ExtensionRegistry | null>(null);
  let reviewAccessPackage = $state<ExtensionPackage | null>(null);
  let confirmOpen = $state(false);
  type BulkEntry = {
    item: ExtensionPackage; previous: ExtensionPackage | null;
    permissions: ExtensionPermission[]; previousPermissions: ExtensionPermission[]; approve: boolean;
  };
  let bulkOpen = $state(false), bulkEntries = $state<BulkEntry[]>([]), bulkProgress = $state("");
  let bulkErrors = $state<string[]>([]), bulkConfirmed = $state(false);

  const extensionDataUnavailable = "Some extension data is unavailable.";

  const installed = $derived(currentPackages(packages));
  const catalog = $derived(availablePackages(store, installed));
  const available = $derived(catalog
    .filter((item) => `${item.displayName} ${item.description ?? ""}`.toLowerCase().includes(search.toLowerCase())));
  const attention = $derived(installed.filter((item) =>
    item.permissionReviewRequired ||
    ["failed", "staged", "disabled", "reviewrequired"].includes(item.state.toLowerCase()) ||
    Boolean(updateFor(item))));
  const focused = $derived(installed.find((item) => item.extensionId.toLowerCase() === extensionId.toLowerCase()));
  const updatable = $derived(installed.filter((item) => updateFor(item)));
  const reviewable = $derived(installed.filter((item) => !updateFor(item) &&
    (item.permissionReviewRequired || ["reviewrequired", "staged"].includes(item.state.toLowerCase()))));
  const bulkTargets = $derived(updatable.length + reviewable.length);
  const bulkChosen = $derived(bulkEntries.filter((entry) => entry.approve).length);
  const catalogTabs = $derived([
    { id: "catalog", label: "Catalog", count: catalog.length },
    { id: "url", label: "From a link" },
    { id: "catalogs", label: "Manage catalogs", count: registries.length },
  ]);

  const permissionChanges = $derived(valueChanges(
    permissions.map((item) => `${item.permissionKind}:${item.permissionValue}`),
    previousPermissions.map((item) => `${item.permissionKind}:${item.permissionValue}`),
  ));
  const capabilityChanges = $derived(valueChanges(
    reviewPackage?.capabilities ?? [],
    previousPackage?.capabilities ?? [],
  ));
  const reviewNeedsPermissions = $derived(Boolean(reviewPackage?.permissionReviewRequired ||
    reviewPackage?.state.toLowerCase() === "reviewrequired"));

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
      extensions.packages(),
      view === "catalog" ? extensions.registries() : Promise.resolve(registries),
      extensions.store(),
    ]);
    if (results[0].status === "fulfilled") packages = results[0].value;
    if (results[1].status === "fulfilled") registries = results[1].value;
    if (results[2].status === "fulfilled") {
      store = results[2].value.items;
      storeErrors = results[2].value.errors;
    }
    const failed = results.find((result) => result.status === "rejected");
    dataUnavailable = failed?.status === "rejected";
    loading = false;
  }

  async function changed() {
    await refresh();
    await onchanged?.();
  }

  async function run(key: string, operation: () => Promise<unknown>, message: string) {
    if (action) return false;
    action = key;
    catalogError = ""; reviewError = "";
    try {
      await operation();
      toast.success(message);
      await changed();
      return true;
    } catch (cause) {
      reportActionError(cause instanceof Error ? cause.message : `${message} failed.`);
      return false;
    } finally {
      action = "";
    }
  }

  async function stage(item: ExtensionStoreItem) {
    if (action) return;
    action = `install:${item.id}`;
    catalogError = "";
    let staged: ExtensionPackage | undefined;
    try {
      const result = await extensions.install(item);
      await changed();
      staged = packages.find((entry) => entry.id === result.packageId);
      toast.success("Extension downloaded and verified. Review its access before enabling.");
    } catch (cause) {
      reportActionError(cause instanceof Error ? cause.message : "Extension could not be staged.");
    } finally {
      action = "";
    }
    if (staged && !staged.active && (staged.permissionReviewRequired ||
      ["reviewrequired", "staged"].includes(staged.state.toLowerCase())))
      await openReview(staged, networkHost(item.downloadUrl));
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
    try {
      const retried = await extensions.retry(item);
      toast.success(retried.permissionReviewRequired || retried.state.toLowerCase() === "reviewrequired"
        ? "Package staged. Review permissions before enabling."
        : "Package staged. Enable it when ready.");
      void changed();
    } catch (cause) {
      reportActionError(cause instanceof Error ? cause.message : "The extension package could not be retried.");
    } finally {
      action = "";
    }
  }

  async function openReview(item: ExtensionPackage, origin = "") {
    if (action) return;
    action = `review-load:${item.id}`;
    try {
      previousPackage = packages.find((entry) => entry.id === item.previousPackageId) ?? null;
      [permissions, previousPermissions] = await Promise.all([
        extensions.permissions(item.id),
        previousPackage ? extensions.permissions(previousPackage.id) : Promise.resolve([]),
      ]);
      reviewPackage = item;
      reviewOrigin = origin || registryOrigin(item);
      decisions = item.permissionReviewRequired || item.state.toLowerCase() === "reviewrequired"
        ? {}
        : Object.fromEntries(permissions.map((permission) => [permission.id, permission.decision.toLowerCase() === "approved"]));
      permissionConfirmed = false;
      reviewError = "";
      reviewOpen = true;
      if (view === "catalog") catalogDialogOpen = false;
    } catch (cause) {
      reportActionError(cause instanceof Error ? cause.message : "Permissions could not be loaded.");
    } finally {
      action = "";
    }
  }

  function networkHost(value: string) {
    try { return new URL(value).host; } catch { return value; }
  }

  function registryOrigin(item: ExtensionPackage) {
    const registry = registries.find((entry) => entry.id === item.registryId);
    return registry ? `${registry.name} · ${networkHost(registry.registryUrl)}` : "";
  }

  function networkHosts(rows: ExtensionPermission[], approvedOnly = false) {
    return [...new Set(rows.filter((item) => item.permissionKind.toLowerCase() === "network" &&
      (!approvedOnly || item.decision.toLowerCase() === "approved"))
      .map((item) => networkHost(item.permissionValue)))];
  }

  function capabilityLabel(value: string) {
    const labels: Record<string, string> = {
      metadata: "Search and song details", streaming: "Streaming", download: "Downloads",
      downloads: "Downloads", playlist: "Playlists", playlists: "Playlists", lyrics: "Lyrics",
      scrobbling: "Listening history",
    };
    return labels[value.toLowerCase()] ?? humanize(value);
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
      const reviewed = reviewNeedsPermissions ? await extensions.review(reviewPackage, permissions.map((item) => ({
        kind: item.permissionKind,
        value: item.permissionValue,
        approved: decisions[item.id],
      }))) : reviewPackage;
      const reviewedPermissions = permissions.map((item) => ({
        ...item, decision: decisions[item.id] ? "approved" : "denied",
      }));
      reviewPackage = null;
      reviewOpen = false;
      if (reviewed.state.toLowerCase() === "staged") {
        await confirmActivation(reviewed, reviewedPermissions);
      } else {
        toast.success("Extension permissions saved.");
        await changed();
        if (view === "catalog") open = false;
      }
    } catch (cause) {
      reportActionError(cause instanceof Error ? cause.message : "Permission review failed.");
    } finally {
      action = "";
    }
  }

  const permissionKeys = (rows: ExtensionPermission[]) => rows.map((item) => `${item.permissionKind}:${item.permissionValue}`);

  // Updating many extensions one review at a time meant a dialog per extension. This fetches every update first, then
  // shows one review with only what each asks for beyond the version running now.
  async function startBulk() {
    if (action) return;
    action = "bulk";
    bulkOpen = true;
    bulkEntries = [];
    bulkErrors = [];
    bulkConfirmed = false;
    const targets = [...updatable.map((item) => ({ item, update: updateFor(item) ?? null })), ...reviewable.map((item) => ({ item, update: null }))];
    const waiting: string[] = [];
    try {
      for (const [index, target] of targets.entries()) {
        bulkProgress = `Getting ${index + 1} of ${targets.length}: ${target.item.displayName}`;
        if (!target.update) { waiting.push(target.item.id); continue; }
        try { waiting.push((await extensions.install(target.update)).packageId); }
        catch (cause) { bulkErrors = [...bulkErrors, `${target.item.displayName}: ${cause instanceof Error ? cause.message : "the update could not be fetched."}`]; }
      }
      bulkProgress = "Reading what each one asks for…";
      packages = await extensions.packages();
      bulkEntries = await Promise.all(waiting.map(async (id) => {
        const item = packages.find((entry) => entry.id === id);
        if (!item) return null;
        const previous = packages.find((entry) => entry.id === item.previousPackageId) ?? null;
        const [current, before] = await Promise.all([
          extensions.permissions(item.id),
          previous ? extensions.permissions(previous.id) : Promise.resolve([]),
        ]);
        // An update to an extension that was switched off stays unticked so approving does not turn it back on.
        return { item, previous, permissions: current, previousPermissions: before, approve: !previous || previous.active };
      })).then((rows) => rows.filter((row): row is BulkEntry => row !== null));
    } catch (cause) {
      bulkErrors = [...bulkErrors, cause instanceof Error ? cause.message : "The extensions could not be read."];
    } finally {
      bulkProgress = "";
      action = "";
    }
  }

  async function applyBulk() {
    if (action) return;
    action = "bulk-apply";
    const failures: string[] = [];
    let enabled = 0;
    try {
      for (const entry of bulkEntries.filter((item) => item.approve)) {
        bulkProgress = `Turning on ${entry.item.displayName}…`;
        try {
          let current = entry.item;
          if (current.permissionReviewRequired || current.state.toLowerCase() === "reviewrequired")
            current = await extensions.review(current, entry.permissions.map((item) => ({
              kind: item.permissionKind, value: item.permissionValue, approved: true,
            })));
          if (current.state.toLowerCase() === "staged") await extensions.activate(current);
          enabled++;
        } catch (cause) {
          failures.push(`${entry.item.displayName}: ${cause instanceof Error ? cause.message : "it could not be turned on."}`);
        }
      }
    } finally {
      bulkProgress = "";
      action = "";
    }
    if (enabled) toast.success(`${enabled} ${enabled === 1 ? "extension" : "extensions"} updated and turned on.`);
    bulkErrors = failures;
    if (!failures.length) bulkOpen = false;
    await changed();
  }

  function closeReview() {
    reviewPackage = null;
    reviewOpen = false;
    if (view === "catalog") open = false;
  }

  function confirmRemoval(item: ExtensionPackage | ExtensionRegistry) {
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

  async function confirmActivation(item: ExtensionPackage, rows?: ExtensionPermission[]) {
    const request = ++activationAccessRequest;
    dialogError = "";
    activatePackage = item;
    activationPermissions = rows ?? [];
    activationAccessReady = Boolean(rows || !item.hasPermissions);
    activationAccessLoading = !activationAccessReady;
    removePackage = null;
    removeRegistry = null;
    reviewAccessPackage = null;
    confirmOpen = true;
    if (activationAccessReady) return;
    try {
      const loaded = await extensions.permissions(item.id);
      if (request !== activationAccessRequest || activatePackage?.id !== item.id) return;
      activationPermissions = loaded;
      activationAccessReady = true;
    } catch (cause) {
      if (request === activationAccessRequest && activatePackage?.id === item.id)
        dialogError = cause instanceof Error ? cause.message : "Access could not be loaded. Close this dialog and try again.";
    } finally {
      if (request === activationAccessRequest && activatePackage?.id === item.id) activationAccessLoading = false;
    }
  }

  function confirmPermissionReview(item: ExtensionPackage) {
    dialogError = "";
    activatePackage = null;
    removePackage = null;
    removeRegistry = null;
    reviewAccessPackage = item;
    confirmOpen = true;
  }

  async function confirm() {
    if (action || (activatePackage && !activationAccessReady)) return;
    let operation: (() => Promise<unknown>) | null = null;
    let operationKey = "";
    let successMessage = "";
    let resetPackage: ExtensionPackage | null = null;
    let enabledPackage: ExtensionPackage | null = null;
    if (reviewAccessPackage) {
      const target = reviewAccessPackage;
      operationKey = `review-access:${target.id}`;
      successMessage = "Permissions reset. Complete the review before re-enabling.";
      operation = async () => { resetPackage = await extensions.revokePermissions(target); };
    } else if (activatePackage) {
      const target = activatePackage;
      operationKey = `activate:${target.id}`;
      enabledPackage = target;
      successMessage = `${target.displayName} ${target.previousPackageId ? "updated and enabled" : target.installed ? "enabled" : "installed and enabled"}.`;
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
      const source = enabledPackage;
      toast.success(successMessage, source ? {
        label: "Open source",
        run: () => goto(`#/sources/services/${encodeURIComponent(source.extensionId)}`, { keepFocus: true, noScroll: true }),
      } : undefined);
      void changed();
      if (view === "catalog" && source) open = false;
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

{#snippet installSteps(current: number)}
  <ol class="extension-setup-steps" aria-label="Extension setup">
    {#each ["Where from", "Review access", "Enable"] as label, index}
      <li aria-current={current === index ? "step" : undefined} class:complete={index < current}>
        <span class="extension-step-number" aria-hidden="true">{#if index < current}<Check size={14} />{:else}{index + 1}{/if}</span>
        <span>{label}</span>
      </li>
    {/each}
  </ol>
{/snippet}

{#snippet accessSummary(item: ExtensionPackage, rows: ExtensionPermission[], approvedOnly = false)}
  {@const hosts = networkHosts(rows, approvedOnly)}
  <div class="extension-access-overview">
    <section aria-label="Extension capabilities">
      <h3>What it can do</h3>
      <div class="source-capabilities">
        {#each item.capabilities ?? [] as capability}<span>{capabilityLabel(capability)}</span>
        {:else}<p>No capabilities listed.</p>{/each}
      </div>
    </section>
    <section aria-label="Extension network access">
      <h3>Where it connects</h3>
      {#if hosts.length}
        <ul class="extension-hosts" aria-label={approvedOnly ? "Approved network hosts" : "Requested network hosts"}>
          {#each hosts as host}<li>{host}</li>{/each}
        </ul>
        <p>{approvedOnly ? "Only the addresses you approved are allowed." : "Review the exact addresses below before allowing access."}</p>
      {:else}<p>{approvedOnly ? "No network access approved." : "No network access requested."}</p>{/if}
    </section>
  </div>
{/snippet}

{#snippet packageRow(item: ExtensionPackage, showOpen: boolean)}
  {@const update = updateFor(item)}
  <li class="extension-row" data-state={item.state.toLowerCase()}>
    <div class="extension-layout">
      <ProviderArtwork id={item.extensionId} definition={definition(item)} />
      <div class="extension-copy">
        <span class="extension-title"><strong>{item.displayName}</strong><small>Version {item.version}{item.author ? ` · ${item.author}` : ""}</small></span>
        <p>{item.description ? sourceDescription(item.description) : "No description from the publisher."}</p>
        {#if item.state.toLowerCase() === "failed"}<p class="extension-failure" role="status"><CircleAlert size={14} aria-hidden="true" />{extensionFailureReason(item.failureCode)}</p>{/if}
        <div class="source-row-capabilities">{#each item.capabilities ?? [] as capability}<span>{humanize(capability)}</span>{/each}</div>
      </div>
      <div class="extension-controls">
        <Badge state={item.active ? "healthy" : item.state.toLowerCase() === "failed" ? "degraded" : "suggested"}>{stateLabel(item)}</Badge>
        <div class="extension-actions">
          {#if update}<Button variant="outline" disabled={Boolean(action)} onclick={() => void stage(update)}>{action === `install:${update.id}` ? "Verifying…" : `Update ${item.version} → ${update.version}`}</Button>
          {:else if item.permissionReviewRequired}<Button variant="outline" disabled={Boolean(action)} onclick={() => void openReview(item)}>{action === `review-load:${item.id}` ? "Loading review…" : "Review permissions"}</Button>
          {:else if item.state.toLowerCase() === "failed"}<Button variant="outline" disabled={Boolean(action)} onclick={() => void retryPackage(item)}>{action === `retry:${item.id}` ? "Retrying…" : "Retry"}</Button>
          {:else if item.state.toLowerCase() === "staged"}<Button variant="outline" disabled={Boolean(action)} onclick={() => confirmActivation(item)}>Enable</Button>
          {:else if item.state.toLowerCase() === "disabled" && item.hasPermissions}<Button variant="outline" disabled={Boolean(action)} onclick={() => confirmPermissionReview(item)}>Review and enable</Button>
          {:else if item.state.toLowerCase() === "disabled"}<Button variant="outline" disabled={Boolean(action)} onclick={() => confirmActivation(item)}>Enable</Button>
          {:else if item.active && showOpen}<Button variant="secondary" href={`#/sources/services/${encodeURIComponent(item.extensionId)}`}>Open source</Button>
          {/if}
          <DropdownMenu.Root>
            <DropdownMenu.Trigger class="icon-button" aria-label="Manage extension" title={`More actions for ${item.displayName}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
            <DropdownMenu.Portal>
              <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                {#if item.permissionReviewRequired && update}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void openReview(item)}>Review permissions</DropdownMenu.Item>{/if}
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
      </div>
      {#if item.active && item.usesSignedSession}<ExtensionSessionPanel {item} onchanged={changed} />{/if}
    </div>
  </li>
{/snippet}

{#if view === "remove" && focused}
  {#if dataUnavailable}<p class="degraded-banner" role="status">{extensionDataUnavailable}</p>{/if}
  <Button variant="text" class="extension-remove-link" disabled={Boolean(action)} onclick={() => confirmRemoval(focused)}>Remove extension…</Button>
{/if}

{#if view === "catalog"}
  <Dialog.Root bind:open={() => open && catalogDialogOpen, (value) => { if (!action) { catalogDialogOpen = value; if (!value) open = false; } }}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-catalog-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Add an extension</Dialog.Title><Dialog.Description>Extensions add services Allstarr can search, stream or download from. Choose one, review its access, then enable it.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close add an extension" disabled={Boolean(action)}><X size={18} aria-hidden="true" /></Dialog.Close></header>
      {#if catalogTab !== "catalogs"}{@render installSteps(0)}{/if}
      <SegmentedNav variant="segments" items={catalogTabs} active={catalogTab} label="Ways to add an extension" class="route-tabs extension-tabs" onchange={(id) => { if (!action) catalogTab = id as typeof catalogTab; }} />
      <div class="extension-catalog-body">
        {#if catalogError}<p class="notice-error" role="alert">{catalogError}</p>{/if}

        {#if dataUnavailable}<p class="degraded-banner" role="status">{extensionDataUnavailable}</p>{/if}
        {#if loading}
          <p class="service-empty">Loading catalogs…</p>
        {:else if catalogTab === "catalog"}
          <section class="extension-catalog">
            <p class="extension-flow-note">The download is verified before Allstarr reads its capabilities and requested access. It stays off until you review and enable it.</p>
            <SearchField class="extension-search" label="Search extensions" placeholder="Search extensions" hiddenLabel bind:value={search} />
            {#each storeErrors as item}<p class="notice-error">{item.repository}: {item.message}</p>{/each}
            <ul class="source-list" aria-label="Available extensions">
              {#each available as item}
                {@const installedVersion = installed.find((entry) => entry.extensionId.toLowerCase() === item.id.toLowerCase())?.version}
                <li class="extension-row">
                  <ProviderArtwork id={item.id} definition={definition(item)} />
                  <div class="extension-copy">
                    <span class="extension-title"><strong>{item.displayName}</strong><small>Version {item.version}{item.author ? ` · ${item.author}` : ""}</small></span>
                    <p>{item.description ? sourceDescription(item.description) : "No description from the publisher."}</p>
                  </div>
                  <span></span>
                  <div class="extension-actions">
                    <Button variant="outline"
                      disabled={!item.sha256 || Boolean(action)}
                      title={!item.sha256 ? "This registry did not publish the SHA-256 checksum required for a safe install." : undefined}
                      aria-label={!item.sha256 ? `Cannot install ${item.displayName}: checksum unavailable` : undefined}
                      onclick={() => void stage(item)}
                    >{!item.sha256 ? "Checksum unavailable" : action === `install:${item.id}` ? "Downloading and verifying…" : installedVersion ? `Review update ${installedVersion} → ${item.version}` : "Download and review"}</Button>
                  </div>
                </li>
              {:else}<li class="compact-empty"><strong>Nothing new to install</strong><p>{registries.length ? "Everything from your catalogs is installed and up to date." : "Add a catalog under Manage catalogs to browse extensions."}</p></li>{/each}
            </ul>
          </section>
        {:else if catalogTab === "url"}
          <form id="extension-install-form" class="extension-install-form" onsubmit={(event) => void stageDirect(event)}>
            <p class="extension-flow-note">Use a direct HTTPS download link and the publisher's checksum. Allstarr must download and verify it before showing its access. Nothing runs yet.</p>
            <label class="form-field"><span>Download link</span><input name="downloadUrl" type="url" required pattern="https://.*" autocomplete="off" placeholder="https://example.org/extension.zip" disabled={Boolean(action)} /><small>A direct link to the extension archive.</small></label>
            <label class="form-field"><span>SHA-256 checksum</span><input name="sha256" required minlength="64" maxlength="64" pattern="[A-Fa-f0-9]{64}" autocomplete="off" spellcheck="false" placeholder="64 hexadecimal characters" disabled={Boolean(action)} /><small>The publisher lists this next to the download. It must contain 64 hexadecimal characters.</small></label>
            <label class="form-field"><span>Catalog</span><SelectField name="registryId" label="Registry attribution" value="" options={[{ value: "", label: "None (direct package)" }, ...registries.filter((item) => item.enabled).map((item) => ({ value: item.id, label: item.name }))]} /></label>
          </form>
        {:else}
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
                  <div class="extension-actions"><Button variant="secondary" size="sm" disabled={Boolean(action)} onclick={() => void run(`registry:${item.id}`, () => extensions.setRegistryEnabled(item, !item.enabled), `Registry ${item.enabled ? "disabled" : "enabled"}.`)}>{action === `registry:${item.id}` ? "Saving…" : item.enabled ? "Disable" : "Enable"}</Button><Button variant="text" class="extension-remove-link" size="sm" disabled={usedBy.length > 0 || Boolean(action)} onclick={() => confirmRemoval(item)}>Remove</Button></div>
                </li>
              {:else}<li class="compact-empty"><strong>No catalogs connected</strong><p>Add a catalog's registry address to browse its extensions.</p></li>{/each}
            </ul>
          </div>
        {/if}
      </div>
      <footer class="dialog-actions"><Dialog.Close class={buttonVariants({ variant: "secondary" })} disabled={Boolean(action)}>Cancel</Dialog.Close>{#if catalogTab === "url"}<Button type="submit" form="extension-install-form" disabled={Boolean(action)}>{action === "install:" ? "Downloading and verifying…" : "Download and review"}</Button>{/if}</footer>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>
{:else if loading}
  {#if view === "package"}<Skeleton class="panel skeleton-panel extension-skeleton" aria-label="Loading extension" aria-busy="true" />{/if}
{:else if view === "attention"}
  {#if attention.length || dataUnavailable}
    <section class="extension-attention" aria-labelledby="extension-attention-title">
      <div class="extension-attention-heading">
        <h3 id="extension-attention-title">Extensions that need you</h3>
        {#if bulkTargets > 1}<Button variant="outline" disabled={Boolean(action)} onclick={() => void startBulk()}>{action === "bulk" ? "Getting updates…" : `Update and review all (${bulkTargets})`}</Button>{/if}
      </div>
      {#if dataUnavailable}<p class="degraded-banner" role="status">{extensionDataUnavailable}</p>{/if}

      {#if attention.length}
        <ul class="source-list extension-list" aria-label="Extensions that need you">
          {#each attention as item (item.id)}{@render packageRow(item, true)}{/each}
        </ul>
      {/if}
    </section>
  {/if}
{:else if view === "package" && focused}
  <section class="settings-card service-extension" aria-labelledby="service-extension-title">
    {#if dataUnavailable}<p class="degraded-banner" role="status">{extensionDataUnavailable}</p>{/if}
    <header><div><h3 id="service-extension-title">Extension</h3><small>This source comes from an extension. Updates, permissions and sign-in live here.</small></div></header>

    <ul class="source-list extension-list extension-package" aria-label={`${focused.displayName} extension`}>
      {@render packageRow(focused, false)}
    </ul>
  </section>
{/if}

  <Dialog.Root bind:open={() => reviewOpen, (value) => { if (!action) { reviewOpen = value; if (!value && view === "catalog") open = false; } }}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-review-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Review permissions</Dialog.Title><Dialog.Description>The extension is downloaded and stays off while you review what it can do and where it can connect.</Dialog.Description></div><Dialog.Close class="icon-button" aria-label="Close permission review" disabled={Boolean(action)}><X size={18} aria-hidden="true" /></Dialog.Close></header>
      {@render installSteps(1)}
      <div class="extension-review-body">
        {#if reviewPackage}
          <div class="extension-review-summary">
            <ProviderArtwork id={reviewPackage.extensionId} definition={definition(reviewPackage)} />
            <div><strong>{reviewPackage.displayName}</strong><small>Version {reviewPackage.version}{reviewPackage.author ? ` · ${reviewPackage.author}` : ""}</small>{#if reviewOrigin}<small>From {reviewOrigin}</small>{/if}</div>
            <Badge state="suggested">Not running</Badge>
          </div>
          {@render accessSummary(reviewPackage, permissions)}
          {#if reviewPackage.usesSignedSession}<p class="extension-flow-note">After enabling, open the source to complete its sign-in step.</p>{/if}
        {/if}
        {#if previousPackage}
          <div class="extension-review-update">
            <strong>Update from {previousPackage.version} to {reviewPackage?.version}</strong>
            <div class="source-capabilities" aria-label={`Capability changes from ${previousPackage.version} to ${reviewPackage?.version}`}>
              {#each capabilityChanges as item}<span class:ready={item.change === "added"} class:removed={item.change === "removed"}>{item.change === "added" ? "Adds " : item.change === "removed" ? "Drops " : ""}{capabilityLabel(item.value)}</span>{/each}
            </div>
          </div>
        {/if}
        {#if permissions.length}
          <div class="permission-bulk">
            <strong>{reviewNeedsPermissions ? "Choose what it can access" : "Saved access"}</strong>
            {#if reviewNeedsPermissions}<div><Button size="sm" variant="outline" disabled={Boolean(action)} onclick={() => decideAll(true)}>Allow all</Button><Button size="sm" variant="outline" disabled={Boolean(action)} onclick={() => decideAll(false)}>Deny all</Button></div>{/if}
          </div>
        {/if}
        {#if permissions.length || permissionChanges.some((item) => item.change === "removed")}
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
              {#if reviewNeedsPermissions}<div class="permission-decision" role="group" aria-label={`Decision for ${item.permissionKind}`}>
                <button type="button" disabled={Boolean(action)} aria-pressed={decisions[item.id] === true} onclick={() => { decisions = { ...decisions, [item.id]: true }; }}>Allow</button>
                <button type="button" disabled={Boolean(action)} class="deny" aria-pressed={decisions[item.id] === false} onclick={() => { decisions = { ...decisions, [item.id]: false }; }}>Deny</button>
              </div>{:else}<Badge state="suggested">{decisions[item.id] ? "Allowed" : "Denied"}</Badge>{/if}
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
        {:else}<p class="extension-flow-note">This extension asks for no permissions.</p>{/if}
      </div>
      {#if reviewError}<p class="notice-error" role="alert">{reviewError}</p>{/if}
      <div class="extension-review-footer">
        <label class="permission-confirm"><Checkbox bind:checked={permissionConfirmed} /><span>I understand what this extension can access.</span></label>
        <p class="extension-review-hint">{reviewNeedsPermissions ? "Saving access does not start the extension. You will enable it in the next step." : "Its saved access is unchanged. Continue to choose when it starts."}</p>
        <footer class="dialog-actions"><Button variant="secondary" disabled={Boolean(action)} onclick={closeReview}>Close review</Button><Button disabled={!permissionConfirmed || permissions.some((item) => decisions[item.id] === undefined) || Boolean(action)} onclick={() => void approve()}>{action.startsWith("review:") ? "Approving…" : reviewNeedsPermissions ? "Approve" : "Continue to enable"}</Button></footer>
      </div>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <Dialog.Root bind:open={bulkOpen} onOpenChange={(value) => { if (!value) void changed(); }}>
    <Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog extension-review-dialog">
      <header class="dialog-heading"><div><Dialog.Title>Update and review extensions</Dialog.Title><Dialog.Description>Approving gives each ticked extension the access listed and turns it on. Unticked ones wait for their own review, where you can also deny single permissions.</Dialog.Description></div></header>
      <div class="extension-review-body">
        {#if bulkProgress}<div class="bulk-progress" role="status"><p>{bulkProgress}</p><Progress value={null} aria-label="Extension updates" /></div>{/if}
        {#each bulkErrors as message}<p class="notice-error" role="alert">{message}</p>{/each}
        <ul class="bulk-review-list" aria-label="Extensions to approve">
          {#each bulkEntries as entry (entry.item.id)}
            {@const changes = valueChanges(permissionKeys(entry.permissions), permissionKeys(entry.previousPermissions))}
            {@const added = entry.permissions.filter((item) => changes.some((change) => change.change === "added" && change.value === `${item.permissionKind}:${item.permissionValue}`))}
            {@const dropped = changes.filter((change) => change.change === "removed").length}
            <li>
              <label class="bulk-review-choice">
                <Checkbox bind:checked={entry.approve} aria-label={`Approve and turn on ${entry.item.displayName}`} />
                <span><strong>{entry.item.displayName}</strong><small>{entry.previous ? `${entry.previous.version} → ${entry.item.version}` : `Version ${entry.item.version}`}</small></span>
              </label>
              {#if added.length}
                <ul class="extension-permissions">
                  {#each added as item (item.id)}
                    {@const copy = permissionCopy(item.permissionKind, item.permissionValue)}
                    {@const Icon = copy.icon}
                    <li><span class="permission-icon" aria-hidden="true"><Icon size={18} /></span><span class="permission-copy"><strong>{copy.title}</strong><small>{copy.detail}</small>{#if entry.previous}<span class="permission-tags"><em class="new">New access</em></span>{/if}</span></li>
                  {/each}
                </ul>
              {:else}<p class="bulk-review-note">{entry.permissions.length ? "No new access" : "Asks for no access"}</p>{/if}
              {#if dropped}<p class="bulk-review-note">No longer asks for {dropped} {dropped === 1 ? "permission" : "permissions"}.</p>{/if}
            </li>
          {/each}
        </ul>
      </div>
      <div class="extension-review-footer">
        <label class="permission-confirm"><Checkbox bind:checked={bulkConfirmed} disabled={!bulkEntries.length} /><span>I understand what these extensions can access.</span></label>
        <footer class="dialog-actions"><Button variant="secondary" disabled={Boolean(action)} onclick={() => { bulkOpen = false; void changed(); }}>Close</Button><Button disabled={!bulkConfirmed || !bulkChosen || Boolean(action)} onclick={() => void applyBulk()}>{action === "bulk-apply" ? "Turning on…" : `Approve ${bulkChosen}`}</Button></footer>
      </div>
    </Dialog.Content></Dialog.Portal>
  </Dialog.Root>

  <AlertDialog.Root bind:open={() => confirmOpen, (value) => { if (!action) { confirmOpen = value; if (!value && view === "catalog" && !catalogDialogOpen) open = false; } }}>
    <AlertDialog.Portal><AlertDialog.Overlay class="dialog-overlay" /><AlertDialog.Content class={`confirm-dialog${activatePackage ? " extension-activation-dialog" : ""}`}>
      <AlertDialog.Title>{reviewAccessPackage ? reviewAccessPackage.state.toLowerCase() === "disabled" ? `Review and enable ${reviewAccessPackage.displayName}?` : `Review access for ${reviewAccessPackage.displayName}?` : activatePackage ? `Activate ${activatePackage.displayName}?` : removePackage ? `Uninstall ${removePackage.displayName}?` : `Remove ${removeRegistry?.name ?? "this registry"}?`}</AlertDialog.Title>
      <AlertDialog.Description>{reviewAccessPackage ? reviewAccessPackage.active ? "The extension runtime will stop now and remain disabled until you save a fresh permission review and reactivate it." : "The extension will remain disabled until you save a fresh permission review and reactivate it." : activatePackage ? "This starts the extension with the access you approved. You can turn it off or remove it from its source page." : removePackage ? "The package and runtime are removed. Encrypted Source accounts remain available for a later reinstall." : "You can add this registry URL again later."}</AlertDialog.Description>
      {#if activatePackage}
        {@render installSteps(2)}
        <div class="extension-activation-body">
          {#if activationAccessLoading}<p class="extension-flow-note" role="status">Loading approved access…</p>
          {:else if activationAccessReady}{@render accessSummary(activatePackage, activationPermissions, true)}{/if}
          {#if activatePackage.usesSignedSession}<p class="extension-flow-note">Open the source after enabling to complete sign-in.</p>{/if}
        </div>
      {/if}
      {#if dialogError}<p class="notice-error" role="alert">{dialogError}</p>{/if}
      <footer class="dialog-actions"><AlertDialog.Cancel class={buttonVariants({ variant: "secondary" })} disabled={Boolean(action)}>Cancel</AlertDialog.Cancel><Button variant={activatePackage || reviewAccessPackage ? "default" : "destructive"} disabled={Boolean(action) || Boolean(activatePackage && !activationAccessReady)} onclick={() => void confirm()}>{action ? reviewAccessPackage ? "Stopping…" : activatePackage ? "Activating…" : "Removing…" : reviewAccessPackage ? "Stop and review" : activatePackage ? "Activate extension" : removePackage ? "Uninstall" : "Remove registry"}</Button></footer>
    </AlertDialog.Content></AlertDialog.Portal>
  </AlertDialog.Root>

<style>
  .extension-row { display: block; container-type: inline-size; }
  .extension-layout { display: grid; grid-template-columns: 2.5rem minmax(16ch, 1fr) auto; align-items: center; gap: var(--space-2) var(--space-4); }
  .extension-layout > :global(.provider-art) { display: grid; width: 2.5rem; height: 2.5rem; align-self: start; place-items: center; border-radius: var(--radius-md); background: var(--color-surface-raised); color: var(--color-ink-muted); }
  .extension-controls { display: flex; align-items: center; gap: var(--space-4); }
  .extension-controls :global(.badge) { text-transform: none; }
  .extension-layout > :global(.extension-session) { grid-column: 2 / -1; }
  @container (max-width: 560px) {
    .extension-layout { grid-template-columns: 2.5rem minmax(0, 1fr); }
    .extension-copy { display: contents; }
    .extension-layout > :global(.extension-session) { grid-column: 1 / -1; }
    .extension-title { grid-column: 2; min-width: min(16ch, 100%); }
    .extension-copy > p, .extension-copy > .source-row-capabilities { grid-column: 1 / -1; min-width: min(16ch, 100%); }
    .source-row-capabilities { flex-wrap: wrap; }
    .extension-controls { grid-column: 1 / -1; flex-wrap: wrap; justify-content: flex-start; gap: var(--space-2); }
    .extension-actions { display: flex; flex-wrap: wrap; justify-content: flex-start; min-width: 0; max-width: 100%; }
    .extension-actions :global([data-slot="button"]) { max-width: 100%; white-space: normal; }
  }
  :global(.extension-remove-link) { color: var(--danger); }
  .extension-setup-steps {
    display: flex;
    flex: 0 0 auto;
    align-items: center;
    justify-content: space-between;
    gap: var(--space-3);
    margin: 0;
    padding: var(--space-4) var(--space-6) 0;
    list-style: none;
  }
  .extension-setup-steps li {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    min-width: 0;
    color: var(--color-ink-muted);
    font-size: var(--text-xs);
  }
  .extension-setup-steps li[aria-current="step"] { color: var(--color-signal-text); font-weight: 600; }
  .extension-step-number {
    display: grid;
    flex: 0 0 auto;
    place-items: center;
    width: var(--space-6);
    height: var(--space-6);
    border: 1px solid var(--color-edge-strong);
    border-radius: 50%;
  }
  [aria-current="step"] .extension-step-number, .complete .extension-step-number {
    border-color: var(--color-signal);
    background: var(--color-signal-muted);
    color: var(--color-signal-text);
  }
  .extension-flow-note {
    margin: 0 0 var(--space-4);
    color: var(--color-ink-muted);
    font-size: var(--text-sm);
    line-height: 1.5;
  }
  .extension-install-form .extension-flow-note, .extension-review-body > .extension-flow-note { margin: 0; }
  .extension-review-summary {
    display: flex;
    align-items: center;
    gap: var(--space-3);
    min-width: 0;
    padding: var(--space-4);
    border: 1px solid var(--color-edge);
    border-radius: var(--radius-md);
  }
  .extension-review-summary > div { flex: 1; min-width: 0; }
  .extension-review-summary strong, .extension-review-summary small { display: block; overflow-wrap: anywhere; }
  .extension-review-summary small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .extension-access-overview { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-5); }
  .extension-access-overview section { min-width: 0; }
  .extension-access-overview h3 { margin: 0 0 var(--space-2); color: var(--color-ink); font-size: var(--text-sm); font-weight: 600; }
  .extension-access-overview p { margin: var(--space-2) 0 0; color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; }
  .extension-hosts { display: grid; gap: var(--space-1); margin: 0; padding-left: var(--space-4); font-size: var(--text-sm); overflow-wrap: anywhere; }
  .extension-review-hint { margin: var(--space-2) 0 0; padding: 0 var(--space-6); color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; }
  :global(.extension-activation-dialog) { --dialog-width: 36rem; }
  .extension-activation-body { padding: var(--space-5) var(--space-4) 0; }
  .extension-activation-body > .extension-flow-note { margin-top: var(--space-4); }
  @media (max-width: 650px) {
    .extension-setup-steps { gap: var(--space-2); padding: var(--space-4) var(--space-4) 0; }
    .extension-setup-steps li { gap: var(--space-1); }
    .extension-access-overview { grid-template-columns: minmax(0, 1fr); gap: var(--space-4); }
    .extension-review-summary { flex-wrap: wrap; }
    .extension-review-hint { padding-inline: var(--space-4); }
  }
</style>
