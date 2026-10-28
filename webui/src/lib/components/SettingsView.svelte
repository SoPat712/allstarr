<script lang="ts">
  import { waitForRestart } from "$lib/restart";
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import {
    home,
    settings,
    type ConfigSection,
    type PlaybackStart,
    type UiSchema,
  } from "$lib/api";
  import SetupGuide from "$lib/components/SetupGuide.svelte";
  import AccessSettingsCard from "$lib/components/AccessSettingsCard.svelte";
  import StorageVerificationCard from "$lib/components/StorageVerificationCard.svelte";
  import HealthView from "$lib/components/HealthView.svelte";
  import BackupsView from "$lib/components/BackupsView.svelte";
  import ListenersView from "$lib/components/ListenersView.svelte";
  import AlertsView from "$lib/components/AlertsView.svelte";
  import type { OnboardingState } from "$lib/api";
  import RouteError from "$lib/components/RouteError.svelte";
  import CacheDiagnosticsCard from "$lib/components/CacheDiagnosticsCard.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import { humanize } from "$lib/sources";
  import { fieldValue, playbackStartLabel, playbackStartOptions } from "$lib/settings";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { onThemeModeChange, readThemeMode, saveThemeMode, themeOptions, type ThemeMode } from "$lib/theme";
  import { Activity, Archive, Bell, HardDrive, Lock, Network, Palette, Rocket, SlidersHorizontal, Users } from "@lucide/svelte";

  let {
    section = "general",
    initialPanel = "",
    administrator,
    onOpenSetup,
    onboardingState = null,
    onSetupChanged = () => {},
  }: {
    section?: string;
    initialPanel?: string;
    administrator: boolean;
    onOpenSetup: () => void | Promise<void>;
    onboardingState?: OnboardingState | null;
    onSetupChanged?: (state: OnboardingState) => void;
  } = $props();

  const allPages = [
    { id: "appearance", label: "Appearance", group: "This browser", icon: Palette, title: "Appearance", description: "How Allstarr looks in this browser. Other devices keep their own choice." },
    { id: "access", label: "Access", group: "Server", icon: Lock, title: "Access", description: "Manage media-server access for playlist sync and library scans." },
    { id: "general", label: "General", group: "Server", icon: SlidersHorizontal, title: "General", description: "Server-wide behavior for every listener." },
    { id: "listeners", label: "Listeners", group: "Server", icon: Users, title: "Listeners", description: "Manage clean listening and see personal playlist copies." },
    { id: "alerts", label: "Alerts", group: "Server", icon: Bell, title: "Alerts", description: "Choose where Allstarr sends problem alerts." },
    { id: "storage", label: "Storage", group: "Server", icon: HardDrive, title: "Storage", description: "Manage saved music, cache space, and playback capacity." },
    { id: "network", label: "Network", group: "Server", icon: Network, title: "Network and security", description: "How the admin interface is exposed. These are usually set by your deployment." },
    { id: "backups", label: "Backups", group: "Maintenance", icon: Archive, title: "Backups", description: "Save and restore Allstarr's database, accounts and encryption keys." },
    { id: "health", label: "Health", group: "Maintenance", icon: Activity, title: "Health", description: "Monitor server checks and recent results." },
    { id: "diagnostics", label: "Diagnostics", group: "Maintenance", icon: Activity, title: "Diagnostics", description: "Run checks when playback or playlists aren't working." },
    { id: "setup", label: "Setup", group: "Maintenance", icon: Rocket, title: "Setup", description: "Reopen the setup guide or import settings from Allstarr v2." },
  ] as const;
  const pages = $derived(allPages.filter(item => administrator || item.id === "alerts"));
  const navGroups = ["This browser", "Server", "Maintenance"] as const;
  const sectionPages: Record<string, string> = { general: "general", paths: "storage", cache: "storage", "audio-limits": "storage", network: "network", "client-address": "network" };
  const contextualTrackCacheKeys = new Set([
    "STORAGE_MODE",
    "CACHE_DURATION_HOURS",
    "CACHE_TRANSCODE_MINUTES",
  ]);
  const integrationRoutingKeys = new Set([
    "AUDIO_QUALITY",
    "MATCHING_LOCAL_PREFERENCE_PERCENT",
  ]);
  const shellPreferenceKeys = new Set(["THEME"]);

  let schema = $state<UiSchema | null>(null);
  let config = $state<Record<string, unknown>>({});
  let cache = $state<Awaited<ReturnType<typeof settings.cache>> | null>(null);
  let cachePreview = $state<Awaited<ReturnType<typeof settings.cachePreview>> | null>(null);
  let loading = $state(true);
  let refreshing = $state(false);
  let action = $state("");
  let error = $state("");
  let purgeTarget = $state("");
  let purgeOpen = $state(false);
  let loadedSection = $state("");
  let dirtyOwners = $state<string[]>([]);
  let serverChanged = $state(false);
  let themeMode = $state<ThemeMode>("system");

  const active = $derived(section === "maintenance"
    ? "backups"
    : pages.some((item) => item.id === section) ? section : administrator ? "general" : "alerts");
  const activePage = $derived(pages.find((item) => item.id === active) ?? pages[0]);
  const configSections = $derived.by(() => {
    return (schema?.configSections ?? [])
      .filter((item) => item.id !== "spotify-import")
      .map((item) => ({
        ...item,
        fields: item.fields.filter((field) =>
          !/^(ODESLI_|EXTENSIONS_ALLOW_REMOTE_INSTALL)/.test(field.key) && !contextualTrackCacheKeys.has(field.key) && !integrationRoutingKeys.has(field.key) && !shellPreferenceKeys.has(field.key.toUpperCase())),
      }))
      .filter((item) => item.fields.length);
  });
  const pageSections = $derived(configSections.map((item) => ({ ...item,
    fields: item.fields.filter((field) => item.id !== "operator" ||
      (/Cors:|ChallengeSolver:/.test(field.valuePath ?? "") ? "network" : /BackupRetention|EventLog/.test(field.valuePath ?? "") ? "storage" : "general") === active),
  })).filter((item) => item.fields.length && (item.id === "operator" || (sectionPages[item.id] ?? "general") === active)));
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

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const requests: Array<[string, Promise<unknown>]> = [["schema", home.schema()]];
    if (["general", "storage", "network"].includes(active))
      requests.push(["config", settings.config()]);
    if (active === "storage") requests.push(
      ["cache", settings.cache()],
      ["cachePreview", settings.cachePreview()],
    );
    const results = await Promise.allSettled(requests.map((request) => request[1]));
    results.forEach((result, index) => {
      if (result.status !== "fulfilled") return;
      const label = requests[index][0];
      if (label === "schema") schema = result.value as UiSchema;
      if (label === "config") {
        if (dirtyOwners.length) serverChanged = JSON.stringify(config) !== JSON.stringify(result.value);
        else config = result.value as Record<string, unknown>;
      }
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
      const result = await settings.save(updates);
      if (result.requiresRestart) { toast.info("Settings saved. Waiting for restart…"); await waitForRestart(); window.location.reload(); return; }
      markSaved(item.id);
      toast.success(`${item.label} settings saved.`);
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : `${item.label} could not be saved.`);
    } finally {
      action = "";
    }
  }

  async function run(name: string, operation: () => Promise<unknown>, message: string) {
    if (action) return;
    action = name;
    try {
      const result = await operation();
      toast.success("message" in (result as object)
        ? String((result as { message: unknown }).message)
        : message);
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : `${message} failed.`);
    } finally {
      action = "";
    }
  }

  async function testChallengeSolver(event: MouseEvent) {
    const form = (event.currentTarget as HTMLButtonElement).form;
    if (action || !form) return;
    const url = String(new FormData(form).get("CHALLENGE_SOLVER_URL") ?? "");
    action = "challenge-solver-test";
    try {
      const result = await settings.testChallengeSolver(url);
      if (result.ok) toast.success(result.message);
      else toast.error(result.message);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Solver could not be reached.");
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

  function deploymentOwned(field: ConfigSection["fields"][number]) {
    return field.readOnly || field.ownership === "deployment";
  }

  onMount(() => {
    themeMode = readThemeMode();
    const unsubscribeTheme = onThemeModeChange((mode) => { themeMode = mode; });
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
  <section class="settings-workspace settings-layout" aria-busy={refreshing}>
    <nav class="settings-nav" aria-label="Settings sections">
      {#each navGroups as group}
        {#if pages.some(item => item.group === group)}
          <p>{group}</p>
          {#each pages.filter((item) => item.group === group) as item (item.id)}
            {@const Icon = item.icon}
            <a href={`#/settings/${item.id}`} aria-current={active === item.id ? "page" : undefined}>
              <Icon size={18} aria-hidden="true" />{item.label}
            </a>
          {/each}
        {/if}
      {/each}
    </nav>
    <SegmentedNav items={pages.map((item) => ({ id: item.id, label: item.label, href: `#/settings/${item.id}` }))} {active} label="Settings sections" class="route-tabs settings-tabs" />

    <div class="settings-content">
      <header class="settings-page-head">
        <h2>{activePage.title}</h2>
        <p>{activePage.description}</p>
      </header>

      {#if error}
        <div class="degraded-banner" role="status">
          <span aria-hidden="true">!</span><p><strong>Some settings may be out of date.</strong> {error}</p>
          <Button variant="secondary" size="sm" disabled={refreshing} onclick={() => void refresh()}>{refreshing ? "Trying again…" : "Retry"}</Button>
        </div>
      {/if}

      {#if serverChanged}
        <div class="degraded-banner" role="status">
          <p><strong>Someone else changed these settings.</strong> Your unsaved edits are kept.</p>
          <Button variant="secondary" size="sm" onclick={() => { dirtyOwners = []; serverChanged = false; void refresh(); }}>Reload server values</Button>
        </div>
      {/if}

      {#if active === "appearance"}
        <section class="settings-card">
          <div class="settings-row">
            <div><strong>Color theme</strong><small>Match your device, or always use light or dark.</small></div>
            <SelectField bind:value={themeMode} label="Color theme" options={themeOptions} onchange={(value) => saveThemeMode(value as ThemeMode)} />
          </div>
        </section>
      {:else if active === "access"}
        <AccessSettingsCard kind="backend" />
      {:else if active === "listeners" && administrator}
        <ListenersView />
      {:else if active === "alerts"}
        <AlertsView {administrator} />
      {:else if ["general", "storage", "network"].includes(active)}
        {#if administrator && active === "network"}<AccessSettingsCard kind="oidc" />{/if}
        {#if administrator && active === "storage"}<StorageVerificationCard />{/if}
        {#each pageSections as item (item.id)}
          {@const editable = item.fields.filter((field) => !deploymentOwned(field))}
          <form class="settings-card settings-disclosure" aria-label={`${item.label} settings`} oninput={() => markDirty(item.id)} onsubmit={(event) => void saveSection(event, item)}>
            <header><h3>{item.label}</h3></header>
            <div class="settings-fields">
              {#each item.fields as field}
                {#if field.key === "CHALLENGE_SOLVER_URL" && !deploymentOwned(field)}
                  <div class="setting-field settings-row">
                    <div>
                      <label for="challenge-solver-url"><strong>{field.label}</strong></label>
                      {#if field.helpText}<small id="challenge-solver-help">{field.helpText}</small>{/if}
                    </div>
                    <span class="solver-control">
                      <input id="challenge-solver-url" name={field.key} type="text" value={String(fieldValue(config, field))} placeholder={field.placeholder ?? "http://flaresolverr:8191"} aria-describedby={field.helpText ? "challenge-solver-help" : undefined} />
                      <Button type="button" variant="secondary" disabled={Boolean(action)} onclick={(event) => void testChallengeSolver(event)}>{action === "challenge-solver-test" ? "Testing…" : "Test"}</Button>
                    </span>
                  </div>
                {:else if field.valuePath === "playback.startPreference"}
                  <div class="setting-field settings-row playback-start-row" class:read-only={deploymentOwned(field)}>
                    <div>
                      <strong>{field.label}</strong>
                      {#if field.helpText}<small>{field.helpText}</small>{/if}
                      {#if deploymentOwned(field)}<small class="settings-owned"><Lock size={12} aria-hidden="true" />Deployment-owned · change it in your deployment config</small>{/if}
                    </div>
                    {#if deploymentOwned(field)}
                      <output>{playbackStartLabel(fieldValue(config, field) as PlaybackStart)}</output>
                    {:else}
                      <ThreeDetentSwitch name={field.key} label={field.label}
                        value={(fieldValue(config, field) || "Balanced") as PlaybackStart} options={playbackStartOptions}
                        compact disabled={Boolean(action)} onchange={value => {
                          config.playback = { ...(config.playback as Record<string, unknown> | undefined), startPreference: value };
                          markDirty(item.id);
                        }} />
                    {/if}
                  </div>
                {:else}
                <label class="setting-field settings-row" class:read-only={deploymentOwned(field)}>
                  <div>
                    <strong>{field.label}</strong>
                    {#if field.helpText}<small>{field.helpText}</small>{/if}
                    {#if deploymentOwned(field)}<small class="settings-owned"><Lock size={12} aria-hidden="true" />Deployment-owned · change it in your deployment config</small>{/if}
                  </div>
                  {#if deploymentOwned(field)}
                    <output>{String(fieldValue(config, field)) || "Not set"}</output>
                  {:else if field.type === "select"}
                    <SelectField
                      name={field.key}
                      label={field.label}
                      value={String(fieldValue(config, field))}
                      options={field.options ?? []}
                      onchange={() => markDirty(item.id)}
                    />
                  {:else if field.type === "toggle"}
                    <span class="settings-toggle">
                      <Checkbox
                        name={field.key}
                        checked={Boolean(fieldValue(config, field))}
                        onCheckedChange={() => markDirty(item.id)}
                      />
                    </span>
                  {:else}
                    <input
                      name={field.key}
                      type={field.type === "number" ? "number" : "text"}
                      value={String(fieldValue(config, field))}
                      min={field.min ?? undefined}
                      max={field.max ?? undefined}
                    />
                  {/if}
                </label>
                {/if}
              {/each}
              {#if item.id === "cache"}
                <p class="settings-impact">
                  Memory cache is capped at 16 MiB and response files at {cacheDiskCeiling.toLocaleString()} MiB on disk. Audio has its own limit. These times only change how often data is refreshed.
                </p>
              {:else if item.id === "audio-limits"}
                <p class="settings-impact">Limits apply to new work; music already being served can finish. Audio cleanup preserves kept songs, downloaded copies and media-server files.</p>
              {/if}
              {#if editable.length}
                <footer>
                  {#if dirtyOwners.includes(item.id)}<span>Unsaved changes</span>{/if}
                  <Button type="submit" disabled={Boolean(action) || !dirtyOwners.includes(item.id)}>{action === item.id ? "Saving…" : `Save ${item.label}`}</Button>
                </footer>
              {/if}
            </div>
          </form>
        {:else}
          <div class="compact-empty"><strong>Nothing to change here</strong><p>This server doesn't expose any settings in this section.</p></div>
        {/each}
        {#if active === "storage"}
          <CacheDiagnosticsCard
            snapshot={cache}
            preview={cachePreview}
            busy={Boolean(action)}
            onClean={() => void run("cleanup", settings.cleanCache, "Cache cleanup complete.")}
            onPurge={(target) => { purgeTarget = target; purgeOpen = true; }}
          />
        {/if}
      {:else if active === "backups"}
        <BackupsView {administrator} />
      {:else if active === "health" && administrator}
        <HealthView />
      {:else if active === "diagnostics"}
        <section class="settings-card">
          <div class="settings-row maintenance-card">
            <div><strong>Media pipeline</strong><small>Checks metadata, artwork and authenticated playback.</small></div>
            <Button variant="secondary" disabled={Boolean(action)} onclick={() => void run("media", settings.mediaProbe, "Media pipeline checked.")}>{action === "media" ? "Testing…" : "Test media pipeline"}</Button>
          </div>
          <div class="settings-row maintenance-card">
            <div><strong>Playlist readiness</strong><small>Checks source access and which songs listeners can play.</small></div>
            <Button variant="secondary" disabled={Boolean(action)} onclick={() => void run("playlists", settings.playlistProbe, "Playlist pipeline checked.")}>{action === "playlists" ? "Testing…" : "Test playlist readiness"}</Button>
          </div>
        </section>
      {:else if active === "setup"}
        {#if administrator}
          <section class="settings-card">
            <div class="settings-row maintenance-card">
              <div><strong>Setup guide</strong><small>Walk through connecting your media server and accounts again. Nothing is reset.</small></div>
              <Button variant="secondary" onclick={() => void onOpenSetup()}>Open setup guide</Button>
            </div>
          </section>
          {#if onboardingState}<SetupGuide state={onboardingState} onChanged={onSetupChanged} />{/if}
        {/if}
      {/if}
    </div>
  </section>

  <ConfirmDialog
    bind:open={purgeOpen}
    title={purgeTarget === "all" ? "Purge the application cache?" : `Purge ${humanize(purgeTarget)} cache?`}
    description="Disposable metadata and media payloads will be removed. Durable business state, accounts, mappings, playlists, and kept audio are not affected."
    confirmLabel="Purge cache"
    onConfirm={purge}
  />
{/if}

<style>
  .solver-control {
    display: flex;
    min-width: 0;
    gap: var(--space-2);
    align-items: center;
  }

  .solver-control input {
    min-width: 0;
    flex: 1;
  }

  @media (min-width: 651px) {
    .settings-card .setting-field.playback-start-row {
      grid-template-columns: minmax(0, 1fr) minmax(0, 23rem);
    }
  }
</style>
