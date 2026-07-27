<script lang="ts">
  import { onMount } from "svelte";
  import { AlertDialog } from "bits-ui";
  import {
    home,
    settings,
    sources,
    type ConfigSection,
    type ProviderAccount,
    type PriorityGroup,
    type UiSchema,
  } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import EnvMigrationCard from "$lib/components/EnvMigrationCard.svelte";
  import ExtensionsView from "$lib/components/ExtensionsView.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import { audienceLabel, humanize } from "$lib/sources";
  import { fieldValue, move, routingOrder } from "$lib/settings";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let {
    section = "general",
    administrator,
  }: {
    section?: string;
    administrator: boolean;
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
  let purgeScope = $state<"metadata" | "media" | "all" | null>(null);
  let purgeOpen = $state(false);
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  const active = $derived(tabs.some((item) => item.id === section) ? section : "general");
  const generalSections = $derived((schema?.configSections ?? [])
    .filter((item) => item.id !== "spotify-import"));

  function provider(id: string) {
    return schema?.providers.find((item) => item.id.toLowerCase() === id.toLowerCase());
  }

  function formatBytes(value?: number | null) {
    if (!value) return "0 B";
    const units = ["B", "KiB", "MiB", "GiB"];
    const power = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
    return `${(value / 1024 ** power).toFixed(power ? 1 : 0)} ${units[power]}`;
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
      if (label === "config") config = result.value as Record<string, unknown>;
      if (label === "accounts")
        accounts = (result.value as Awaited<ReturnType<typeof sources.accounts>>).accounts;
      if (label === "storage")
        storage = result.value as Awaited<ReturnType<typeof settings.storage>>;
      if (label === "cache")
        cache = result.value as Awaited<ReturnType<typeof settings.cache>>;
      if (label === "cachePreview")
        cachePreview = result.value as Awaited<ReturnType<typeof settings.cachePreview>>;
    });
    if (schema) {
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
      feedback = `${group.label} saved.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Provider routing could not be saved.";
    } finally {
      action = "";
    }
  }

  function moveProvider(group: PriorityGroup, index: number, direction: -1 | 1) {
    orders = { ...orders, [group.id]: move(orders[group.id] ?? [], index, direction) };
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
    if (!purgeScope) return;
    await run(`purge-${purgeScope}`, () => settings.purgeCache(purgeScope!), `${humanize(purgeScope)} cache purged.`);
    purgeOpen = false;
  }

  onMount(() => {
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
  <section class="panel route-error" role="alert">
    <span aria-hidden="true">!</span>
    <div><p class="eyebrow">Settings unavailable</p><h2>Allstarr could not load runtime settings.</h2><p>{error}</p></div>
    <button class="button-secondary" type="button" onclick={() => void refresh()}>Try again</button>
  </section>
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

    {#if active === "general"}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Runtime configuration</p><h2>General</h2><p>Durable settings apply immediately. Deployment-owned values are identified but cannot be edited here.</p></header>
        {#each generalSections as item}
          <details class="panel settings-disclosure" open={item.id === "general"}>
            <summary><span><strong>{item.label}</strong><small>{item.fields.filter((field) => !field.readOnly).length} editable</small></span></summary>
            <form class="settings-fields" onsubmit={(event) => void saveSection(event, item)}>
              {#each item.fields as field}
                <label class="setting-field" class:read-only={field.readOnly || field.ownership === "deployment"}>
                  <span><strong>{field.label}</strong>{#if field.ownership === "deployment"}<small>Deployment-owned</small>{/if}</span>
                  {#if field.readOnly || field.ownership === "deployment"}
                    <output>{String(fieldValue(config, field))}</output>
                  {:else if field.type === "select"}
                    <select name={field.key}>
                      {#each field.options ?? [] as option}<option value={option} selected={fieldValue(config, field) === option}>{option}</option>{/each}
                    </select>
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
                <ProviderMark id={account.providerId} definition={provider(account.providerId)} />
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
                    <ProviderMark id={group.pinnedProvider.id} label={group.pinnedProvider.name} />
                    <span><strong>{group.pinnedProvider.name}</strong><small>{group.pinnedProvider.reason}</small></span>
                    <span class="status-pill healthy">Local · fixed</span>
                  </li>
                {/if}
                {#each orders[group.id] ?? group.providers as providerId, index}
                  {@const definition = provider(providerId)}
                  <li>
                    <ProviderMark id={providerId} definition={definition} />
                    <span><strong>{definition?.name ?? humanize(providerId)}</strong><small>{definition?.categories?.map(humanize).join(" · ") || "Provider Source"}</small></span>
                    <span class="routing-actions">
                      <button type="button" aria-label={`Move ${definition?.name ?? providerId} up`} disabled={index === 0} onclick={() => moveProvider(group, index, -1)}>↑</button>
                      <button type="button" aria-label={`Move ${definition?.name ?? providerId} down`} disabled={index === (orders[group.id] ?? group.providers).length - 1} onclick={() => moveProvider(group, index, 1)}>↓</button>
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
          <article class="panel maintenance-card">
            <header><div><strong>Application cache</strong><small>Disposable metadata and media</small></div><span>{formatBytes((cache?.database.payloadBytes ?? 0) + (cache?.media.payloadBytes ?? 0))}</span></header>
            <dl><div><dt>Metadata entries</dt><dd>{cache?.database.entryCount ?? 0}</dd></div><div><dt>Media entries</dt><dd>{cache?.media.entryCount ?? 0}</dd></div><div><dt>Reclaimable</dt><dd>{formatBytes((cachePreview?.metadata.reclaimableBytes ?? 0) + (cachePreview?.media.reclaimableBytes ?? 0) + (cachePreview?.unreferencedArtworkBytes ?? 0))}</dd></div></dl>
            <div class="maintenance-actions">
              <button class="button-primary" type="button" disabled={Boolean(action)} onclick={() => void run("cleanup", settings.cleanCache, "Cache cleanup complete.")}>{action === "cleanup" ? "Cleaning…" : "Clean expired entries"}</button>
              <button class="button-danger" type="button" onclick={() => { purgeScope = "all"; purgeOpen = true; }}>Purge cache</button>
            </div>
          </article>
          <article class="panel maintenance-card">
            <header><div><strong>Media pipeline</strong><small>Metadata, artwork, and authenticated playback</small></div></header>
            <button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void run("media", settings.mediaProbe, "Media pipeline checked.")}>{action === "media" ? "Testing…" : "Test media pipeline"}</button>
          </article>
          <article class="panel maintenance-card">
            <header><div><strong>Playlist pipeline</strong><small>Source access and playable materialization</small></div></header>
            <button class="button-secondary" type="button" disabled={Boolean(action)} onclick={() => void run("playlists", settings.playlistProbe, "Playlist pipeline checked.")}>{action === "playlists" ? "Testing…" : "Test playlist readiness"}</button>
          </article>
          {#if administrator}<EnvMigrationCard />{/if}
        </section>
      </div>
    {/if}
  </section>

  <AlertDialog.Root bind:open={purgeOpen}>
    <AlertDialog.Portal>
      <AlertDialog.Overlay class="dialog-overlay" />
      <AlertDialog.Content class="confirm-dialog">
        <AlertDialog.Title>Purge the application cache?</AlertDialog.Title>
        <AlertDialog.Description>Disposable metadata and media payloads will be removed. PostgreSQL business state, accounts, mappings, playlists, and kept audio are not affected.</AlertDialog.Description>
        <footer><AlertDialog.Cancel class="button-secondary">Cancel</AlertDialog.Cancel><AlertDialog.Action class="button-danger" onclick={() => void purge()}>Purge cache</AlertDialog.Action></footer>
      </AlertDialog.Content>
    </AlertDialog.Portal>
  </AlertDialog.Root>
{/if}
