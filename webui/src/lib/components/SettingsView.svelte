<script lang="ts">
  import { onMount } from "svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import {
    home,
    settings,
    type ConfigSection,
    type UiSchema,
  } from "$lib/api";
  import EnvMigrationCard from "$lib/components/EnvMigrationCard.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SelectiveTransferCard from "$lib/components/SelectiveTransferCard.svelte";
  import CacheDiagnosticsCard from "$lib/components/CacheDiagnosticsCard.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { humanize } from "$lib/sources";
  import { fieldValue } from "$lib/settings";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { onThemeModeChange, readThemeMode, saveThemeMode, themeOptions, type ThemeMode } from "$lib/theme";

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
    { id: "maintenance", label: "Maintenance", href: "#/settings/maintenance" },
  ] as const;
  const contextualTrackCacheKeys = new Set([
    "STORAGE_MODE",
    "CACHE_DURATION_HOURS",
    "CACHE_TRANSCODE_MINUTES",
  ]);
  const integrationRoutingKeys = new Set([
    "AUDIO_QUALITY",
    "MATCHING_LOCAL_PREFERENCE_PERCENT",
    "MATCHING_EXTENSION_PENALTY_PERCENT",
  ]);
  const shellPreferenceKeys = new Set(["THEME"]);

  let schema = $state<UiSchema | null>(null);
  let config = $state<Record<string, unknown>>({});
  let storage = $state<Awaited<ReturnType<typeof settings.storage>> | null>(null);
  let cache = $state<Awaited<ReturnType<typeof settings.cache>> | null>(null);
  let cachePreview = $state<Awaited<ReturnType<typeof settings.cachePreview>> | null>(null);
  let loading = $state(true);
  let refreshing = $state(false);
  let action = $state("");
  let error = $state("");
  let feedback = $state("");
  let purgeTarget = $state("");
  let purgeOpen = $state(false);
  let loadedSection = $state("");
  let dirtyOwners = $state<string[]>([]);
  let serverChanged = $state(false);
  let openSections = $state(["general"]);
  let themeMode = $state<ThemeMode>("system");

  const active = $derived(section === "maintenance" ? "maintenance" : "general");
  const generalSections = $derived.by(() => {
    return (schema?.configSections ?? [])
      .filter((item) => item.id !== "spotify-import")
      .map((item) => ({
        ...item,
        fields: item.fields.filter((field) =>
          !contextualTrackCacheKeys.has(field.key) && !integrationRoutingKeys.has(field.key) && !shellPreferenceKeys.has(field.key.toUpperCase())),
      }))
      .filter((item) => item.fields.length);
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
    if (active === "general")
      requests.push(["config", settings.config()]);
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
      if (label === "storage")
        storage = result.value as Awaited<ReturnType<typeof settings.storage>>;
      if (label === "cache")
        cache = result.value as Awaited<ReturnType<typeof settings.cache>>;
      if (label === "cachePreview")
        cachePreview = result.value as Awaited<ReturnType<typeof settings.cachePreview>>;
    });
    const failed = results.filter((result) => result.status === "rejected");
    if (failed.length)
      error = failed[0].reason instanceof Error ? failed[0].reason.message : "Some settings are unavailable.";
    loading = false;
    refreshing = false;
  }

  const refreshScheduler = createRefreshScheduler(refresh);
  const scheduleRefresh = refreshScheduler.schedule;

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
    themeMode = readThemeMode();
    const unsubscribeTheme = onThemeModeChange((mode) => { themeMode = mode; });
    if (initialPanel && !openSections.includes(initialPanel))
      openSections = [...openSections, initialPanel];
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      unsubscribeTheme();
      refreshScheduler.cancel();
    };
  });
</script>

{#if loading}
  <Skeleton class="panel settings-panel skeleton-panel" aria-label="Loading Settings" aria-busy="true" />
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
        <Button variant="secondary" size="sm" onclick={() => void refresh()}>Retry</Button>
      </div>
    {/if}
    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}
    {#if serverChanged}
      <div class="degraded-banner" role="status">
        <p><strong>Server settings changed.</strong> Your unsaved edits are preserved.</p>
        <Button variant="secondary" size="sm" onclick={() => { dirtyOwners = []; serverChanged = false; void refresh(); }}>Reload server values</Button>
      </div>
    {/if}

    {#if active === "general"}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Runtime configuration</p><h2>General</h2><p>Durable settings apply immediately. Deployment-owned values are identified but cannot be edited here.</p></header>
        <section class="panel appearance-settings">
          <div><strong>Appearance</strong><small>Stored in this browser and applied immediately.</small></div>
          <label class="field"><span>Color theme</span><SelectField bind:value={themeMode} label="Color theme" options={themeOptions} onchange={(value) => saveThemeMode(value as ThemeMode)} /></label>
        </section>
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
                    <Checkbox name={field.key} checked={Boolean(fieldValue(config, field))} />
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
                <footer><Button type="submit" disabled={Boolean(action)}>{action === item.id ? "Saving…" : `Save ${item.label}`}</Button></footer>
              {/if}
            </form>
          </details>
        {/each}
      </div>
    {:else}
      <div class="settings-stack">
        <header class="settings-intro"><p class="eyebrow">Operations</p><h2>Maintenance</h2><p>Readiness, verified backups, bounded cache cleanup, and read-only media diagnostics.</p></header>
        <section class="maintenance-grid">
          <article class="panel maintenance-card">
            <header><div><strong>PostgreSQL</strong><small>Durable application state</small></div><Badge state={storage?.storage.readiness?.toLowerCase() === "ready" ? "healthy" : "degraded"}>{storage?.storage.readiness ?? "Unknown"}</Badge></header>
            <dl><div><dt>Provider</dt><dd>{storage?.storage.provider ?? "PostgreSQL"}</dd></div><div><dt>Verified backups</dt><dd>{storage?.backups.filter((backup) => backup.verifiedAt).length ?? 0}</dd></div></dl>
            <Button disabled={Boolean(action)} onclick={() => void run("backup", settings.backup, "Verified database backup created.")}>{action === "backup" ? "Creating…" : "Create verified backup"}</Button>
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
            <Button variant="secondary" disabled={Boolean(action)} onclick={() => void run("media", settings.mediaProbe, "Media pipeline checked.")}>{action === "media" ? "Testing…" : "Test media pipeline"}</Button>
          </article>
          <article class="panel maintenance-card">
            <header><div><strong>Playlist readiness</strong><small>Source access and songs available to listeners</small></div></header>
            <Button variant="secondary" disabled={Boolean(action)} onclick={() => void run("playlists", settings.playlistProbe, "Playlist pipeline checked.")}>{action === "playlists" ? "Testing…" : "Test playlist readiness"}</Button>
          </article>
          {#if administrator}<SelectiveTransferCard />{/if}
          {#if administrator}
            <article class="panel maintenance-card">
              <header><div><strong>Setup guide</strong><small>Durable account onboarding</small></div></header>
              <p>Reopen setup without clearing browser data or changing runtime health.</p>
              <Button variant="secondary" onclick={() => void onOpenSetup()}>Open setup guide</Button>
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
