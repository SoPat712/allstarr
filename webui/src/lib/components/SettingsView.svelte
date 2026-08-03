<script lang="ts">
  import { onMount } from "svelte";
  import { ArrowDown, ArrowUp } from "lucide-svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    home,
    settings,
    sources,
    type ConfigSection,
    type ProviderAccount,
    type PriorityGroup,
    type UiSchema,
  } from "$lib/api";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import EnvMigrationCard from "$lib/components/EnvMigrationCard.svelte";
  import ExtensionsView from "$lib/components/ExtensionsView.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SelectiveTransferCard from "$lib/components/SelectiveTransferCard.svelte";
  import CacheDiagnosticsCard from "$lib/components/CacheDiagnosticsCard.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { audienceLabel, humanize } from "$lib/sources";
  import { fieldValue, move, routingOrder } from "$lib/settings";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let {
    section = "general",
    initialPanel = "",
    administrator,
    onOpenSetup,
  }: {
    section?: string;
    initialPanel?: string;
    administrator: boolean;
    onOpenSetup: () => void | Promise<void>;
  } = $props();

  const tabs = [
    { id: "general", label: "General", href: "#/settings/general" },
    { id: "accounts", label: "Accounts", href: "#/settings/accounts" },
    { id: "routing", label: "Provider routing", href: "#/settings/routing" },
    { id: "extensions", label: "Extensions", href: "#/settings/extensions" },
    { id: "maintenance", label: "Maintenance", href: "#/settings/maintenance" },
  ] as const;

  let schema = $state<UiSchema | null>(null);
  let config = $state<Record<string, unknown>>({});
  let accounts = $state<ProviderAccount[]>([]);
  let storage = $state<Awaited<ReturnType<typeof settings.storage>> | null>(null);
  let cache = $state<Awaited<ReturnType<typeof settings.cache>> | null>(null);
  let cachePreview = $state<Awaited<ReturnType<typeof settings.cachePreview>> | null>(null);
  let orders = $state<Record<string, string[]>>({});
  let loading = $state(true);
  let refreshing = $state(false);
  let action = $state("");
  let error = $state("");
  let feedback = $state("");
  let purgeTarget = $state("");
  let purgeOpen = $state(false);
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let dragging = $state<{ groupId: string; index: number } | null>(null);
  let loadedSection = $state("");
  let dirtyOwners = $state<string[]>([]);
  let serverChanged = $state(false);
  let openSections = $state(["general"]);

  const active = $derived(tabs.some((item) => item.id === section) ? section : "general");
  const generalSections = $derived.by(() => {
    const sections = (schema?.configSections ?? []).filter((item) => item.id !== "spotify-import");
    const providerSections = (schema?.providers ?? [])
      .filter((item) => item.connectionKind === "operator_managed" && item.configSchema?.length)
      .map((item) => ({
        id: `provider-${item.id}`,
        label: item.name,
        fields: item.configSchema ?? [],
      }));
    return sections.length ? [sections[0], ...providerSections, ...sections.slice(1)] : providerSections;
  });
  const cacheDiskCeiling = $derived(
    Number((config.cache as Record<string, unknown> | undefined)?.mediaMaximumMegabytes ?? 512),
  );

  $effect(() => {
    const section = active;
    if (section === loadedSection) return;
    const hadSection = loadedSection.length > 0;
    loadedSection = section;
    if (hadSection) void refresh();
  });

  function provider(id: string) {
    return schema?.providers.find((item) => item.id.toLowerCase() === id.toLowerCase());
  }

  function markDirty(owner: string) {
    if (!dirtyOwners.includes(owner)) dirtyOwners = [...dirtyOwners, owner];
  }

  function markSaved(owner: string) {
    dirtyOwners = dirtyOwners.filter((item) => item !== owner);
    if (!dirtyOwners.length) serverChanged = false;
  }

  function disclosureToggled(event: Event, id: string) {
    const open = (event.currentTarget as HTMLDetailsElement).open;
    if (open && !openSections.includes(id)) openSections = [...openSections, id];
    if (!open) openSections = openSections.filter((item) => item !== id);
  }

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const requests: Array<[string, Promise<unknown>]> = [["schema", home.schema()]];
    if (active === "general" || active === "routing")
      requests.push(["config", settings.config()]);
    if (active === "accounts") requests.push(["accounts", sources.accounts()]);
    if (active === "maintenance") requests.push(
      ["storage", settings.storage()],
      ["cache", settings.cache()],
      ["cachePreview", settings.cachePreview()],
    );
    const results = await Promise.allSettled(requests.map((request) => request[1]));
    results.forEach((result, index) => {
      if (result.status !== "fulfilled") return;
      const label = requests[index][0];
      if (label === "schema") schema = result.value as UiSchema;
      if (label === "config") {
        if (dirtyOwners.length) serverChanged = true;
        else config = result.value as Record<string, unknown>;
      }
      if (label === "accounts")
        accounts = (result.value as Awaited<ReturnType<typeof sources.accounts>>).accounts;
      if (label === "storage")
        storage = result.value as Awaited<ReturnType<typeof settings.storage>>;
      if (label === "cache")
        cache = result.value as Awaited<ReturnType<typeof settings.cache>>;
      if (label === "cachePreview")
        cachePreview = result.value as Awaited<ReturnType<typeof settings.cachePreview>>;
    });
    if (schema && !dirtyOwners.length) {
      orders = Object.fromEntries((schema.priorityGroups ?? [])
        .map((group) => [group.id, routingOrder(config, group)]));
    }
    const failed = results.filter((result) => result.status === "rejected");
    if (failed.length)
      error = failed[0].reason instanceof Error ? failed[0].reason.message : "Some settings are unavailable.";
    loading = false;
    refreshing = false;
  }

  function scheduleRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void refresh();
    }, 250);
  }

  async function saveSection(event: SubmitEvent, item: ConfigSection) {
    event.preventDefault();
    if (action) return;
    action = item.id;
    const data = new FormData(event.currentTarget as HTMLFormElement);
    const updates = Object.fromEntries(item.fields
      .filter((field) => !field.readOnly && field.ownership !== "deployment")
      .map((field) => [
        field.key,
        field.type === "toggle" ? String(data.get(field.key) === "on") : String(data.get(field.key) ?? ""),
      ]));
    try {
      await settings.save(updates);
      markSaved(item.id);
      feedback = `${item.label} saved.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : `${item.label} could not be saved.`;
    } finally {
      action = "";
    }
  }

  async function saveOrder(group: PriorityGroup) {
    if (action) return;
    action = group.id;
    try {
      await settings.save({ [group.envKey]: (orders[group.id] ?? []).join(",") });
      markSaved(group.id);
      feedback = `${group.label} saved.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Provider routing could not be saved.";
    } finally {
      action = "";
    }
  }

  function moveProvider(group: PriorityGroup, index: number, direction: -1 | 1) {
    const order = orders[group.id] ?? [];
    const providerId = order[index];
    orders = { ...orders, [group.id]: move(order, index, direction) };
    markDirty(group.id);
    feedback = `${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + direction + 1}.`;
  }

  function dropProvider(group: PriorityGroup, index: number) {
    if (!dragging || dragging.groupId !== group.id || dragging.index === index) return;
    const order = [...(orders[group.id] ?? [])];
    const [providerId] = order.splice(dragging.index, 1);
    order.splice(index, 0, providerId);
    orders = { ...orders, [group.id]: order };
    markDirty(group.id);
    feedback = `${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + 1}.`;
    dragging = null;
  }

  async function run(name: string, operation: () => Promise<unknown>, message: string) {
    if (action) return;
    action = name;
    try {
      const result = await operation();
      feedback = "message" in (result as object)
        ? String((result as { message: unknown }).message)
        : message;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : `${message} failed.`;
    } finally {
      action = "";
    }
  }

  async function purge() {
    if (!purgeTarget) return;
    const scope = ["metadata", "media", "all"].includes(purgeTarget)
      ? purgeTarget as "metadata" | "media" | "all"
      : null;
    await run(
      `purge-${purgeTarget}`,
      () => scope ? settings.purgeCache(scope) : settings.purgeCacheCategory(purgeTarget),
      `${humanize(purgeTarget)} cache purged.`,
    );
    purgeOpen = false;
  }

  onMount(() => {
    if (initialPanel && !openSections.includes(initialPanel))
      openSections = [...openSections, initialPanel];
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if loading}
  <section class="panel settings-panel skeleton-panel" aria-label="Loading Settings" aria-busy="true"></section>
{:else if !schema}
  <RouteError
    eyebrow="Settings unavailable"
    title="Allstarr could not load runtime settings."
    message={error}
    onRetry={refresh}
  />
{:else}
  <section class="settings-workspace" aria-busy={refreshing}>
    <SegmentedNav items={tabs} {active} label="Settings sections" class="settings-tabs" />

    {#if error}
      <div class="degraded-banner" role="status">
        <span aria-hidden="true">!</span><p><strong>Some settings may be stale.</strong> {error}</p>
        <button type="button" onclick={() => void refresh()}>Retry</button>
      </div>
    {/if}
    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}
    {#if serverChanged}
      <div class="degraded-banner" role="status">
        <p><strong>Server settings changed.</strong> Your unsaved edits are preserved.</p>
        <button type="button" onclick={() => { dirtyOwners = []; serverChanged = false; void refresh(); }}>Reload server values</button>
      </div>
    {/if}

    {#if active === "general"}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Runtime configuration</p><h2>General</h2><p>Durable settings apply immediately. Deployment-owned values are identified but cannot be edited here.</p></header>
        {#each generalSections as item}
          <details
            class="panel settings-disclosure"
            open={openSections.includes(item.id)}
            ontoggle={(event) => disclosureToggled(event, item.id)}
          >
            <summary><span><strong>{item.label}</strong><small>{item.fields.filter((field) => !field.readOnly).length} editable</small></span></summary>
            <form class="settings-fields" oninput={() => markDirty(item.id)} onsubmit={(event) => void saveSection(event, item)}>
              {#each item.fields as field}
                <label class="setting-field" class:read-only={field.readOnly || field.ownership === "deployment"}>
                  <span><strong>{field.label}</strong>{#if field.ownership === "deployment"}<small>Deployment-owned</small>{/if}</span>
                  {#if field.readOnly || field.ownership === "deployment"}
                    <output>{String(fieldValue(config, field))}</output>
                  {:else if field.type === "select"}
                    <SelectField
                      name={field.key}
                      label={field.label}
                      value={String(fieldValue(config, field))}
                      options={field.options ?? []}
                      onchange={() => markDirty(item.id)}
                    />
                  {:else if field.type === "toggle"}
                    <input name={field.key} type="checkbox" checked={Boolean(fieldValue(config, field))} />
                  {:else}
                    <input
                      name={field.key}
                      type={field.type === "number" ? "number" : "text"}
                      value={String(fieldValue(config, field))}
                      min={field.min ?? undefined}
                      max={field.max ?? undefined}
                    />
                  {/if}
                  {#if field.helpText}<small>{field.helpText}</small>{/if}
                </label>
              {/each}
              {#if item.id === "cache"}
                <p class="settings-impact">
                  Estimated ceiling after save: hot RAM remains fixed at 16 MiB and disk remains deployment-bounded at {cacheDiskCeiling.toLocaleString()} MiB. Retention changes affect refresh frequency, not those ceilings.
                </p>
              {/if}
              {#if item.fields.some((field) => !field.readOnly && field.ownership !== "deployment")}
                <footer><button class="button-primary" type="submit" disabled={Boolean(action)}>{action === item.id ? "Saving…" : `Save ${item.label}`}</button></footer>
              {/if}
            </form>
          </details>
        {/each}
      </div>
    {:else if active === "accounts"}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Identity and secure access</p><h2>Accounts</h2><p>Provider credentials and audiences remain in Sources so Settings does not become a second account owner.</p></header>
        <section class="panel settings-account-panel">
          <header><div><strong>Source connections</strong><small>{accounts.length} encrypted account{accounts.length === 1 ? "" : "s"}</small></div><a class="button-primary" href="#/sources">Open Sources</a></header>
          <div>
            {#each accounts as account}
              <article>
                <ProviderArtwork id={account.providerId} definition={provider(account.providerId)} />
                <span><strong>{account.sourceDisplayName || account.displayName}</strong><small>{audienceLabel(account)} · {account.enabled ? "Enabled" : "Disabled"}</small></span>
                <span class={`status-pill ${account.secret.configured && !account.secret.revoked ? "healthy" : "needs_config"}`}>
                  {account.secret.configured && !account.secret.revoked ? "Stored" : "Setup needed"}
                </span>
              </article>
            {:else}
              <div class="compact-empty"><strong>No Source connections</strong><p>Connect one from Sources when a provider requires an account.</p></div>
            {/each}
          </div>
        </section>
      </div>
    {:else if active === "routing"}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Provider-neutral policy</p><h2>Provider routing</h2><p>The local media server remains locked first. Move fallback Sources into the order Allstarr should try them.</p></header>
        <div class="routing-groups">
          {#each schema.priorityGroups ?? [] as group}
            <section class="panel routing-group">
              <header><div><strong>{group.label}</strong><small>{group.description}</small></div><button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void saveOrder(group)}>{action === group.id ? "Saving…" : "Save order"}</button></header>
              <ol>
                {#if group.pinnedProvider}
                  <li class="pinned">
                    <ProviderArtwork id={group.pinnedProvider.id} label={group.pinnedProvider.name} />
                    <span><strong>{group.pinnedProvider.name}</strong><small>{group.pinnedProvider.reason}</small></span>
                    <span class="status-pill healthy">Local · fixed</span>
                  </li>
                {/if}
                {#each orders[group.id] ?? group.providers as providerId, index}
                  {@const definition = provider(providerId)}
                  <li
                    draggable="true"
                    class:dragging={dragging?.groupId === group.id && dragging.index === index}
                    ondragstart={() => { dragging = { groupId: group.id, index }; }}
                    ondragover={(event) => event.preventDefault()}
                    ondrop={() => dropProvider(group, index)}
                    ondragend={() => { dragging = null; }}
                  >
                    <ProviderArtwork id={providerId} definition={definition} />
                    <span><strong>{definition?.name ?? humanize(providerId)}</strong><small>{definition?.categories?.map(humanize).join(" · ") || "Provider Source"}</small></span>
                    <span class="routing-actions">
                      <button type="button" aria-label={`Move ${definition?.name ?? providerId} up`} disabled={index === 0} onclick={() => moveProvider(group, index, -1)}><ArrowUp size={18} aria-hidden="true" /></button>
                      <button type="button" aria-label={`Move ${definition?.name ?? providerId} down`} disabled={index === (orders[group.id] ?? group.providers).length - 1} onclick={() => moveProvider(group, index, 1)}><ArrowDown size={18} aria-hidden="true" /></button>
                    </span>
                  </li>
                {/each}
              </ol>
            </section>
          {/each}
        </div>
      </div>
    {:else if active === "extensions"}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Extension control plane</p><h2>Extensions</h2><p>Installed providers use the same capability, account, routing, and readiness components as built-ins.</p></header>
        <ExtensionsView />
      </div>
    {:else}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Operations</p><h2>Maintenance</h2><p>Readiness, verified backups, bounded cache cleanup, and read-only media diagnostics.</p></header>
        <section class="maintenance-grid">
          <article class="panel maintenance-card">
            <header><div><strong>PostgreSQL</strong><small>Durable application state</small></div><span class={`status-pill ${storage?.storage.readiness?.toLowerCase() === "ready" ? "healthy" : "degraded"}`}>{storage?.storage.readiness ?? "Unknown"}</span></header>
            <dl><div><dt>Provider</dt><dd>{storage?.storage.provider ?? "PostgreSQL"}</dd></div><div><dt>Verified backups</dt><dd>{storage?.backups.filter((backup) => backup.verifiedAt).length ?? 0}</dd></div></dl>
            <button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void run("backup", settings.backup, "Verified database backup created.")}>{action === "backup" ? "Creating…" : "Create verified backup"}</button>
            <p>Restore remains an offline operator procedure and is intentionally unavailable against the active database.</p>
          </article>
          <CacheDiagnosticsCard
            snapshot={cache}
            preview={cachePreview}
            busy={Boolean(action)}
            onClean={() => void run("cleanup", settings.cleanCache, "Cache cleanup complete.")}
            onPurge={(target) => { purgeTarget = target; purgeOpen = true; }}
          />
          <article class="panel maintenance-card">
            <header><div><strong>Media pipeline</strong><small>Metadata, artwork, and authenticated playback</small></div></header>
            <button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void run("media", settings.mediaProbe, "Media pipeline checked.")}>{action === "media" ? "Testing…" : "Test media pipeline"}</button>
          </article>
          <article class="panel maintenance-card">
            <header><div><strong>Playlist readiness</strong><small>Source access and songs available to listeners</small></div></header>
            <button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void run("playlists", settings.playlistProbe, "Playlist pipeline checked.")}>{action === "playlists" ? "Testing…" : "Test playlist readiness"}</button>
          </article>
          {#if administrator}<SelectiveTransferCard />{/if}
          {#if administrator}
            <article class="panel maintenance-card">
              <header><div><strong>Setup guide</strong><small>Durable account onboarding</small></div></header>
              <p>Reopen setup without clearing browser data or changing runtime health.</p>
              <button class="button-secondary" type="button" onclick={() => void onOpenSetup()}>Open setup guide</button>
            </article>
          {/if}
          {#if administrator}<EnvMigrationCard />{/if}
        </section>
      </div>
    {/if}
  </section>

  <ConfirmDialog
    bind:open={purgeOpen}
    title={purgeTarget === "all" ? "Purge the application cache?" : `Purge ${humanize(purgeTarget)} cache?`}
    description="Disposable metadata and media payloads will be removed. PostgreSQL business state, accounts, mappings, playlists, and kept audio are not affected."
    confirmLabel="Purge cache"
    onConfirm={purge}
  />
{/if}
