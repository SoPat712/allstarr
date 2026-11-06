<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { onMount } from "svelte";
  import { toast } from "$lib/toasts";
  import { home, settings, downloads, type DownloadsResponse, type ConfigSection, type UiSchema, type OnboardingState } from "$lib/api";
  import { slideIndicator } from "$lib/slide-indicator";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Activity, Archive, AudioLines, Bell, Gauge, HardDrive, Headphones, Library, Lock, Palette, Rocket, SlidersHorizontal, Users } from "@lucide/svelte";
  import { Button } from "$lib/components/ui/button";
  import SettingsRestartNotice from "$lib/components/SettingsRestartNotice.svelte";
  import SettingsFields from "$lib/components/SettingsFields.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import CacheDiagnosticsCard from "$lib/components/CacheDiagnosticsCard.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import "$lib/components/settings-surface.css";

  let { section = "general", initialPanel = "", administrator, onOpenSetup, onboardingState = null, onSetupChanged = () => {} }: {
    section?: string; initialPanel?: string; administrator: boolean; onOpenSetup: () => void | Promise<void>;
    onboardingState?: OnboardingState | null; onSetupChanged?: (state: OnboardingState) => void;
  } = $props();
  const allPages = [
    { id: "appearance", label: "Appearance", group: "You", icon: Palette, description: "How Allstarr looks on this device." },
    { id: "preferences", label: "Listening preferences", group: "You", icon: Headphones, description: "Your own choices. They save as you change them and never go above the household’s limits." },
    { id: "adding-to-library", label: "Adding to the library", group: "You", icon: Library, description: "Your own process. Saved as you go; the household default applies to listeners without one." },
    { id: "general", label: "General", group: "Server", icon: SlidersHorizontal, description: "Server-wide behavior for every listener. Changes save as you make them." },
    { id: "quality", label: "Quality and routing", group: "Server", icon: Gauge, description: "The highest quality Allstarr plays and downloads, and the order it tries sources. Your own music always comes first." },
    { id: "access", label: "Access and network", group: "Server", icon: Lock, description: "Who can sign in, media-server access, and how Allstarr is reached." },
    { id: "listeners", label: "Listeners", group: "Server", icon: Users, description: "Everyone who uses Allstarr through your media server." },
    { id: "alerts", label: "Alerts", group: "Server", icon: Bell, description: "Problems that need attention, and where to send alerts." },
    { id: "storage", label: "Storage", group: "Server", icon: HardDrive, description: "Saved music, cache space, and playback capacity." },
    { id: "sound-analysis", label: "Sound analysis", group: "Server", icon: AudioLines, description: "Connect AudioMuse to analyze the sound of your music." },
    { id: "backups", label: "Backups", group: "Maintenance", icon: Archive, description: "Save and restore settings, accounts and listening history." },
    { id: "health", label: "Health", group: "Maintenance", icon: Activity, description: "Checks against your media server. Skipped checks don't mean a problem." },
    { id: "setup", label: "Setup guide", group: "Maintenance", icon: Rocket, description: "Walk through setup again. Nothing is reset; progress is saved for your account." },
  ] as const;
  const pages = $derived(allPages.filter(item => administrator || item.id === "alerts" || item.group === "You"));
  const legacy: Record<string, string> = { network: "access", diagnostics: "health", maintenance: "backups", routing: "quality" };
  const requested = $derived(legacy[section] ?? section);
  const active = $derived(pages.some(item => item.id === requested) ? requested : administrator ? "general" : "appearance");
  const activePage = $derived(pages.find(item => item.id === active) ?? pages[0]);
  const href = (item: (typeof allPages)[number]) => `#/settings/${item.id}`;
  let schema = $state<UiSchema | null>(null), config = $state<Record<string, unknown>>({});
  let cache = $state<Awaited<ReturnType<typeof settings.cache>> | null>(null), preview = $state<Awaited<ReturnType<typeof settings.cachePreview>> | null>(null);
  let cachedSongs = $state<DownloadsResponse | null>(null), keptSongs = $state<DownloadsResponse | null>(null);
  let loading = $state(true), refreshing = $state(false), error = $state(""), action = $state("");
  let purgeOpen = $state(false), purgeTarget = $state("");
  let loadedSection = "";
  const generic = $derived(["general", "storage", "access"].includes(active));
  const fields = $derived((schema?.configSections ?? []).filter(item => item.id !== "spotify-import").flatMap(item => item.fields));
  const qualityKeys = new Set(["AUDIO_QUALITY", "MATCHING_LOCAL_PREFERENCE_PERCENT"]);
  const linkKeys = (key: string) => /^(ODESLI_|EXTENSIONS_ALLOW_REMOTE_INSTALL)/.test(key);
  const sectionFor = (id: string) => schema?.configSections?.find(item => item.id === id)?.fields ?? [];
  const group = (id: string, label: string, selected: typeof fields): ConfigSection => ({ id, label, fields: selected });
  const generalGroups = $derived([
    group("library", "Library", [...fields.filter(field => /MusicLibraryIds/.test(field.valuePath ?? "")), ...fields.filter(field => field.key === "BACKEND_TYPE")]),
    group("general", "Defaults for everyone", sectionFor("general").filter(field => !qualityKeys.has(field.key) && !["BACKEND_TYPE", "STORAGE_MODE"].includes(field.key))),
    group("links", "Song links and extensions", fields.filter(field => linkKeys(field.key) && field.key !== "ODESLI_RATE_LIMIT_MS")),
    ...((schema?.configSections ?? []).filter(item => !["operator", "general", "paths", "cache", "audio-limits", "network", "client-address", "spotify-import"].includes(item.id))),
  ].filter(item => item.fields.length));
  const accessFields = $derived(fields.filter(field => /Cors:|ChallengeSolver:/.test(field.valuePath ?? "") || sectionFor("network").includes(field) || field.key === "PUBLIC_LISTENER_URL"));
  const storageAdvanced = $derived(fields.filter(field => /BackupRetention|EventLog/.test(field.valuePath ?? "") || sectionFor("cache").includes(field)).filter(field => field.key !== "CACHE_DURATION_HOURS"));
  const bytes = (value: number) => `${(value / 1024 / 1024).toLocaleString(undefined, { maximumFractionDigits: 1 })} MiB`;
  const audioMaximum = $derived(Number((config.cache as Record<string, unknown> | undefined)?.audioMaximumGiB ?? 2) * 1024 ** 3);
  const cacheAudioBytes = $derived(typeof cachedSongs?.totalSize === "number" ? cachedSongs.totalSize : null);
  const keptAudioBytes = $derived(typeof keptSongs?.totalSize === "number" ? keptSongs.totalSize : null);
  const storageItems = $derived([{ label: cacheAudioBytes === null ? "Cached song usage unavailable" : `${bytes(cacheAudioBytes)} cached of ${bytes(audioMaximum)}` }, { label: keptAudioBytes === null ? "Kept song usage unavailable" : `${bytes(keptAudioBytes)} kept` }, { label: "Database size unavailable" }]);
  const storageSegments = $derived(cacheAudioBytes !== null && keptAudioBytes !== null ? [
    { label: "Cached songs (MiB)", value: Math.round(cacheAudioBytes / 1024 ** 2), tone: "info" as const },
    { label: "Kept songs (MiB)", value: Math.round(keptAudioBytes / 1024 ** 2), tone: "good" as const },
    { label: "Cache room left (MiB)", value: Math.round(Math.max(0, audioMaximum - cacheAudioBytes) / 1024 ** 2), tone: "neutral" as const },
  ] : []);

  async function refresh() {
    if (refreshing) return;
    refreshing = true; error = "";
    const requests: Array<[string, Promise<unknown>]> = [["schema", home.schema()]];
    if (generic) requests.push(["config", settings.config()]);
    if (active === "storage") requests.push(["cache", settings.cache()], ["preview", settings.cachePreview()], ["cachedSongs", downloads.list("cache")], ["keptSongs", downloads.list("kept")]);
    const results = await Promise.allSettled(requests.map(item => item[1]));
    results.forEach((result, index) => {
      if (result.status === "rejected") { error = result.reason instanceof Error ? result.reason.message : "Settings could not be loaded."; return; }
      if (requests[index][0] === "schema") schema = result.value as UiSchema;
      if (requests[index][0] === "config") config = result.value as Record<string, unknown>;
      if (requests[index][0] === "cache") cache = result.value as typeof cache;
      if (requests[index][0] === "preview") preview = result.value as typeof preview;
      if (requests[index][0] === "cachedSongs") cachedSongs = result.value as DownloadsResponse;
      if (requests[index][0] === "keptSongs") keptSongs = result.value as DownloadsResponse;
    });
    loading = false; refreshing = false;
  }
  const scheduler = createRefreshScheduler(refresh);
  $effect(() => { if (loadedSection && loadedSection !== active) void refresh(); loadedSection = active; });
  async function run(name: string, operation: () => Promise<unknown>, message: string) {
    if (action) return;
    action = name;
    try { await operation(); toast.success(message); await refresh(); }
    catch (cause) { toast.error(cause instanceof Error ? cause.message : "This action could not finish. Try again."); }
    finally { action = ""; }
  }
  async function purge() {
    const scope = ["metadata", "media", "all"].includes(purgeTarget) ? purgeTarget as "metadata" | "media" | "all" : null;
    await run("purge", () => scope ? settings.purgeCache(scope) : settings.purgeCacheCategory(purgeTarget), "Cache removed.");
    purgeOpen = false;
  }
  onMount(() => { void refresh(); const unsubscribe = liveUpdates.subscribe(scheduler.schedule); return () => { unsubscribe(); scheduler.cancel(); }; });
</script>

<SettingsRestartNotice />
<section class="settings-workspace settings-layout settings-surface" aria-busy={refreshing}>
  <nav class="settings-nav" aria-label="Settings sections" use:slideIndicator={'a[aria-current="page"]'}>
    {#each ["You", "Server", "Maintenance"] as navGroup}
      {#if pages.some(item => item.group === navGroup)}
        <p>{navGroup}</p>
        {#each pages.filter(item => item.group === navGroup) as item (item.id)}
          {@const Icon = item.icon}<a href={href(item)} aria-current={active === item.id ? "page" : undefined}><Icon size={18} aria-hidden="true" />{item.label}</a>
        {/each}
      {/if}
    {/each}
  </nav>
  <SegmentedNav variant="tabs" items={pages.map(item => ({ id: item.id, label: item.label, href: href(item) }))} {active} label="Settings sections" class="route-tabs settings-tabs" />
  <div class="settings-content">
    <header class="settings-page-head"><h2>{activePage.label}</h2><p>{activePage.description}</p></header>
    {#if loading}<RowsSkeleton count={5} />
    {:else if !schema || error}<ErrorState title="Some settings could not be loaded" detail={error} onretry={refresh} />{/if}
    {#if !loading && schema}
      {#if ["general", "access", "storage", "setup"].includes(active)}
        <div class:setup-progress={active === "setup"}><PageStatus segments={active === "storage" ? storageSegments : active === "setup" ? [{ label: "Reviewed", value: onboardingState?.steps?.filter(step => step.state !== "pending").length ?? 0, tone: "info" }, { label: "Left", value: onboardingState?.steps?.filter(step => step.state === "pending").length ?? 0, tone: "neutral" }] : []} items={active === "storage" ? storageItems : active === "setup" ? [{ label: `${onboardingState?.steps?.filter(step => step.state !== "pending").length ?? 0} of ${countLabel(onboardingState?.steps?.length ?? 0, "step")} reviewed or skipped` }, { label: "Playback works while setup is open" }] : [{ label: "Changes save as you make them" }, { label: "Restart changes ask first" }]} /></div>
      {/if}
      {#if active === "appearance"}
        {#await import("./SettingsAppearance.svelte")}<RowsSkeleton count={1} />{:then { default: Appearance }}<Appearance />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "adding-to-library"}
        {#await import("./LibraryPromotionSettings.svelte")}<RowsSkeleton count={4} />{:then { default: Promotion }}<Promotion {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "preferences"}
        {#await import("./ListeningPreferencesView.svelte")}<RowsSkeleton />{:then { default: Preferences }}<Preferences {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "quality"}
        {#await import("./RoutingView.svelte")}<RowsSkeleton />{:then { default: Routing }}<Routing embedded />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "listeners"}
        {#await import("./ListenersView.svelte")}<RowsSkeleton />{:then { default: Listeners }}<Listeners {initialPanel} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "alerts"}
        {#await import("./AlertsView.svelte")}<RowsSkeleton />{:then { default: Alerts }}<Alerts {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "sound-analysis"}
        {#await import("./SoundAnalysisSettings.svelte")}<RowsSkeleton count={2} />{:then { default: Sound }}<Sound />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "backups"}
        {#await import("./BackupsView.svelte")}<RowsSkeleton />{:then { default: Backups }}<Backups {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "health"}
        {#await import("./HealthView.svelte")}<RowsSkeleton />{:then { default: Health }}<Health {initialPanel} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        <details class="settings-fold"><summary>Advanced: media and playlist tests</summary><section class="settings-card" aria-label="Diagnostics">
          <div class="settings-row"><div><strong>Media pipeline</strong><small>Checks song details, artwork and playback.</small></div><Button variant="outline" disabled={Boolean(action)} onclick={() => run("media", settings.mediaProbe, "Media pipeline checked.")}>Test media pipeline</Button></div>
          <div class="settings-row"><div><strong>Playlist readiness</strong><small>Checks source access and which songs listeners can play.</small></div><Button variant="outline" disabled={Boolean(action)} onclick={() => run("playlists", settings.playlistProbe, "Playlist pipeline checked.")}>Test playlist readiness</Button></div>
        </section></details>
      {:else if active === "setup"}
        {#await import("./SetupGuide.svelte")}<RowsSkeleton />{:then { default: Guide }}
          {#if onboardingState}<Guide state={onboardingState} onChanged={onSetupChanged} embedded={onboardingState.completed} />{:else}<Button variant="outline" onclick={() => onOpenSetup()}>Open setup guide</Button>{/if}
        {:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "general"}
        {#each generalGroups as item (item.id)}<SettingsFields section={item} {config} onChanged={refresh} />{/each}
        {#if fields.some(field => field.key === "ODESLI_RATE_LIMIT_MS")}<details class="settings-fold"><summary>Advanced: wait between song.link lookups</summary><SettingsFields section={group("link-timing", "Song link timing", fields.filter(field => field.key === "ODESLI_RATE_LIMIT_MS"))} {config} onChanged={refresh} /></details>{/if}
        <p class="settings-muted">Allstarr · beta {__APP_VERSION__}</p>
      {:else if active === "access"}
        {#await import("./AccessSettingsCard.svelte")}<RowsSkeleton count={2} />{:then { default: Access }}<Access kind="backend" /><Access kind="oidc" />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if accessFields.some(field => !/Cors:/.test(field.valuePath ?? ""))}<SettingsFields section={group("network", "Addresses and network", accessFields.filter(field => !/Cors:/.test(field.valuePath ?? "")))} {config} onChanged={refresh} />{/if}
        <details class="settings-fold"><summary>Advanced: browser access (origins, methods, headers)</summary><SettingsFields section={group("browser-access", "Browser access", accessFields.filter(field => /Cors:/.test(field.valuePath ?? "")))} {config} onChanged={refresh} /></details>
      {:else if active === "storage"}
        {#if fields.some(field => ["STORAGE_MODE", "CACHE_DURATION_HOURS", "CACHE_AUDIO_MAXIMUM_GIB"].includes(field.key))}<SettingsFields section={group("track-cache", "Track cache", fields.filter(field => ["STORAGE_MODE", "CACHE_DURATION_HOURS", "CACHE_AUDIO_MAXIMUM_GIB"].includes(field.key)))} {config} onChanged={refresh} />{/if}
        {#if sectionFor("audio-limits").some(field => field.key !== "CACHE_AUDIO_MAXIMUM_GIB")}<SettingsFields section={group("audio-limits", "Audio limits", sectionFor("audio-limits").filter(field => field.key !== "CACHE_AUDIO_MAXIMUM_GIB"))} {config} onChanged={refresh} />{/if}
        {#if sectionFor("paths").length}<SettingsFields section={group("paths", "Folders", sectionFor("paths"))} {config} onChanged={refresh} />{/if}
        <details class="settings-fold"><summary>Advanced: activity retention, cache durations, saved-file verification</summary>
          {#if storageAdvanced.length}<SettingsFields section={group("storage-advanced", "Retention and cache", storageAdvanced)} {config} onChanged={refresh} />{/if}
          {#await import("./StorageVerificationCard.svelte")}<RowsSkeleton count={1} />{:then { default: Verification }}<Verification />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
          <CacheDiagnosticsCard snapshot={cache} {preview} busy={Boolean(action)} onClean={() => run("clean", settings.cleanCache, "Cache cleanup complete.")} onPurge={target => { purgeTarget = target; purgeOpen = true; }} />
        </details>
      {/if}
    {/if}
  </div>
</section>
<ConfirmDialog bind:open={purgeOpen} title="Remove cached files?" description="Temporary cached files will be removed. Kept songs, downloads and media-server files stay safe." confirmLabel="Remove cache" onConfirm={purge} />

<style>
  .setup-progress :global(.status-legend) { display: none; }
  .setup-progress :global(.status-bar [data-tone="neutral"]) { background: var(--color-panel-raised); }
</style>
