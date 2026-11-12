<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { countLabel } from "$lib/count-label";
  import { storageSegments as measuredStorageSegments } from "$lib/storage-usage";
  import { onMount } from "svelte";
  import { toast } from "$lib/toasts";
  import { home, settings, downloads, sources, type DownloadsResponse, type ConfigSection, type ProviderScore, type UiSchema, type OnboardingState } from "$lib/api";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { Archive, ArrowLeft, AudioLines, Bell, ChevronRight, HardDrive, Headphones, History, Library, LibraryBig, ListOrdered, Palette, SlidersHorizontal, Smartphone, UserRound, Users } from "@lucide/svelte";
  import { Button } from "$lib/components/ui/button";
  import SettingsRestartNotice from "$lib/components/SettingsRestartNotice.svelte";
  import SettingsFields from "$lib/components/SettingsFields.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import CacheDiagnosticsCard from "$lib/components/CacheDiagnosticsCard.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import { searchSettings, settingPage } from "$lib/settings-search";
  import { settingsSummary } from "$lib/settings-status";
  import { tick } from "svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import "$lib/components/settings-surface.css";

  let { section = "", initialPanel = "", administrator, onOpenSetup, onboardingState = null, onSetupChanged = () => {} }: {
    section?: string; initialPanel?: string; administrator: boolean; onOpenSetup: () => void | Promise<void>;
    onboardingState?: OnboardingState | null; onSetupChanged?: (state: OnboardingState) => void;
  } = $props();
  // Material 3 list-detail: one list of plain categories, each saying what's set, and one page per category.
  const allPages = {
    listening: { label: "Listening", icon: UserRound, scope: "you", description: "Only you. Everyone else keeps the household choices." },
    appearance: { label: "Appearance", icon: Palette, scope: "you", description: "How Allstarr looks on this device. Other devices and other people keep their own." },
    adding: { label: "Adding to the library", icon: LibraryBig, scope: "you", description: "Your own way of saving songs to the library. The household's applies until you change it." },
    quality: { label: "Sound quality", icon: Headphones, scope: "everyone", description: "How good everything sounds, and how fast songs start. Listeners can choose less for themselves, never more." },
    sources: { label: "Music sources", icon: ListOrdered, scope: "everyone", description: "Which services Allstarr asks, in what order, for each job." },
    library: { label: "Library", icon: Library, scope: "everyone", description: "What Allstarr reads from your media server, and how songs get added to it." },
    apps: { label: "Apps", icon: Smartphone, scope: "everyone", description: "Music apps signed in with a music account: Songbird, Feishin, Finamp and others." },
    mixes: { label: "Mixes and discovery", icon: AudioLines, scope: "everyone", description: "Where mixes, radio and similar songs come from." },
    history: { label: "History and scrobbling", icon: History, scope: "everyone", description: "What Allstarr remembers about plays, and what it sends to Last.fm and ListenBrainz." },
    people: { label: "People and sign-in", icon: Users, scope: "everyone", description: "Who uses Allstarr, what they can do, and how they sign in." },
    alerts: { label: "Alerts", icon: Bell, scope: "everyone", description: "Problems that need attention now, and where Allstarr tells you about new ones." },
    storage: { label: "Storage and speed", icon: HardDrive, scope: "everyone", description: "Disk space for songs played from services, and how much Allstarr does at once." },
    backups: { label: "Backups and health", icon: Archive, scope: "everyone", description: "Copies of everything Allstarr knows, and regular checks that it still works." },
    advanced: { label: "Advanced", icon: SlidersHorizontal, scope: "everyone", description: "Rarely changed. Settings marked \"Restarts Allstarr\" restart it when saved." },
    setup: { label: "Setup guide", icon: Archive, scope: "everyone", description: "Walk through setup again. Nothing is reset; progress is saved for your account." },
  } as const;
  type PageId = keyof typeof allPages;
  const groups = $derived<{ label: string; ids: PageId[] }[]>(administrator
    ? [{ label: "You", ids: ["listening", "appearance"] },
       { label: "Everyone", ids: ["quality", "sources", "library", "apps", "mixes", "history", "people", "alerts"] },
       { label: "This server", ids: ["storage", "backups", "advanced"] }]
    : [{ label: "You", ids: ["listening", "appearance", "adding", "alerts"] }]);
  // The setup guide has no row of its own; Backups and health links to it, and first-run setup opens it.
  const pageIds = $derived<PageId[]>([...groups.flatMap(item => item.ids), ...(administrator ? ["setup" as const] : [])]);
  // Earlier addresses keep working and open the page that holds those settings now.
  const legacy = $derived<Record<string, PageId>>({
    general: administrator ? "quality" : "listening", playback: administrator ? "quality" : "listening", preferences: "listening",
    routing: "sources", access: "people", network: "people", listeners: "people", "sound-analysis": "mixes", discovery: "mixes",
    diagnostics: "backups", maintenance: "backups", health: "backups", "adding-to-library": administrator ? "library" : "adding",
  });
  const requested = $derived(legacy[section] ?? section);
  const listOnly = $derived(!section);
  const active = $derived<PageId>(pageIds.includes(requested as PageId) ? requested as PageId : administrator ? "quality" : "listening");
  const activePage = $derived(allPages[active]);
  const href = (id: string) => `#/settings/${id}`;
  let schema = $state<UiSchema | null>(null), config = $state<Record<string, unknown>>({});
  let cache = $state<Awaited<ReturnType<typeof settings.cache>> | null>(null), preview = $state<Awaited<ReturnType<typeof settings.cachePreview>> | null>(null);
  let cachedSongs = $state<DownloadsResponse | null>(null), keptSongs = $state<DownloadsResponse | null>(null);
  let databaseBytes = $state<number | null>(null);
  let loading = $state(true), refreshing = $state(false), error = $state(""), action = $state("");
  let purgeOpen = $state(false), purgeTarget = $state("");
  let addingOpen = $state(false), sendsOpen = $state(false);
  let scores = $state<ProviderScore[]>([]);
  $effect(() => { if (section === "adding-to-library" && administrator) addingOpen = true; });
  let loadedSection = "";
  const fields = $derived((schema?.configSections ?? []).flatMap(item => item.fields));
  const sectionFor = (id: string) => schema?.configSections?.find(item => item.id === id)?.fields ?? [];
  const group = (id: string, label: string, selected: typeof fields): ConfigSection => ({ id, label, fields: selected });
  const pick = (...keys: string[]) => keys.flatMap(key => fields.filter(field => field.key === key));
  const pickPath = (pattern: RegExp) => fields.filter(field => pattern.test(field.valuePath ?? ""));
  const qualityGroups = $derived([
    group("audio-quality", "Quality", pick("AUDIO_QUALITY")),
    group("starting-songs", "Starting songs", pick("PLAYBACK_START_PREFERENCE", "PLAYBACK_PREPARE_UPCOMING_SONGS")),
    group("your-copy", "Your copy or a service's", pick("MATCHING_LOCAL_PREFERENCE_PERCENT")),
  ].filter(item => item.fields.length));
  const libraryGroups = $derived([
    group("libraries", "Libraries Allstarr uses", pickPath(/MusicLibraryIds/)),
    group("downloads", "Downloads", pick("DOWNLOAD_MODE")),
    group("imported-playlists", "Imported playlists", sectionFor("spotify-import")),
    group("paths", "Folders", sectionFor("paths")),
  ].filter(item => item.fields.length));
  const appsGroups = $derived([
    group("apps", "Allstarr features in apps", pick("CLIENT_API_ENABLED")),
    group("connecting", "Connecting", pick("PUBLIC_LISTENER_URL")),
  ].filter(item => item.fields.length));
  const browserOrigins = $derived(group("browser-apps", "Browser apps", pickPath(/Cors:AllowedOrigins/)));
  const browserAdvanced = $derived(group("browser-advanced", "Methods, headers and credentials", pickPath(/Cors:(AllowedMethods|AllowedHeaders|AllowCredentials)/)));
  const similarSongs = $derived(group("similar-songs", "When a service has no similar songs", pick("PLAYBACK_LASTFM_SIMILAR_ENABLED", "PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED")));
  const scrobbling = $derived(group("scrobbling", "Songs from your own library", pick("SCROBBLING_LOCAL_TRACKS_ENABLED")));
  const activityHistory = $derived(group("activity-history", "Activity history", pick("EVENT_LOG_RETENTION_DAYS", "EVENT_LOG_MAXIMUM_ROWS")));
  const activityAdvanced = $derived(group("activity-advanced", "Cleanup timing and job history", pick("EVENT_LOG_CLEANUP_INTERVAL_MINUTES", "EVENT_LOG_CLEANUP_BATCH_SIZE", "EVENT_LOG_JOB_HISTORY_RETENTION_DAYS")));
  const accounts = $derived(group("accounts", "Accounts", pick("LISTENERS_CAN_CONNECT_OWN_ACCOUNTS")));
  const everyoneHears = $derived(group("everyone-hears", "What everyone hears", pick("EXPLICIT_FILTER")));
  const networkFields = $derived(pick("ADMIN_BIND_ANY_IP", "ADMIN_TRUSTED_SUBNETS"));
  const diagnosticsFields = $derived(pick("DEBUG_LOG_ALL_REQUESTS"));
  const mediaServer = $derived(group("media-server", "Media server", pick("BACKEND_TYPE")));
  // Settings no page claims yet still show, so nothing new is ever hidden.
  const unplaced = $derived(group("more", "More", fields.filter(field => settingPage(field.key) === null)));
  const songCache = $derived(group("song-cache", "Song cache", pick("STORAGE_MODE", "CACHE_AUDIO_MAXIMUM_GIB", "CACHE_DURATION_HOURS")));
  const speed = $derived(group("speed", "Speed", pick("PROVIDER_STREAMS_PER_LISTENER", "MEDIA_MAXIMUM_CONCURRENT_CONVERSIONS", "CACHE_SEARCH_BUDGET_MILLISECONDS")));
  const storageAdvanced = $derived(fields.filter(field => /BackupRetention/.test(field.valuePath ?? "") || sectionFor("cache").includes(field))
    .filter(field => !["CACHE_DURATION_HOURS", "CACHE_SEARCH_BUDGET_MILLISECONDS"].includes(field.key)));
  const bytes = (value: number) => `${(value / 1024 / 1024).toLocaleString(undefined, { maximumFractionDigits: 1 })} MB`;
  const audioMaximum = $derived(Number((config.cache as Record<string, unknown> | undefined)?.audioMaximumGiB ?? 2) * 1024 ** 3);
  const cacheAudioBytes = $derived(typeof cachedSongs?.totalSize === "number" ? cachedSongs.totalSize : null);
  const keptAudioBytes = $derived(typeof keptSongs?.totalSize === "number" ? keptSongs.totalSize : null);
  const storageItems = $derived([{ label: cacheAudioBytes === null ? "Cached song usage unavailable" : `${bytes(cacheAudioBytes)} cached of ${bytes(audioMaximum)}` }, { label: keptAudioBytes === null ? "Kept song usage unavailable" : `${bytes(keptAudioBytes)} kept` }, ...(databaseBytes === null ? [] : [{ label: `${bytes(databaseBytes)} database` }])]);
  const storageSegments = $derived(measuredStorageSegments(cacheAudioBytes, keptAudioBytes, audioMaximum, databaseBytes));
  const summaries = $derived(Object.fromEntries(pageIds.map(id => [id, settingsSummary(id, config, fields, administrator)])));
  // "What each source sends": what Allstarr asks each service for at this setting, beside what real plays delivered.
  function sendsRows(requested: readonly string[]) {
    const asked = new Map(requested.map(line => { const at = line.indexOf(": "); return [line.slice(0, at), line.slice(at + 2)] as const; }));
    const name = (id: string) => schema?.providers?.find(item => item.id === id)?.name ?? id;
    const measured = scores.filter(item => item.quality).map(item => ({ name: name(item.providerId), got: item.quality!.typical }));
    return [...new Set([...asked.keys(), ...measured.map(item => item.name)])]
      .map(source => ({ source, asked: asked.get(source) ?? "its best, up to your limit", got: measured.find(item => item.name === source)?.got ?? null }));
  }
  const setupReviewed = $derived(onboardingState?.steps?.filter(step => step.state !== "pending").length ?? 0);

  let findQuery = $state("");
  const pageLabels = $derived(Object.fromEntries(groups.flatMap(item => item.ids).map(id => [id, allPages[id].label])));
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

  // Administrators load the household settings with the first page (every category's summary line reads them),
  // then again after a save or a live update; switching pages loads only what the new page needs.
  async function refresh(withConfig = true) {
    if (refreshing) return;
    refreshing = true; error = "";
    const requests: Array<[string, Promise<unknown>]> = [["schema", home.schema()]];
    if (administrator && withConfig) requests.push(["config", settings.config()]);
    if (active === "quality") requests.push(["scores", sources.scores().then(value => value.providers ?? []).catch(() => [])]);
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
      if (requests[index][0] === "scores") scores = result.value as ProviderScore[];
    });
    loading = false; refreshing = false;
  }
  const scheduler = createRefreshScheduler(() => refresh());
  $effect(() => { if (loadedSection && loadedSection !== active) void refresh(false); loadedSection = active; });
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
{#snippet qualitySends(requested: readonly string[])}
  <button type="button" class="settings-row settings-link-row" onclick={() => sendsOpen = true}><div><strong>What each source sends</strong><small>What Allstarr asks each service for at this setting, and what it actually got.</small></div><span>Open<ChevronRight size={18} aria-hidden="true" /></span></button>
  <SidePanel bind:open={sendsOpen} title="What each source sends" description="Asked for at this setting, and measured from real plays over the last 30 days." width={420} minimumContentWidth={560} wrapTitle browsable={false}>
    <ul class="source-sends">{#each sendsRows(requested) as row (row.source)}<li><div><strong>{row.source}</strong><small>Asks for {row.asked}</small></div><span>{row.got ?? "Not measured yet"}</span></li>{/each}</ul>
    <p class="settings-muted source-sends-note">Your library always plays as it is. A listener's own limit lowers what they get.</p>
  </SidePanel>
{/snippet}
{#snippet linkRow(title: string, detail: string, label: string, target: string)}
  <a class="settings-row settings-link-row" href={target}><div><strong>{title}</strong><small>{detail}</small></div><span>{label}<ChevronRight size={18} aria-hidden="true" /></span></a>
{/snippet}

<SettingsRestartNotice />
<section class="settings-workspace settings-split settings-surface" class:list-only={listOnly} aria-busy={refreshing}>
  <nav class="settings-categories" aria-label="Settings categories">
    {@render findSetting()}
    {#each groups as navGroup (navGroup.label)}
      <p>{navGroup.label}</p>
      {#each navGroup.ids as id (id)}
        {@const item = allPages[id]}{@const Icon = item.icon}
        <a href={href(id)} aria-current={!listOnly && active === id ? "page" : active === id ? "true" : undefined}><Icon size={22} aria-hidden="true" /><span><strong>{item.label}</strong>{#if summaries[id]}<small>{summaries[id]}</small>{/if}</span></a>
      {/each}
    {/each}
    <small class="settings-version">Allstarr · beta {__APP_VERSION__}</small>
  </nav>
  {#key active}<div class="settings-content settings-page-enter">
    <a class="settings-back" href="#/settings"><ArrowLeft size={20} aria-hidden="true" />Settings</a>
    <header class="settings-page-head">
      <div><h2>{activePage.label}</h2><span class="settings-scope" data-scope={activePage.scope}>{activePage.scope === "you" ? "Just you" : "Everyone"}</span></div>
      <p>{activePage.description}</p>
    </header>
    {#if loading}<RowsSkeleton count={5} />
    {:else if !schema || error}<ErrorState title="Some settings could not be loaded" detail={error} onretry={() => refresh()} />{/if}
    {#if !loading && schema}
      {#if active === "storage"}<PageStatus segments={storageSegments} items={storageItems} />
      {:else if active === "setup"}
        <div class="setup-progress"><PageStatus segments={[{ label: "Reviewed", value: setupReviewed, tone: "info" }, { label: "Left", value: (onboardingState?.steps?.length ?? 0) - setupReviewed, tone: "neutral" }]} items={[{ label: `${setupReviewed} of ${countLabel(onboardingState?.steps?.length ?? 0, "step")} reviewed or skipped` }, { label: "Playback works while setup is open" }]} /></div>
      {/if}
      {#if active === "listening"}
        {#await import("./ListeningPreferencesView.svelte")}<RowsSkeleton />{:then { default: Preferences }}<Preferences {administrator} householdStatus={[]} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "appearance"}
        {#await import("./SettingsAppearance.svelte")}<RowsSkeleton count={1} />{:then { default: Appearance }}<Appearance />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "adding"}
        {#await import("./LibraryPromotionSettings.svelte")}<RowsSkeleton count={4} />{:then { default: Promotion }}<Promotion {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "quality"}
        {#each qualityGroups as item (item.id)}<SettingsFields section={item} {config} onChanged={refresh} sourceQuality={item.id === "audio-quality" ? qualitySends : undefined} />{/each}
      {:else if active === "sources"}
        {#await import("./RoutingView.svelte")}<RowsSkeleton />{:then { default: Routing }}<Routing embedded />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if accounts.fields.length}<SettingsFields section={accounts} {config} onChanged={refresh}>
          {#snippet footer()}{@render linkRow("Manage accounts", "Every service account, who uses it and whether its sign-in still works.", "Sources", "#/integrations/accounts")}{/snippet}
        </SettingsFields>{/if}
      {:else if active === "library"}
        {#each libraryGroups.slice(0, 1) as item (item.id)}<SettingsFields section={item} {config} onChanged={refresh} />{/each}
        <section class="settings-card" aria-label="Adding songs to the library">
          <header><h3>Adding songs to the library</h3></header>
          <button type="button" class="settings-row settings-link-row" onclick={() => addingOpen = true}><div><strong>How added songs are saved</strong><small>Where they go, who adds without asking, quality, tags and file names.</small></div><span>Edit<ChevronRight size={18} aria-hidden="true" /></span></button>
        </section>
        {#each libraryGroups.slice(1) as item (item.id)}<SettingsFields section={item} {config} onChanged={refresh} />{/each}
      {:else if active === "apps"}
        {#each appsGroups as item (item.id)}<SettingsFields section={item} {config} onChanged={refresh}>
          {#snippet footer()}{#if item.id === "connecting"}{@render linkRow("Connect an app", "Step-by-step setup, with a QR code for phones.", "Open", "#/connect")}{/if}{/snippet}
        </SettingsFields>{/each}
        {#if browserOrigins.fields.length}<SettingsFields section={browserOrigins} {config} onChanged={refresh}>
          {#snippet footer()}{#if browserAdvanced.fields.length}<AdvancedFold label="Advanced: methods, headers and credentials"><SettingsFields embedded section={browserAdvanced} {config} onChanged={refresh} /></AdvancedFold>{/if}{/snippet}
        </SettingsFields>{/if}
      {:else if active === "mixes"}
        {#await import("./SoundAnalysisSettings.svelte")}<RowsSkeleton count={2} />{:then { default: Sound }}<Sound />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if similarSongs.fields.length}<SettingsFields section={similarSongs} {config} onChanged={refresh} />{/if}
      {:else if active === "history"}
        <section class="settings-card" aria-label="Scrobbling accounts"><header><h3>Scrobbling</h3></header>
          {@render linkRow("Last.fm and ListenBrainz", "Each listener connects their own accounts. Songs streamed through Allstarr are always sent.", "Open accounts", "#/integrations/accounts")}
        </section>
        {#if scrobbling.fields.length}<SettingsFields section={scrobbling} {config} onChanged={refresh} />{/if}
        {#if activityHistory.fields.length}<SettingsFields section={activityHistory} {config} onChanged={refresh}>
          {#snippet footer()}{#if activityAdvanced.fields.length}<AdvancedFold label="Advanced: cleanup timing and job history"><SettingsFields embedded section={activityAdvanced} {config} onChanged={refresh} /></AdvancedFold>{/if}{/snippet}
        </SettingsFields>{/if}
      {:else if active === "people"}
        {#await import("./ListenersView.svelte")}<RowsSkeleton />{:then { default: Listeners }}<Listeners {initialPanel} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if everyoneHears.fields.length}<SettingsFields section={everyoneHears} {config} onChanged={refresh} />{/if}
        {#await import("./AccessSettingsCard.svelte")}<RowsSkeleton count={2} />{:then { default: Access }}<Access kind="backend" /><Access kind="oidc" />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#if networkFields.length}<SettingsFields section={group("network", "Network", networkFields)} {config} onChanged={refresh} />{/if}
      {:else if active === "alerts"}
        {#await import("./AlertsView.svelte")}<RowsSkeleton />{:then { default: Alerts }}<Alerts {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "storage"}
        {#if songCache.fields.length}<SettingsFields section={songCache} {config} onChanged={refresh} />{/if}
        {#if speed.fields.length}<SettingsFields section={speed} {config} onChanged={refresh} />{/if}
        <AdvancedFold placement="standalone" label="Advanced: cache times, file checks and cleanup">
          {#if storageAdvanced.length}<SettingsFields section={group("storage-advanced", "Retention and cache", storageAdvanced)} {config} onChanged={refresh} />{/if}
          {#await import("./StorageVerificationCard.svelte")}<RowsSkeleton count={1} />{:then { default: Verification }}<Verification />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
          <CacheDiagnosticsCard snapshot={cache} {preview} busy={Boolean(action)} onClean={() => run("clean", settings.cleanCache, "Cache cleanup complete.")} onPurge={target => { purgeTarget = target; purgeOpen = true; }} />
        </AdvancedFold>
      {:else if active === "backups"}
        {#await import("./BackupsView.svelte")}<RowsSkeleton />{:then { default: Backups }}<Backups {administrator} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        {#await import("./HealthView.svelte")}<RowsSkeleton />{:then { default: Health }}<Health {initialPanel} />{:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
        <AdvancedFold placement="standalone" label="Advanced: media and playlist tests, request logging">{#if diagnosticsFields.length}<SettingsFields embedded section={group("diagnostics", "Request logging", diagnosticsFields)} {config} onChanged={refresh} />{/if}<section class="settings-card" aria-label="Diagnostics">
          <div class="settings-row"><div><strong>Media pipeline</strong><small>Checks song details, artwork and playback.</small></div><Button variant="outline" disabled={Boolean(action)} onclick={() => run("media", settings.mediaProbe, "Media pipeline checked.")}>Test media pipeline</Button></div>
          <div class="settings-row"><div><strong>Playlist readiness</strong><small>Checks source access and which songs listeners can play.</small></div><Button variant="outline" disabled={Boolean(action)} onclick={() => run("playlists", settings.playlistProbe, "Playlist pipeline checked.")}>Test playlist readiness</Button></div>
        </section></AdvancedFold>
        <section class="settings-card" aria-label="Setup guide"><header><h3>Setup guide</h3></header>
          {@render linkRow("Walk through setup again", "Nothing is reset; you continue where you left off.", "Open", href("setup"))}
        </section>
      {:else if active === "setup"}
        {#await import("./SetupGuide.svelte")}<RowsSkeleton />{:then { default: Guide }}
          {#if onboardingState}<Guide state={onboardingState} onChanged={onSetupChanged} embedded={onboardingState.completed} />{:else}<Button variant="outline" onclick={() => onOpenSetup()}>Open setup guide</Button>{/if}
        {:catch}<ErrorState title="This settings page could not be opened" onretry={() => window.location.reload()} />{/await}
      {:else if active === "advanced"}
        {#await import("./SourceHelpers.svelte") then { default: SourceHelpers }}<SourceHelpers {schema} {config} {refresh} />{/await}
        {#if mediaServer.fields.length}<SettingsFields section={mediaServer} {config} onChanged={refresh} />{/if}
        {#if unplaced.fields.length}<SettingsFields section={unplaced} {config} onChanged={refresh} />{/if}
      {/if}
    {/if}
  </div>{/key}
</section>
{#if administrator}
  <SidePanel bind:open={addingOpen} title="Adding songs to the library" description="Listeners without their own profile use this one." width={420} minimumContentWidth={560} wrapTitle browsable={false}>
    {#if addingOpen}{#await import("./LibraryPromotionSettings.svelte") then { default: Promotion }}<Promotion {administrator} />{/await}{/if}
  </SidePanel>
{/if}
<ConfirmDialog bind:open={purgeOpen} title="Remove cached files?" description="Temporary cached files will be removed. Kept songs, downloads and media-server files stay safe." confirmLabel="Remove cache" onConfirm={purge} />

<style>
  /* List-detail: categories on the left, the chosen page on the right; on a phone, the list first, then the page. */
  .settings-split { display: grid; grid-template-columns: 19rem minmax(0, 1fr); align-items: start; gap: 0 var(--space-8, 2rem); }
  .settings-categories { position: sticky; top: var(--space-4); display: grid; gap: 2px; align-content: start; }
  .settings-categories > p { margin: var(--space-4) 0 var(--space-1); padding-inline: var(--space-4); color: var(--color-signal-text, var(--color-signal)); font-size: var(--text-sm); font-weight: 500; }
  .settings-categories > a { display: grid; grid-template-columns: 24px minmax(0, 1fr); align-items: center; gap: var(--space-4); min-height: 56px; padding: var(--space-2) var(--space-4); border-radius: 28px; color: var(--color-ink); text-decoration: none; }
  .settings-categories > a :global(svg) { color: var(--color-ink-muted); }
  .settings-categories strong { display: block; font-size: 1rem; line-height: 1.375rem; font-weight: 400; }
  .settings-categories small { display: block; overflow: hidden; color: var(--color-ink-muted); font-size: .8125rem; line-height: 1.125rem; text-overflow: ellipsis; white-space: nowrap; }
  .settings-categories > a:hover { background: color-mix(in srgb, var(--color-ink) 6%, transparent); }
  .settings-categories > a[aria-current] { background: var(--color-secondary); }
  .settings-categories > a[aria-current] :is(strong, small, :global(svg)) { color: var(--color-secondary-foreground); }
  .settings-categories > a:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 2px; }
  .settings-page-head { display: grid; gap: var(--space-2); }
  .settings-page-head > div { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); }
  .settings-page-head :global(h2) { font: 400 1.75rem / 2.25rem var(--font-display, inherit); }
  .settings-scope { display: inline-flex; align-items: center; height: 32px; padding: 0 12px; border: 1px solid var(--color-edge-strong); border-radius: 8px; color: var(--color-ink-muted); font-size: var(--text-sm); font-weight: 500; }
  .settings-scope[data-scope="you"] { border-color: transparent; background: var(--color-panel-raised); }
  .settings-back { display: none; }
  .settings-link-row { width: 100%; border: 0; background: transparent; color: inherit; font: inherit; text-align: left; text-decoration: none; cursor: pointer; }
  .settings-link-row > span { display: inline-flex; justify-self: end; align-items: center; gap: var(--space-2); color: var(--color-ink); font-size: var(--text-sm); font-weight: 500; }
  .settings-link-row:hover { background: var(--row-hover); }
  .source-sends { display: grid; margin: 0; padding: 0; list-style: none; }
  .source-sends li { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); padding: var(--space-3) 0; border-bottom: 1px solid var(--color-edge); }
  .source-sends strong { display: block; font-weight: 500; }
  .source-sends small { display: block; color: var(--color-ink-muted); font-size: var(--text-sm); }
  .source-sends li > span { color: var(--color-ink); font-size: var(--text-sm); text-align: right; }
  .source-sends-note { margin: var(--space-3) 0 0; font-size: var(--text-sm); }
  @media (max-width: 900px) {
    .settings-split { grid-template-columns: minmax(0, 1fr); }
    .settings-split:not(.list-only) .settings-categories, .settings-split.list-only > .settings-content { display: none; }
    .settings-categories { position: static; }
    .list-only .settings-categories > a[aria-current] { background: transparent; }
    .list-only .settings-categories > a[aria-current] :is(strong, small, :global(svg)) { color: inherit; }
    .settings-back { display: inline-flex; align-items: center; gap: var(--space-2); justify-self: start; min-height: 44px; color: var(--color-ink-muted); text-decoration: none; }
  }
  .settings-page-enter { animation: settings-page-in 260ms var(--ease-emphasized); }
  @keyframes settings-page-in { from { opacity: 0; transform: translateY(6px); } }
  .settings-find { position: relative; margin-bottom: var(--space-3); }
  .settings-find input { width: 100%; min-height: 48px; border: 0; border-radius: 28px; padding: 0 var(--space-5); background: var(--color-panel-raised); color: var(--color-ink); font-size: var(--text-md, 1rem); }
  .settings-find input:focus-visible { outline: 2px solid var(--color-signal); outline-offset: 0; }
  .settings-find-results { position: absolute; z-index: 20; top: calc(100% + 6px); left: 0; width: max(100%, 18rem); margin: 0; padding: var(--space-1); list-style: none; border: 1px solid var(--color-edge); border-radius: 14px; background: var(--color-panel-raised, var(--color-panel)); box-shadow: 0 12px 32px color-mix(in srgb, black 30%, transparent); animation: find-in 160ms var(--ease-emphasized); }
  .settings-find-results a { display: grid; gap: 2px; padding: var(--space-2) var(--space-3); border-radius: 10px; color: inherit; text-decoration: none; }
  .settings-find-results a:hover, .settings-find-results a:focus-visible { background: var(--row-hover); }
  .settings-find-results strong { font-weight: 500; font-size: var(--text-sm); }
  .settings-find-results small, .settings-find-empty { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .settings-find-empty { padding: var(--space-2) var(--space-3); }
  @keyframes find-in { from { opacity: 0; transform: translateY(-4px); } }
  :global(.setting-found) { animation: setting-found 1.8s var(--ease-standard); }
  @keyframes setting-found { 0%, 35% { background: color-mix(in srgb, var(--color-signal) 18%, transparent); } }
  @media (prefers-reduced-motion: reduce) { .settings-find-results, :global(.setting-found), .settings-page-enter { animation: none; } }
  .settings-version { display: block; margin-top: var(--space-4); padding-inline: var(--space-4); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .setup-progress :global(.status-legend) { display: none; }
  .setup-progress :global(.status-bar [data-tone="neutral"]) { background: var(--color-panel-raised); }
</style>
