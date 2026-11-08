<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { countLabel } from "$lib/count-label";
  import { storageSegments as measuredStorageSegments } from "$lib/storage-usage";
  import { onMount } from "svelte";
  import { toast } from "$lib/toasts";
  import { home, settings, downloads, type DownloadsResponse, type ConfigSection, type UiSchema, type OnboardingState } from "$lib/api";
  import { slideIndicator } from "$lib/slide-indicator";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Activity, Archive, AudioLines, Bell, CirclePlay, HardDrive, History, Library, LibraryBig, Palette, Rocket, Users } from "@lucide/svelte";
  import { Button } from "$lib/components/ui/button";
  import SettingsRestartNotice from "$lib/components/SettingsRestartNotice.svelte";
  import SettingsFields from "$lib/components/SettingsFields.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import CacheDiagnosticsCard from "$lib/components/CacheDiagnosticsCard.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import { searchSettings, settingPage } from "$lib/settings-search";
  import { settingsStatus } from "$lib/settings-status";
  import { tick } from "svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import "$lib/components/settings-surface.css";

  let { section = "general", initialPanel = "", administrator, onOpenSetup, onboardingState = null, onSetupChanged = () => {} }: {
    section?: string; initialPanel?: string; administrator: boolean; onOpenSetup: () => void | Promise<void>;
    onboardingState?: OnboardingState | null; onSetupChanged?: (state: OnboardingState) => void;
  } = $props();
  const allPages = [
    { id: "appearance", label: "Appearance", group: "You", icon: Palette, listener: true, description: "How Allstarr looks on this device." },
    { id: "playback", label: "Playback", group: "Listening", icon: CirclePlay, listener: true, description: "What plays, at what quality and from where. Changes save as you make them." },
    { id: "library", label: "Library", group: "Listening", icon: Library, listener: false, description: "Which libraries Allstarr reads, and where it keeps files." },
    { id: "adding-to-library", label: "Adding to the library", group: "Listening", icon: LibraryBig, listener: true, description: "Your own process. Saved as you go; the household default applies to listeners without one." },
    { id: "discovery", label: "Mixes and discovery", group: "Listening", icon: AudioLines, listener: false, description: "Where mixes, radio and similar songs come from." },
    { id: "history", label: "History and scrobbling", group: "Listening", icon: History, listener: false, description: "What Allstarr remembers and sends to Last.fm and ListenBrainz." },
    { id: "people", label: "People and access", group: "Server", icon: Users, listener: false, description: "Who uses Allstarr, what they can do and how they sign in." },
    { id: "storage", label: "Storage", group: "Server", icon: HardDrive, listener: false, description: "Space for cached songs and how much Allstarr does at once." },
    { id: "alerts", label: "Alerts", group: "Server", icon: Bell, listener: true, description: "Problems that need attention, and where to send alerts." },
    { id: "backups", label: "Backups", group: "Maintenance", icon: Archive, listener: false, description: "Save and restore settings, accounts and listening history." },
    { id: "health", label: "Health", group: "Maintenance", icon: Activity, listener: false, description: "Checks against your media server. Skipped checks don't mean a problem." },
    { id: "setup", label: "Setup guide", group: "Maintenance", icon: Rocket, listener: false, description: "Walk through setup again. Nothing is reset; progress is saved for your account." },
  ] as const;
  const pages = $derived(allPages.filter(item => administrator || item.listener));
  // Earlier addresses keep working and open the page that holds those settings now.
  const legacy: Record<string, string> = {
    general: "playback", preferences: "playback", quality: "playback", routing: "playback", access: "people", network: "people",
    listeners: "people", "sound-analysis": "discovery", diagnostics: "health", maintenance: "backups",
  };
  const requested = $derived(legacy[section] ?? section);
  const active = $derived(pages.some(item => item.id === requested) ? requested : "playback");
  const activePage = $derived(pages.find(item => item.id === active) ?? pages[0]);
  const href = (item: (typeof allPages)[number]) => `#/settings/${item.id}`;
  let schema = $state<UiSchema | null>(null), config = $state<Record<string, unknown>>({});
  let cache = $state<Awaited<ReturnType<typeof settings.cache>> | null>(null), preview = $state<Awaited<ReturnType<typeof settings.cachePreview>> | null>(null);
  let cachedSongs = $state<DownloadsResponse | null>(null), keptSongs = $state<DownloadsResponse | null>(null);
  let databaseBytes = $state<number | null>(null);
  let loading = $state(true), refreshing = $state(false), error = $state(""), action = $state("");
  let purgeOpen = $state(false), purgeTarget = $state("");
  let loadedSection = "";
  const generic = $derived(["playback", "library", "discovery", "history", "people", "storage", "health"].includes(active));
  const fields = $derived((schema?.configSections ?? []).filter(item => item.id !== "spotify-import").flatMap(item => item.fields));
  const sectionFor = (id: string) => schema?.configSections?.find(item => item.id === id)?.fields ?? [];
  const group = (id: string, label: string, selected: typeof fields): ConfigSection => ({ id, label, fields: selected });
  const pick = (...keys: string[]) => keys.flatMap(key => fields.filter(field => field.key === key));
  const pickPath = (pattern: RegExp) => fields.filter(field => pattern.test(field.valuePath ?? ""));
  const playbackDefaults = $derived(group("playback-defaults", "Defaults for everyone", pick("PLAYBACK_START_PREFERENCE", "PLAYBACK_PREPARE_UPCOMING_SONGS", "EXPLICIT_FILTER")));
  const libraryGroups = $derived([
    group("libraries", "Libraries Allstarr reads", [...pick("BACKEND_TYPE"), ...pickPath(/MusicLibraryIds/)]),
    group("downloads", "Downloads", pick("DOWNLOAD_MODE", "STORAGE_MODE")),
    group("paths", "Folders", sectionFor("paths")),
  ].filter(item => item.fields.length));
  const similarSongs = $derived(group("similar-songs", "When a service has no similar songs", pick("PLAYBACK_LASTFM_SIMILAR_ENABLED", "PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED")));
  const scrobbling = $derived(group("scrobbling", "Scrobbling for everyone", pick("SCROBBLING_LOCAL_TRACKS_ENABLED")));
  const activityHistory = $derived(group("activity-history", "Activity history", pick("EVENT_LOG_RETENTION_DAYS", "EVENT_LOG_MAXIMUM_ROWS")));
  const activityAdvanced = $derived(group("activity-advanced", "Cleanup timing and job history", pick("EVENT_LOG_CLEANUP_INTERVAL_MINUTES", "EVENT_LOG_CLEANUP_BATCH_SIZE", "EVENT_LOG_JOB_HISTORY_RETENTION_DAYS")));
  const peopleGroups = $derived([
    group("listener-rights", "What listeners can do", pick("LISTENERS_CAN_CONNECT_OWN_ACCOUNTS")),
    group("apps", "Apps", pick("CLIENT_API_ENABLED", "PUBLIC_LISTENER_URL")),
  ].filter(item => item.fields.length));
  const networkFields = $derived(pick("ADMIN_BIND_ANY_IP", "ADMIN_TRUSTED_SUBNETS"));
  const browserFields = $derived(pickPath(/Cors:/));
  const diagnosticsFields = $derived(pick("DEBUG_LOG_ALL_REQUESTS"));
  // Settings no page claims yet still show, so nothing new is ever hidden.
  const unplaced = $derived(group("more", "More", fields.filter(field => settingPage(field.key) === null)));
  const storageAdvanced = $derived(fields.filter(field => /BackupRetention/.test(field.valuePath ?? "") || sectionFor("cache").includes(field)).filter(field => field.key !== "CACHE_DURATION_HOURS"));
  const bytes = (value: number) => `${(value / 1024 / 1024).toLocaleString(undefined, { maximumFractionDigits: 1 })} MB`;
  const audioMaximum = $derived(Number((config.cache as Record<string, unknown> | undefined)?.audioMaximumGiB ?? 2) * 1024 ** 3);
  const cacheAudioBytes = $derived(typeof cachedSongs?.totalSize === "number" ? cachedSongs.totalSize : null);
  const keptAudioBytes = $derived(typeof keptSongs?.totalSize === "number" ? keptSongs.totalSize : null);
  const storageItems = $derived([{ label: cacheAudioBytes === null ? "Cached song usage unavailable" : `${bytes(cacheAudioBytes)} cached of ${bytes(audioMaximum)}` }, { label: keptAudioBytes === null ? "Kept song usage unavailable" : `${bytes(keptAudioBytes)} kept` }, ...(databaseBytes === null ? [] : [{ label: `${bytes(databaseBytes)} database` }])]);
  const storageSegments = $derived(measuredStorageSegments(cacheAudioBytes, keptAudioBytes, audioMaximum, databaseBytes));

  const statusItems = $derived(administrator ? settingsStatus(active, config, fields) : []);

  let findQuery = $state("");
  const pageLabels = $derived(Object.fromEntries(pages.map(item => [item.id, item.label])));
  const findMatches = $derived(searchSettings(findQuery, fields, pageLabels));
  // A setting opened from Find a setting scrolls into view and glows once.
  async function revealSetting() {
    const key = new URLSearchParams(window.location.hash.split("?", 2)[1] ?? "").get("setting");
    if (!key) return;
    await tick();
    const row = document.querySelector<HTMLElement>(`[data-setting="${CSS.escape(key)}"]`);
    if (!row) return;
    row.scrollIntoView({ block: "center", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
    row.classList.remove("setting-found"); void row.offsetWidth; row.classList.add("setting-found");
  }
  $effect(() => { if (!loading && schema && config) void revealSetting(); });

  async function refresh() {
    if (refreshing) return;
    refreshing = true; error = "";
    const requests: Array<[string, Promise<unknown>]> = [["schema", home.schema()]];
    if (generic) requests.push(["config", settings.config()]);
    if (active === "storage") requests.push(["cache", settings.cache()], ["preview", settings.cachePreview()], ["cachedSongs", downloads.list("cache")], ["keptSongs", downloads.list("kept")], ["database", settings.storage().then(value => value.databaseBytes ?? null).catch(() => null)]);
    const results = await Promise.allSettled(requests.map(item => item[1]));
    results.forEach((result, index) => {
      if (result.status === "rejected") { error = result.reason instanceof Error ? result.reason.message : "Settings could not be loaded."; return; }
      if (requests[index][0] === "schema") schema = result.value as UiSchema;
      if (requests[index][0] === "config") config = result.value as Record<string, unknown>;
      if (requests[index][0] === "cache") cache = result.value as typeof cache;
      if (requests[index][0] === "preview") preview = result.value as typeof preview;
      if (requests[index][0] === "cachedSongs") cachedSongs = result.value as DownloadsResponse;
      if (requests[index][0] === "keptSongs") keptSongs = result.value as DownloadsResponse;
      if (requests[index][0] === "database") databaseBytes = result.value as number | null;
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

{#snippet findSetting()}
  <div class="settings-find" role="search">
    <input type="search" placeholder="Find a setting" aria-label="Find a setting" bind:value={findQuery} onkeydown={event => { if (event.key === "Escape") findQuery = ""; }} />
    {#if findQuery.trim()}
      <ul class="settings-find-results" aria-label="Matching settings">
        {#each findMatches as match (match.key)}<li><a href={match.href} onclick={() => { findQuery = ""; setTimeout(() => void revealSetting(), 60); }}><strong>{match.label}</strong><small>{match.detail}</small></a></li>
        {:else}<li class="settings-find-empty">No setting matches “{findQuery.trim()}”</li>{/each}
      </ul>
    {/if}
  </div>
{/snippet}

<SettingsRestartNotice />
<section class="settings-workspace settings-layout settings-surface" aria-busy={refreshing}>
  <nav class="settings-nav" aria-label="Settings sections" use:slideIndicator={'a[aria-current="page"]'}>
    {@render findSetting()}
    {#each ["You", "Listening", "Server", "Maintenance"] as navGroup}
      {#if pages.some(item => item.group === navGroup)}
        <p>{navGroup}</p>
        {#each pages.filter(item => item.group === navGroup) as item (item.id)}
          {@const Icon = item.icon}<a href={href(item)} aria-current={active === item.id ? "page" : undefined}><Icon size={18} aria-hidden="true" />{item.label}</a>
        {/each}
      {/if}
    {/each}
    <small class="settings-version">Allstarr · beta {__APP_VERSION__}</small>
  </nav>
  <SegmentedNav variant="tabs" items={pages.map(item => ({ id: item.id, label: item.label, href: href(item) }))} {active} label="Settings sections" class="route-tabs settings-tabs" />
  {#key active}<div class="settings-content settings-page-enter">
    <div class="settings-find-phone">{@render findSetting()}</div>
    <header class="settings-page-head"><h2>{activePage.label}</h2><p>{activePage.description}</p></header>
    {#if loading}<RowsSkeleton count={5} />
    {:else if !schema || error}<ErrorState title="Some settings could not be loaded" detail={error} onretry={refresh} />{/if}
    {#if !loading && schema}
      {#if statusItems.length && !["storage", "setup"].includes(active)}<PageStatus items={statusItems} />
      {:else if ["storage", "setup"].includes(active)}
        <div class:setup-progress={active === "setup"}><PageStatus segments={active === "storage" ? storageSegments : active === "setup" ? [{ label: "Reviewed", value: onboardingState?.steps?.filter(step => step.state !== "pending").length ?? 0, tone: "info" }, { label: "Left", value: onboardingState?.steps?.filter(step => step.state === "pending").length ?? 0, tone: "neutral" }] : []} items={active === "storage" ? storageItems : active === "setup" ? [{ label: `${onboardingState?.steps?.filter(step => step.state !== "pending").length ?? 0} of ${countLabel(onboardingState?.steps?.length ?? 0, "step")} reviewed or skipped` }, { label: "Playback works while setup is open" }] : []} /></div>
      {/if}
      {#if active === "appearance"}
        {#await import("./SettingsAppearance.svelte")}<RowsSkeleton count={1} />{:then { default: Appearance }}<Appearance />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "adding-to-library"}
        {#await import("./LibraryPromotionSettings.svelte")}<RowsSkeleton count={4} />{:then { default: Promotion }}<Promotion {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "playback"}
        {#await import("./ListeningPreferencesView.svelte")}<RowsSkeleton />{:then { default: Preferences }}<Preferences {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if administrator}
          {#if playbackDefaults.fields.length}<SettingsFields section={playbackDefaults} {config} onChanged={refresh} />{/if}
          {#await import("./RoutingView.svelte")}<RowsSkeleton />{:then { default: Routing }}<Routing embedded />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
          {#if unplaced.fields.length}<SettingsFields section={unplaced} {config} onChanged={refresh} />{/if}
        {/if}
      {:else if active === "library"}
        {#each libraryGroups as item (item.id)}<SettingsFields section={item} {config} onChanged={refresh} />{/each}
      {:else if active === "history"}
        <section class="settings-card" aria-label="Scrobbling accounts"><header><h3>Scrobbling accounts</h3></header>
          <div class="settings-row"><div><strong>Last.fm and ListenBrainz</strong><small>Each listener connects their own accounts. Songs streamed through Allstarr are sent there.</small></div><Button variant="outline" href="#/integrations/accounts">Open accounts</Button></div>
        </section>
        {#if scrobbling.fields.length}<SettingsFields section={scrobbling} {config} onChanged={refresh} />{/if}
        {#if activityHistory.fields.length}<SettingsFields section={activityHistory} {config} onChanged={refresh}>
          {#snippet footer()}{#if activityAdvanced.fields.length}<AdvancedFold label="Advanced: cleanup timing and job history"><SettingsFields embedded section={activityAdvanced} {config} onChanged={refresh} /></AdvancedFold>{/if}{/snippet}
        </SettingsFields>{/if}
      {:else if active === "people"}
        {#await import("./ListenersView.svelte")}<RowsSkeleton />{:then { default: Listeners }}<Listeners {initialPanel} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#each peopleGroups as item (item.id)}<SettingsFields section={item} {config} onChanged={refresh} />{/each}
        {#await import("./AccessSettingsCard.svelte")}<RowsSkeleton count={2} />{:then { default: Access }}<Access kind="backend" /><Access kind="oidc" />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if networkFields.length || browserFields.length}<SettingsFields section={group("network", "Network", networkFields)} {config} onChanged={refresh}>
          {#snippet footer()}{#if browserFields.length}<AdvancedFold label="Advanced: browser origins, methods and headers"><SettingsFields embedded section={group("browser-access", "Browser access", browserFields)} {config} onChanged={refresh} /></AdvancedFold>{/if}{/snippet}
        </SettingsFields>{/if}
      {:else if active === "alerts"}
        {#await import("./AlertsView.svelte")}<RowsSkeleton />{:then { default: Alerts }}<Alerts {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "discovery"}
        {#await import("./SoundAnalysisSettings.svelte")}<RowsSkeleton count={2} />{:then { default: Sound }}<Sound />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if similarSongs.fields.length}<SettingsFields section={similarSongs} {config} onChanged={refresh} />{/if}
      {:else if active === "backups"}
        {#await import("./BackupsView.svelte")}<RowsSkeleton />{:then { default: Backups }}<Backups {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "health"}
        {#await import("./HealthView.svelte")}<RowsSkeleton />{:then { default: Health }}<Health {initialPanel} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        <AdvancedFold label="Advanced: diagnostics" description="media and playlist tests, request logging">{#if diagnosticsFields.length}<SettingsFields embedded section={group("diagnostics", "Request logging", diagnosticsFields)} {config} onChanged={refresh} />{/if}<section class="settings-card" aria-label="Diagnostics">
          <div class="settings-row"><div><strong>Media pipeline</strong><small>Checks song details, artwork and playback.</small></div><Button variant="outline" disabled={Boolean(action)} onclick={() => run("media", settings.mediaProbe, "Media pipeline checked.")}>Test media pipeline</Button></div>
          <div class="settings-row"><div><strong>Playlist readiness</strong><small>Checks source access and which songs listeners can play.</small></div><Button variant="outline" disabled={Boolean(action)} onclick={() => run("playlists", settings.playlistProbe, "Playlist pipeline checked.")}>Test playlist readiness</Button></div>
        </section></AdvancedFold>
      {:else if active === "setup"}
        {#await import("./SetupGuide.svelte")}<RowsSkeleton />{:then { default: Guide }}
          {#if onboardingState}<Guide state={onboardingState} onChanged={onSetupChanged} embedded={onboardingState.completed} />{:else}<Button variant="outline" onclick={() => onOpenSetup()}>Open setup guide</Button>{/if}
        {:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "storage"}
        {#if fields.some(field => ["STORAGE_MODE", "CACHE_DURATION_HOURS", "CACHE_AUDIO_MAXIMUM_GIB"].includes(field.key))}<SettingsFields section={group("track-cache", "Track cache", fields.filter(field => ["STORAGE_MODE", "CACHE_DURATION_HOURS", "CACHE_AUDIO_MAXIMUM_GIB"].includes(field.key)))} {config} onChanged={refresh} />{/if}
        {#if sectionFor("audio-limits").some(field => field.key !== "CACHE_AUDIO_MAXIMUM_GIB")}<SettingsFields section={group("audio-limits", "Audio limits", sectionFor("audio-limits").filter(field => field.key !== "CACHE_AUDIO_MAXIMUM_GIB"))} {config} onChanged={refresh} />{/if}
        <AdvancedFold label="Advanced: cache times, file checks and cleanup">
          {#if storageAdvanced.length}<SettingsFields section={group("storage-advanced", "Retention and cache", storageAdvanced)} {config} onChanged={refresh} />{/if}
          {#await import("./StorageVerificationCard.svelte")}<RowsSkeleton count={1} />{:then { default: Verification }}<Verification />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
          <CacheDiagnosticsCard snapshot={cache} {preview} busy={Boolean(action)} onClean={() => run("clean", settings.cleanCache, "Cache cleanup complete.")} onPurge={target => { purgeTarget = target; purgeOpen = true; }} />
        </AdvancedFold>
      {/if}
    {/if}
  </div>{/key}
</section>
<ConfirmDialog bind:open={purgeOpen} title="Remove cached files?" description="Temporary cached files will be removed. Kept songs, downloads and media-server files stay safe." confirmLabel="Remove cache" onConfirm={purge} />

<style>
  .settings-page-enter { animation: settings-page-in 260ms var(--ease-emphasized); }
  @keyframes settings-page-in { from { opacity: 0; transform: translateY(6px); } }
  .settings-find { position: relative; margin-bottom: var(--space-3); }
  .settings-find input { width: 100%; min-height: 38px; border: 1px solid var(--color-edge-strong); border-radius: 999px; padding: 0 var(--space-4); background: transparent; color: var(--color-ink); font-size: var(--text-sm); transition: border-color var(--motion-standard); }
  .settings-find input:focus-visible { outline: none; border-color: var(--color-signal); }
  .settings-find-results { position: absolute; z-index: 20; top: calc(100% + 6px); left: 0; width: max(100%, 18rem); margin: 0; padding: var(--space-1); list-style: none; border: 1px solid var(--color-edge); border-radius: 14px; background: var(--color-panel-raised, var(--color-panel)); box-shadow: 0 12px 32px color-mix(in srgb, black 30%, transparent); animation: find-in 160ms var(--ease-emphasized); }
  .settings-find-results a { display: grid; gap: 2px; padding: var(--space-2) var(--space-3); border-radius: 10px; color: inherit; text-decoration: none; }
  .settings-find-results a:hover, .settings-find-results a:focus-visible { background: var(--row-hover); }
  .settings-find-results strong { font-weight: 500; font-size: var(--text-sm); }
  .settings-find-results small, .settings-find-empty { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .settings-find-empty { padding: var(--space-2) var(--space-3); }
  .settings-find-phone { display: none; }
  @media (max-width: 900px) { .settings-find-phone { display: block; } }
  @keyframes find-in { from { opacity: 0; transform: translateY(-4px); } }
  :global(.setting-found) { animation: setting-found 1.8s var(--ease-standard); }
  @keyframes setting-found { 0%, 35% { background: color-mix(in srgb, var(--color-signal) 18%, transparent); } }
  @media (prefers-reduced-motion: reduce) { .settings-find-results, :global(.setting-found), .settings-page-enter { animation: none; } }
  .settings-version { display: block; margin-top: var(--space-4); padding-inline: var(--space-3); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .setup-progress :global(.status-legend) { display: none; }
  .setup-progress :global(.status-bar [data-tone="neutral"]) { background: var(--color-panel-raised); }
</style>
