<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { countLabel } from "$lib/count-label";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import { Switch } from "$lib/components/ui/switch";
  import { diagnosticFailureMessage, diagnosticIsNeutral, type DiagnosticFailure } from "$lib/diagnostics";
  import { toast } from "$lib/toasts";
  import { onMount, tick } from "svelte";
  import { goto } from "$app/navigation";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { ChevronRight, LockKeyhole, MoreHorizontal, PackagePlus, Plus, Radio } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import {
    home,
    appleDownload,
    ApiError,
    normalizeResponse,
    type AppleDownloadStatus,
    settings,
    sources,
    type ConnectivityResult,
    type ConfigField,
    type CtsMeasurement,
    type ProviderAccount,
    type ProviderDefinition,
    type ProviderHealth,
    type ProviderSetting,
    type ProviderScore,
    type ProviderSummary,
    type UiSchema,
    type ScrobbleDelivery,
  } from "$lib/api";
  import ConnectivityBars from "$lib/components/ConnectivityBars.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import PlaylistConsent from "$lib/components/PlaylistConsent.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { fieldValue } from "$lib/settings";
  import { relativeTime } from "$lib/activity";
  import { deliveryCounts } from "$lib/source-deliveries";
  import {
    accountSettings,
    accountSubtitle,
    accountDisplayLabel,
    accountOwnershipLabel,
    providerDisplayName,
    ctsMeasurementLabel,
    humanize,
    providerReadinessLabel,
    checkName,
    checkGuide,
    checkStatusLabel,
    sourceProblemLabel,
    sourceStatusLabel,
    checkStatusState,
    groupSourceChecks,
    type SourceCheck,
    sourceMetrics,
    sourceHasChecks,
    sourceNeedsAccount,
    sourceOriginLabel,
    sourceStatus,
    sourceIsInUse,
    sourceUpdateNeedsRefresh,
    sourceTimingLabel,
    sourceStartTimeLabel,
    settingDefault,
    sourceServerFields,
    supportsPlaybackDiagnostic,
    sourceDescription,
  } from "$lib/sources";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";

  let {
    mode = "services",
    administrator,
    backend = "",
    developmentIntelligence = false,
    currentUserId = "",
    initialSource = "",
    initialSection = "data",
    initialConnect = false,
    serviceId = "",
    from = "",
    focusAccount = "",
  }: {
    mode?: "services" | "accounts";
    administrator: boolean;
    backend?: string;
    developmentIntelligence?: boolean;
    currentUserId?: string;
    initialSource?: string;
    initialSection?: string;
    initialConnect?: boolean;
    serviceId?: string;
    from?: string;
    focusAccount?: string;
  } = $props();

  let schema = $state<UiSchema | null>(null);
  let accounts = $state<ProviderAccount[]>([]);
  let audienceUsers = $state<{ id: string; displayName: string }[]>([]);
  let health = $state<ProviderHealth[]>([]);
  let summaries = $state<ProviderSummary[]>([]);
  let measurements = $state<CtsMeasurement[]>([]);
  let config = $state<Record<string, unknown>>({});
  let listenersCanConnectOwnAccounts = $state(true);
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let action = $state("");
  let connectOpen = $state(false);
  let connectProviderId = $state("");
  let appleDownloadOpen = $state(false);
  let tryOpen = $state(false);
  let tryTarget = $state<{ provider: ProviderDefinition; account: ProviderAccount | null; capability: string } | null>(null);
  const checkToasts = new Map<string, number>();
  const lyricSearchSources = $derived.by(() => {
    const ordered = schema?.priorityGroups?.find(group => group.id === "metadata")?.providers ?? [];
    const candidates = schema?.providers.filter(item => item.useMode !== "off" && item.status !== "disabled" &&
      (item.categories?.includes("metadata") || item.runtimeCapabilities?.some(capability => capability.id === "metadata" && capability.canAttempt))) ?? [];
    return candidates.toSorted((a, b) => {
      const rank = (id: string) => ordered.includes(id) ? ordered.indexOf(id) : ordered.length;
      return rank(a.id) - rank(b.id);
    }).map(provider => ({ provider, account: accounts.find(item => item.providerId === provider.id && item.enabled && item.ownerUserId === currentUserId) ??
      accounts.find(item => item.providerId === provider.id && item.enabled && item.scope === "Shared") ?? null }));
  });
  const tryable = new Set(["metadata", "lyrics", "playlist", "streaming", "download", "scrobbling"]);

  function openTry(providerDefinition: ProviderDefinition | undefined, account: ProviderAccount | null, capability: string) {
    if (!providerDefinition) return;
    const key = `${account?.id ?? providerDefinition.id}:${capability}`;
    const notification = checkToasts.get(key);
    if (notification !== undefined) toast.dismiss(notification);
    checkToasts.delete(key);
    tryTarget = { provider: providerDefinition, account, capability };
    tryOpen = true;
  }

  function goToSignIn() {
    const panel = document.querySelector<HTMLElement>(".extension-session");
    if (!panel) return;
    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    panel.scrollIntoView({ behavior: reduced ? "auto" : "smooth", block: "start" });
    const action = [...panel.querySelectorAll<HTMLButtonElement>("button:not([disabled])")]
      .find((button) => /Reconnect|Start verification|Get a new link/.test(button.textContent ?? ""));
    action?.focus({ preventScroll: true });
  }
  let appleStatus = $state<AppleDownloadStatus | null>(null);
  let appleStatusError = $state("");
  let applePlaybackResult = $state("");
  let appleRequest = 0;
  const appleAccount = $derived(accounts.find(account => account.providerId === "apple-musickit" && account.enabled && account.ownerUserId === currentUserId)
    ?? accounts.find(account => account.providerId === "apple-musickit" && account.enabled));
  const appleLoginState = $derived(String(appleStatus?.login_state || appleStatus?.account?.state || "").toLowerCase().replaceAll("-", "_"));
  const appleSessionLabel = $derived(!administrator ? "An administrator manages sign-in" : !appleStatus ? appleStatusError ? "Needs attention" : "Checking setup…"
    : !appleStatus.staged || !appleStatus.daemon_running || !appleStatus.wrapper_healthy ? "Not set up"
    : appleLoginState.includes("mismatch") ? "Use the same Apple account"
    : ["awaiting_2fa", "awaiting2fa", "needs_2fa", "2fa_required", "two_factor_required"].includes(appleLoginState) ? "Waiting for 2FA code"
    : appleStatus.logged_in ? "Signed in" : "Signed out");

  async function loadAppleStatus() {
    const current = ++appleRequest;
    try { const result = await appleDownload.status(); if (current === appleRequest) { appleStatus = result; appleStatusError = ""; } }
    catch { if (current === appleRequest) appleStatusError = "Apple Music setup could not be checked. Open step 2 and refresh status."; }
  }
  $effect(() => { if (administrator && pageId === "apple-musickit") void loadAppleStatus(); });
  function diagnosticError(cause: unknown, capability?: string) {
    const details = cause instanceof ApiError ? normalizeResponse(cause.details) as DiagnosticFailure : {};
    return diagnosticFailureMessage({ ...details, capability });
  }
  async function testApplePlayback() {
    if (!appleAccount?.canManage || action) return;
    const result = await test(appleAccount);
    if (result?.metric === "cts") applePlaybackResult = result.healthy ?? result.success ? "Playback test passed." : diagnosticFailureMessage(result);
    else await measure(appleAccount);
  }
  let configureOpen = $state(false);
  let accessOpen = $state(false);
  let selectedAccount = $state<ProviderAccount | null>(null);
  let selectedSource = $state<ProviderDefinition | null>(null);
  let listFilter = $state("attention");
  let moreOpen = $state(false);
  let moreSourceId = "";
  let sourceUndo = $state<{ providerId: string; run: () => Promise<void> } | null>(null);
  let undoing = $state(false);
  let search = $state("");
  let kind = $state("all");
  let filterInitialized = false;
  let deliveries = $state<ScrobbleDelivery[]>([]);
  let deliveryCountComplete = $state(false);
  const deliverySummary = $derived(deliveryCountComplete ? deliveryCounts(deliveries) : { waiting: 0, unsendable: 0 });
  let deliverySection = $state<HTMLDivElement | null>(null);
  $effect(() => {
    if (initialSection === "scrobble-problems" && deliverySection) {
      const section = deliverySection;
      void tick().then(() => { section.focus({ preventScroll: true }); section.scrollIntoView({ block: "start" }); });
    }
  });
  let deliveryRefresh = $state(0);
  const accountsPage = $derived(mode === "accounts" || from === "accounts" || initialSection === "playlist-editing");
  const panelAccount = $derived(focusAccount ? accounts.find(account => account.id === focusAccount) : undefined);
  let addSourceOpen = $state(false);
  let removal = $state<ProviderAccount | null>(null);
  let removeOpen = $state(false);
  let testResults = $state<Record<string, ConnectivityResult>>({});
  let consumedSourceLink = "";
  let ConnectSourceDialog = $state<typeof import("$lib/components/ConnectSourceDialog.svelte").default | null>(null);
  let AccountAccessDialog = $state<typeof import("$lib/components/AccountAccessDialog.svelte").default | null>(null);
  let AppleDownloadDialog = $state<typeof import("$lib/components/AppleDownloadDialog.svelte").default | null>(null);
  let ProviderTryDialog = $state<typeof import("$lib/components/ProviderTryDialog.svelte").default | null>(null);

  $effect(() => {
    if ((connectOpen || configureOpen) && !ConnectSourceDialog)
      void import("$lib/components/ConnectSourceDialog.svelte").then(module => { ConnectSourceDialog = module.default; }).catch(() => {
        connectOpen = false; configureOpen = false;
        toast.error("The connection form could not be loaded. Try again.");
      });
  });
  $effect(() => {
    if (accessOpen && !AccountAccessDialog)
      void import("$lib/components/AccountAccessDialog.svelte").then(module => { AccountAccessDialog = module.default; }).catch(() => {
        accessOpen = false;
        toast.error("The access editor could not be loaded. Try again.");
      });
  });
  $effect(() => {
    if (appleDownloadOpen && !AppleDownloadDialog)
      void import("$lib/components/AppleDownloadDialog.svelte").then(module => { AppleDownloadDialog = module.default; }).catch(() => {
        appleDownloadOpen = false;
        toast.error("Apple Music sign-in could not be loaded. Try again.");
      });
  });
  $effect(() => {
    if (tryOpen && !ProviderTryDialog)
      void import("$lib/components/ProviderTryDialog.svelte").then(module => { ProviderTryDialog = module.default; }).catch(() => {
        tryOpen = false;
        toast.error("Source tests could not be loaded. Try again.");
      });
  });

  const providers = $derived(
    [...(schema?.providers ?? [])].toSorted((left, right) => {
      const section = (item: ProviderDefinition) =>
        sourceStatus(item, accounts, health) === "disabled"
          ? 2
          : !sourceNeedsAccount(item) ||
              providerAccounts(item.id).some((account) => account.enabled) ? 0 : 1;
      const order = { degraded: 0, needs_verification: 1, needs_config: 1, partial_config: 1, available: 2, configured: 3, healthy: 4, disabled: 5 };
      return section(left) - section(right) ||
        (order[sourceStatus(left, accounts, health) as keyof typeof order] ?? 2) -
        (order[sourceStatus(right, accounts, health) as keyof typeof order] ?? 2) ||
        left.name.localeCompare(right.name);
    }),
  );
  let scores = $state<ProviderScore[]>([]);
  const scoreOf = (id: string) => scores.find((item) => item.providerId.toLowerCase() === id.toLowerCase())?.score ?? null;
  const qualityOf = (id: string) => scores.find((item) => item.providerId.toLowerCase() === id.toLowerCase())?.quality?.typical;
  let scoresRequested = false;
  $effect(() => {
    if (!administrator || scoresRequested) return;
    scoresRequested = true;
    void sources.scores().then((result) => { scores = result.providers ?? []; }).catch(() => {});
  });

  const canConnectAccounts = $derived(administrator || listenersCanConnectOwnAccounts);
  const mySources = $derived(providers.filter(item => sourceIsInUse(item, accounts, health) && item.useMode !== "off" && sourceStatus(item, accounts, health) !== "disabled"));
  const needsSetup = $derived(mySources.filter((item) =>
    ["degraded", "needs_verification", "needs_config", "partial_config"].includes(sourceStatus(item, accounts, health))));
  const pageId = $derived(serviceId || (initialSource && !(initialConnect && mode === "accounts") ? initialSource : ""));
  const pageSource = $derived(pageId ? provider(pageId) ?? null : null);
  const backHref = $derived(accountsPage ? "#/sources/accounts" : "#/sources/services");

  $effect(() => {
    selectedSource = pageSource;
  });

  function servicePath(id: string, account?: string) {
    const query = new URLSearchParams();
    if (accountsPage) query.set("from", "accounts");
    if (account) query.set("account", account);
    const search = query.toString();
    return `#/sources/services/${encodeURIComponent(id)}${search ? `?${search}` : ""}`;
  }
  const offSources = $derived(providers.filter(item => item.useMode === "off" || sourceStatus(item, accounts, health) === "disabled"));
  const filteredSources = $derived((listFilter === "attention" ? needsSetup : listFilter === "in-use" ? mySources : listFilter === "off" ? offSources : providers)
    .filter(item => item.name.toLowerCase().includes(search.trim().toLowerCase()) && (kind === "all" || item.categories?.includes(kind))));
  const filteredAccounts = $derived(accounts.filter(account => `${providerDisplayName(providers, account.providerId)} ${account.displayName} ${account.ownerDisplayName ?? ""}`.toLowerCase().includes(search.trim().toLowerCase())));
  const kinds = $derived([...new Set(providers.flatMap(item => item.categories ?? []))].sort());

  $effect(() => {
    if (!pageSource || moreSourceId === pageSource.id) return;
    moreSourceId = pageSource.id;
    moreOpen = initialSection === "configuration" || Boolean(pageSource.usesSignedSession) || pageSource.id === "apple-musickit" || pageSource.connectionKind === "operator_managed";
  });

  function closePanel() { void goto(backHref, { keepFocus: true, noScroll: true }); }
  async function undoSourceSetting() {
    if (!sourceUndo || undoing) return;
    const change = sourceUndo;
    undoing = true;
    try {
      await change.run();
      if (sourceUndo === change) sourceUndo = null;
      toast.success("Source setting restored.");
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "The source setting could not be restored."); }
    finally { undoing = false; }
  }
  function sourceFact(item: ProviderDefinition) {
    const metrics = sourceMetrics(item, providerHealth(item.id));
    const problem = sourceProblemLabel(item, accounts, health);
    const state = sourceStatus(item, accounts, health);
    const timing = sourceTimingLabel(item, summary(item.id));
    const fact = item.useMode === "off" || state === "disabled" ? "Not used" : problem
      ? `${problem}${metrics.total ? ` · ${metrics.passing} of ${countLabel(metrics.total, "check")} passed` : ""}`
      : state === "needs_verification" ? "Sign in again" : state === "needs_config" ? "Connect an account to use this source"
      : [qualityOf(item.id), timing === "Not applicable" ? "" : timing].filter(Boolean).join(" · ") || (sourceNeedsAccount(item) ? "Account connected" : "No account needed");
    return `${sourceOriginLabel(item)} · ${fact}`;
  }
  function sourceLabel(item: ProviderDefinition) {
    const state = sourceStatus(item, accounts, health);
    if (item.useMode === "off" || state === "disabled") return "Off";
    if (item.useMode === "manualOnly") return "Manual only";
    if (state === "degraded") return "Failing";
    if (["healthy", "configured"].includes(state)) return "Working";
    return sourceStatusLabel(state);
  }
  function sourceTone(item: ProviderDefinition): "ok" | "stop" | "warn" | "neutral" {
    const label = sourceLabel(item);
    return label === "Working" ? "ok" : label === "Failing" ? "stop" : ["Off", "Manual only", "Available"].includes(label) ? "neutral" : "warn";
  }
  function waitingFor(account: ProviderAccount) {
    return deliveries.filter(item => item.destinationAccountId === account.id || !item.destinationAccountId && item.suggestedDestinationAccountId === account.id).length;
  }
  function accountState(account: ProviderAccount) {
    if (!account.enabled) return "Off";
    const waiting = waitingFor(account);
    if (waiting) return `${waiting} waiting`;
    if (!provider(account.providerId)?.usesSignedSession && (!account.secret.configured || account.secret.revoked)) return "Sign in again";
    const checks = providerHealth(account.providerId).filter(item => !item.providerAccountId || item.providerAccountId === account.id);
    if (checks.some(item => item.health === "degraded" && item.reasonCode?.replaceAll("-", "_") === "service_unavailable")) return "Service down";
    return checks.some(item => item.health === "degraded") ? "Needs attention" : "Working";
  }
  const panelIndex = $derived(panelAccount ? filteredAccounts.findIndex(account => account.id === panelAccount.id) : filteredSources.findIndex(item => item.id === pageId));
  const panelCount = $derived(panelAccount ? filteredAccounts.length : filteredSources.length);

  function panelNavigate(offset: number) {
    if (panelAccount) {
      const index = filteredAccounts.findIndex(account => account.id === panelAccount.id);
      const next = filteredAccounts[index + offset];
      if (next) void goto(servicePath(next.providerId, next.id), { keepFocus: true, noScroll: true });
    } else {
      const index = filteredSources.findIndex(item => item.id === pageId);
      const next = filteredSources[index + offset];
      if (next) void goto(servicePath(next.id), { keepFocus: true, noScroll: true });
    }
  }
  function canCheckSource(item: ProviderDefinition) {
    return providerAccounts(item.id).some(account => account.enabled && account.canManage) || item.runtimeCapabilities?.some(check => check.canTest) || providerHealth(item.id).some(check => check.canTest);
  }
  async function checkSource(item: ProviderDefinition) {
    const account = providerAccounts(item.id).find(account => account.enabled && account.canManage);
    if (account) await test(account);
    else {
      const check = item.runtimeCapabilities?.find(check => check.canTest);
      const capability = check?.id ?? providerHealth(item.id).find(check => check.canTest)?.capability;
      if (capability) await testWithoutAccount(item, capability);
    }
  }
  const canManageAllAccounts = $derived(administrator);

  function provider(id: string) {
    return providers.find((item) => item.id.toLowerCase() === id.toLowerCase());
  }

  function providerAccounts(id: string) {
    return accounts.filter((account) =>
      account.providerId.toLowerCase() === id.toLowerCase());
  }

  function providerHealth(id: string) {
    return health.filter((item) =>
      item.provider.toLowerCase() === id.toLowerCase());
  }

  function summary(id: string) {
    return summaries.find((item) =>
      item.providerId.toLowerCase() === id.toLowerCase());
  }

  const readinessClass = (ready: boolean, health?: string | null) =>
    ready ? "healthy" : health === "degraded" ? "degraded" : "suggested";

  let followUps: ReturnType<typeof setTimeout>[] = [];
  // Signing in or changing an account starts checks in the background; pick up their results as they finish.
  async function refreshAndFollow() {
    await refresh();
    for (const timer of followUps) clearTimeout(timer);
    followUps = [5_000, 15_000, 45_000].map((delay) => setTimeout(() => void refresh(), delay));
  }

  $effect(() => () => { for (const timer of followUps) clearTimeout(timer); });

  let refreshTask: Promise<void> | null = null;

  function refresh(): Promise<void> {
    if (!refreshTask) refreshTask = loadSources().finally(() => { refreshTask = null; });
    return refreshTask;
  }

  async function loadSources() {
    error = "";
    const requests: Promise<unknown>[] = [home.schema(), sources.accounts()];
    if (administrator) requests.push(home.providers(), sources.health(), sources.cts(), settings.config());
    const results = await Promise.allSettled(requests);
    if (results[0].status === "fulfilled") schema = results[0].value as UiSchema;
    if (results[1].status === "fulfilled") {
      const response = results[1].value as {
        listenersCanConnectOwnAccounts?: boolean;
        audienceUsers?: { id: string; displayName: string }[];
        accounts: ProviderAccount[];
      };
      accounts = response.accounts;
      audienceUsers = response.audienceUsers ?? [];
      listenersCanConnectOwnAccounts = response.listenersCanConnectOwnAccounts ?? true;
    } else if (!administrator) {
      listenersCanConnectOwnAccounts = schema?.listenersCanConnectOwnAccounts ?? true;
    }
    if (administrator) {
      if (results[2]?.status === "fulfilled")
        summaries = (results[2].value as { providers: ProviderSummary[] }).providers;
      if (results[3]?.status === "fulfilled") health = results[3].value as ProviderHealth[];
      if (results[4]?.status === "fulfilled")
        measurements = (results[4].value as { measurements: CtsMeasurement[] }).measurements;
      if (results[5]?.status === "fulfilled") config = results[5].value as Record<string, unknown>;
    }
    const failed = results.filter((result) => result.status === "rejected");
    if (failed.length)
      error = failed[0].reason instanceof Error ? failed[0].reason.message : "Source state is unavailable.";
    if (!filterInitialized && schema) { listFilter = needsSetup.length ? "attention" : "in-use"; filterInitialized = true; }
    if (accountsPage) deliveryRefresh++;
    loading = false;
  }

  const refreshScheduler = createRefreshScheduler(refresh);
  const scheduleRefresh = refreshScheduler.schedule;

  async function completed(message: string) {
    connectOpen = false; configureOpen = false; accessOpen = false;
    toast.success(message);
    await refresh();
  }

  function configure(account: ProviderAccount) {
    selectedAccount = account;
    configureOpen = true;
  }

  $effect(() => {
    if (!initialSource || !initialConnect || mode !== "accounts") {
      consumedSourceLink = "";
      return;
    }
    const link = `${initialSource}\0${initialConnect}`;
    if (!schema || consumedSourceLink === link || !canConnectAccounts) return;
    const item = schema.providers.find((provider) =>
      provider.id.toLowerCase() === initialSource.toLowerCase());
    if (!item) return;
    consumedSourceLink = link;
    connectProviderId = item.id;
    connectOpen = true;
  });

  function sourcePurpose(item: ProviderDefinition) {
    if (item.id === "apple-musickit")
      return "Browse the public catalog and connect your personal library with a Media User Token and storefront. Playback, downloads and synced lyrics use the same account after step 2.";
    if (item.id === "spotiflac-apple-music")
      return "Apple Music extension metadata and Media User Token lyrics, including configured translation or pronunciation.";
    if (item.description) return sourceDescription(item.description);
    const uses: Record<string, string> = {
      metadata: "song details",
      streaming: "playback",
      download: "downloads",
      playlists: "playlists",
      lyrics: "lyrics",
      scrobbling: "scrobbling",
      intelligence: "recommendations",
    };
    const purposes = (item.categories ?? []).map((category) => uses[category.toLowerCase()]).filter(Boolean);
    if (!purposes.length) return "Open to see what this service provides.";
    const list = purposes.length === 1 ? purposes[0] : `${purposes.slice(0, -1).join(", ")} and ${purposes.at(-1)}`;
    return `Used for ${list}.`;
  }

  function accountSettingValue(account: ProviderAccount, field: ProviderSetting) {
    if (field.sensitive)
      return account.configuredFields?.includes(field.key) ? "Stored" : "Not set";
    const saved = account.configuration?.[field.key];
    const value = saved ?? settingDefault(field);
    if (value === "" || value == null) return "Not set";
    if (field.type === "toggle") return value === true || value === "true" ? "Enabled" : "Disabled";
    return String(value);
  }

  async function saveServerValue(field: ConfigField, value: string, undo = false) {
    if (action || field.readOnly || field.ownership === "deployment") return;
    const previous = String(fieldValue(config, field));
    action = `setting:${field.key}`;
    try {
      await settings.save({ [field.key]: value });
      await refresh();
      if (!undo) toast.success(`${field.label} saved.`, field.sensitive ? undefined : { label: "Undo", run: () => saveServerValue(field, previous, true) });
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "The setting could not be saved."); }
    finally { action = ""; }
  }

  function resetServerField(event: MouseEvent, field: ConfigField) {
    const input = (event.currentTarget as HTMLButtonElement).form?.elements.namedItem(field.key);
    if (input instanceof HTMLInputElement) { input.value = String(settingDefault(field)); void saveServerValue(field, input.value); }
  }

  function manageAccess(account: ProviderAccount) {
    selectedAccount = account;
    accessOpen = true;
  }

  async function toggle(account: ProviderAccount) {
    if (action) return;
    action = account.id;
    try {
      await sources.setEnabled(account, !account.enabled);
      await refresh();
      const saved = accounts.find(item => item.id === account.id);
      toast.success(`${account.sourceDisplayName || account.displayName} ${account.enabled ? "paused" : "enabled"}.`, saved ? { label: "Undo", run: async () => {
        try { await sources.setEnabled(saved, account.enabled); await refresh(); } catch (cause) { toast.error(cause instanceof Error ? cause.message : "The account could not be updated."); }
      } } : undefined);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The connection could not be updated.");
    } finally {
      action = "";
    }
  }

  function checkNotification(key: string, message: string, kind: "success" | "error" | "info") {
    const previous = checkToasts.get(key);
    if (previous !== undefined) toast.dismiss(previous);
    if (!tryOpen) checkToasts.set(key, toast.show(message, kind));
  }

  async function test(account: ProviderAccount, capability?: string) {
    const key = `${account.id}:${capability ?? "all"}`;
    if (action) return;
    action = key;
    try {
      const slow = capability === "streaming" || !capability && supportsPlaybackDiagnostic(providerHealth(account.providerId));
      const pending = toast.progress(`Testing ${provider(account.providerId)?.name ?? account.providerId} ${capability ? humanize(capability) : "connection"}…${slow ? " Playback checks can take a few minutes." : ""}`);
      const result = await sources.test(account, capability).finally(() => toast.dismiss(pending));
      testResults = { ...testResults, [key]: result };
      let message = `${provider(account.providerId)?.name ?? account.providerId} ${capability ? humanize(capability) : "connection"} ${result.healthy ?? result.success ? "passed" : "needs attention"}.`;
      if (result.seekRung) message += ` Playback: ${humanize(result.seekRung)}${result.sampleBytes ? ` (${countLabel(result.sampleBytes, "byte")} sampled)` : ""}.`;
      if (result.reasonCode) message += ` ${diagnosticFailureMessage({ ...result, capability })}`;
      if (diagnosticIsNeutral(result)) checkNotification(key, diagnosticFailureMessage({ ...result, capability }), "info");
      else checkNotification(key, message, result.healthy ?? result.success ? "success" : "error");
      await refresh();
      return result;
    } catch (cause) {
      checkNotification(key, diagnosticError(cause, capability ?? "connection"), "error");
    } finally {
      action = "";
    }
  }

  async function testWithoutAccount(source: ProviderDefinition, capability: string) {
    const key = `${source.id}:${capability}`;
    if (action) return;
    action = key;
    try {
      const pending = toast.progress(`Testing ${source.name} ${humanize(capability)}…${capability === "streaming" ? " Playback checks can take a few minutes." : ""}`);
      const result = await sources.testWithoutAccount(source.id, capability).finally(() => toast.dismiss(pending));
      testResults = { ...testResults, [key]: result };
      const passed = result.healthy ?? result.success ?? false;
      if (diagnosticIsNeutral(result)) checkNotification(key, diagnosticFailureMessage({ ...result, capability }), "info");
      else checkNotification(key, `${source.name} ${humanize(capability)} ${passed ? "passed" : "needs attention"}.${result.reasonCode ? ` ${diagnosticFailureMessage({ ...result, capability })}` : ""}`, passed ? "success" : "error");
      await refresh();
    } catch (cause) {
      checkNotification(key, diagnosticError(cause, capability ?? "connection"), "error");
    } finally {
      action = "";
    }
  }

  async function measure(account: ProviderAccount) {
    const key = `${account.id}:cts`;
    if (action) return;
    action = key;
    try {
      const pending = toast.progress(`Measuring ${provider(account.providerId)?.name ?? account.providerId} click-to-stream… This can take a few minutes.`);
      const result = await sources.deepStream(account.providerId, account.id).finally(() => toast.dismiss(pending));
      testResults = { ...testResults, [key]: {
        ...result,
        latencyMs: result.clickToStreamMilliseconds ?? result.latencyMs,
      } };
      const passed = result.succeeded ?? result.healthy ?? result.success ?? false;
      const message = passed ? `${provider(account.providerId)?.name ?? account.providerId} click-to-stream measured${result.seekRung ? `: ${humanize(result.seekRung)}` : ""}.` : diagnosticFailureMessage(result);
      toast.show(message, passed ? "success" : diagnosticIsNeutral(result) ? "info" : "error");
      if (account.providerId === "apple-musickit") applePlaybackResult = passed ? "Playback test passed." : message;
      await refresh();
    } catch (cause) {
      const message = diagnosticError(cause);
      toast.error(message);
      if (account.providerId === "apple-musickit") applePlaybackResult = message;
    } finally {
      action = "";
    }
  }

  async function measureSource(item: ProviderDefinition) {
    const key = `${item.id}:cts`;
    if (action) return;
    action = key;
    try {
      const pending = toast.progress(`Measuring ${item.name} click-to-stream… This can take a few minutes.`);
      const result = await sources.deepStream(item.id).finally(() => toast.dismiss(pending));
      const passed = result.succeeded ?? result.healthy ?? result.success ?? false;
      toast.show(passed ? `${item.name} click-to-stream measured.` : diagnosticFailureMessage(result), passed ? "success" : diagnosticIsNeutral(result) ? "info" : "error");
      await refresh();
    } catch (cause) {
      toast.error(diagnosticError(cause));
    } finally {
      action = "";
    }
  }

  async function remove() {
    if (!removal || action) return;
    action = removal.id;
    try {
      await sources.remove(removal.id);
      removeOpen = false;
      if (focusAccount === removal.id) closePanel();
      await completed(`${removal.sourceDisplayName || removal.displayName} removed.`);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The saved connection could not be removed.");
    } finally {
      action = "";
    }
  }

  onMount(() => {
    try { search = localStorage.getItem("allstarr.sources.search") ?? ""; kind = localStorage.getItem("allstarr.sources.kind") ?? "all"; } catch { /* use the default filters */ }
    void refresh();
    const unsubscribe = liveUpdates.subscribe(update => {
      if (sourceUpdateNeedsRefresh(update)) scheduleRefresh();
    });
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
    };
  });
  $effect(() => {
    try { localStorage.setItem("allstarr.sources.search", search); localStorage.setItem("allstarr.sources.kind", kind); } catch { /* filters work without storage */ }
  });
</script>

{#snippet sourceUseAndFacts(source: ProviderDefinition)}
  {#await import("$lib/components/SourceUseSettings.svelte") then { default: SourceUseSettings }}<SourceUseSettings providerId={source.id} {administrator} onSaved={refresh} onUndo={(providerId, run) => sourceUndo = { providerId, run }} />{/await}
  <dl class="source-facts"><dt>Quality</dt><dd>{qualityOf(source.id) || "Not measured yet"}</dd><dt>Score</dt><dd>{scoreOf(source.id) ?? "Not measured yet"}</dd><dt>Accounts</dt><dd>{providerAccounts(source.id).filter(account => account.enabled).length} of {providerAccounts(source.id).length} on</dd><dt>Start time</dt><dd>{sourceStartTimeLabel(measurements.find(item => item.providerId === source.id), scores.find(item => item.providerId.toLowerCase() === source.id.toLowerCase()))}</dd></dl>
{/snippet}

{#snippet accountItems(account: ProviderAccount)}
      <DropdownMenu.Item class="bits-menu-item" onSelect={() => window.location.hash = servicePath(account.providerId, account.id)}>Account details</DropdownMenu.Item>
      {#if account.canManage}<DropdownMenu.Item class="bits-menu-item" onSelect={() => configure(account)}>Reconnect</DropdownMenu.Item>{/if}
      {#if account.canChangeAudience}<DropdownMenu.Item class="bits-menu-item" onSelect={() => manageAccess(account)}>Edit access</DropdownMenu.Item>{/if}
      {#if administrator && account.canManage}<DropdownMenu.Item class="bits-menu-item" onSelect={() => void test(account)}>Test connection</DropdownMenu.Item>{/if}
      {#if account.canManage}<DropdownMenu.Item class="bits-menu-item" onSelect={() => void toggle(account)}>{account.enabled ? "Pause" : "Enable"}</DropdownMenu.Item><DropdownMenu.Separator /><DropdownMenu.Item class="bits-menu-item danger-item" onSelect={() => { removal = account; removeOpen = true; }}>Remove…</DropdownMenu.Item>{/if}
{/snippet}

{#snippet accountMenu(group: ProviderAccount[], label: string)}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger class="icon-button" aria-label={`Actions for ${group.length > 1 ? `${label} accounts` : group[0].displayName}`} disabled={Boolean(action)}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
    <DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu" sideOffset={6} align="end">
      {#if group.length === 1}{@render accountItems(group[0])}
      {:else}{#each group as item, index (item.id)}{#if index}<DropdownMenu.Separator />{/if}<DropdownMenu.Group><DropdownMenu.GroupHeading class="bits-menu-heading">{item.displayName}</DropdownMenu.GroupHeading>{@render accountItems(item)}</DropdownMenu.Group>{/each}{/if}
    </DropdownMenu.Content></DropdownMenu.Portal>
  </DropdownMenu.Root>
{/snippet}

{#snippet accountList()}
  {#if filteredAccounts.length}
    {@const serviceName = (account: ProviderAccount) => providerDisplayName(providers, account.providerId)}
    {@const groups = [...new Set(filteredAccounts.map(serviceName))].toSorted((left, right) => left.localeCompare(right)).map(name => filteredAccounts.filter(account => serviceName(account) === name))}
    {@const labels = new Map(filteredAccounts.map(item => [item.id, accountDisplayLabel(item, providerDisplayName(providers, item.providerId))]))}
    {@const hasLabels = [...labels.values()].some(Boolean)}
    <div class="accounts-table-wrap"><table class="accounts-table" aria-label="Accounts"><thead><tr><th>Service</th>{#if hasLabels}<th>Account</th>{/if}<th>Owner</th><th class="shared-column">Shared with</th><th>State</th><th><span class="sr-only">Actions</span></th></tr></thead><tbody>
      {#each groups as group (serviceName(group[0]))}
        {@const account = group[0]}
        <tr class:account-selected={group.some(item => focusAccount === item.id)} id={`connection-${account.id}`}>
          <td data-label="Service"><a class="account-opener" href={servicePath(account.providerId, account.id)} data-sveltekit-keepfocus data-sveltekit-noscroll aria-label={`Open ${providerDisplayName(providers, account.providerId)} account`}><ProviderArtwork id={account.providerId} definition={provider(account.providerId)} decorative /><strong>{providerDisplayName(providers, account.providerId)}{group.length > 1 ? ` · ${countLabel(group.length, "account")}` : ""}</strong></a></td>
          {#if hasLabels}{@const accountLabels = [...new Set(group.map(item => labels.get(item.id)).filter(Boolean))].join(", ")}<td data-label={accountLabels ? "Account" : undefined}>{accountLabels}</td>{/if}
          <td data-label="Owner">{[...new Set(group.map(item => item.ownerDisplayName || item.creatorDisplayName || (item.scope === "Shared" ? "Household" : "")).filter(Boolean))].join(", ")}</td>
          <td data-label="Shared with">{[...new Set(group.map(item => item.scope === "Shared" ? "Everyone" : `Just ${item.ownerDisplayName || "you"}`))].join(", ")}</td>
          <td data-label="State">{#each [...new Set(group.map(accountState))] as state}<StatePill tone={state === "Working" ? "ok" : state === "Service down" ? "stop" : ["Off", "Connected"].includes(state) ? "neutral" : "warn"} text={state} />{/each}</td>
          <td><div class="account-menus">{#if group.some(item => item.canManage || item.canChangeAudience)}{@render accountMenu(group.filter(item => item.canManage || item.canChangeAudience), serviceName(account))}{:else}<span class="muted">Read only</span>{/if}</div></td>
        </tr>
      {/each}
    </tbody></table></div>
  {:else}<EmptyState title={accounts.length ? "No accounts in this view" : "No accounts connected"} description={accounts.length ? "Try a different search." : "Connect an account to use your subscriptions and send listens."} />{/if}
{/snippet}

{#snippet checkRow(label: string, check: Omit<SourceCheck, "id">, result?: ConnectivityResult, onTest?: () => void, canTest = false, testKey = "", onTry?: () => void)}
  {@const guide = check.ready ? null : checkGuide(check.reasonCode)}
  <li class="service-check">
    <span class="service-check-name">{label}</span>
    <span class="service-check-status"><Badge state={checkStatusState(check)}>{checkStatusLabel(check)}</Badge>{#if guide}<small>{guide.step}</small>{#if guide.signIn}<button type="button" class="text-button" onclick={goToSignIn}>Go to sign-in</button>{/if}{/if}{#if check.reasonCode === "apple-download-sign-in-required"}{#if administrator}<button type="button" class="text-button" onclick={() => appleDownloadOpen = true}>Open step 2: Apple Music sign-in</button>{:else}<a href="#/sources/services/apple-musickit">See step 2: Apple Music sign-in</a>{/if}{/if}</span>
    {#if result?.bars != null}<ConnectivityBars bars={result.healthy ?? result.success ? result.bars : 0} latency={result.latencyMs} />{:else}<span></span>{/if}
    {#if (canTest && onTest) || onTry}
      <span class="service-check-actions">
        {#if canTest && onTest}<Button variant="secondary" size="sm" class={onTry ? "rounded-e-none" : ""} disabled={Boolean(action)} onclick={onTest}>{action && action === testKey ? "Testing…" : "Test"}</Button>{/if}
        {#if onTry}<Button variant="secondary" size="sm" class={canTest && onTest ? "rounded-s-none px-1.5" : ""} aria-label={`Open ${label} tests`} title={`Open ${label} tests`} onclick={onTry}>{#if canTest && onTest}<ChevronRight aria-hidden="true" />{:else}Test<ChevronRight aria-hidden="true" />{/if}</Button>{/if}
      </span>
    {:else}<span></span>{/if}
  </li>
{/snippet}

{#snippet serverFields(source: ProviderDefinition, fields: ConfigField[])}
            {#each fields as field}
              <div class="settings-row setting-field" class:read-only={field.readOnly || field.ownership === "deployment" || source.id === "apple-musickit" && (config.appleDownload as { endpointManagedByDeployment?: boolean })?.endpointManagedByDeployment}>
                <div><label for={field.key}><strong>{source.id === "apple-musickit" ? "Optional playback gateway URL" : field.label}</strong></label>{#if source.id === "apple-musickit"}<small>{(config.appleDownload as { endpointManagedByDeployment?: boolean })?.endpointManagedByDeployment ? "Configured by deployment." : "Leave blank to use the bundled gateway."}</small>{:else if field.helpText}<small>{field.helpText}</small>{/if}{#if field.ownership === "deployment"}<small>Deployment-owned</small>{/if}</div>
                {#if field.readOnly || field.ownership === "deployment" || source.id === "apple-musickit" && (config.appleDownload as { endpointManagedByDeployment?: boolean })?.endpointManagedByDeployment}
                  <output><LockKeyhole size={14} aria-label="Managed by deployment" /> {field.sensitive ? "Stored" : String(fieldValue(config, field))}</output>
                {:else if field.type === "select"}
                  <SelectField name={field.key} label={source.id === "apple-musickit" ? "Optional playback gateway URL" : field.label} value={field.sensitive ? "" : String(fieldValue(config, field))} options={field.options ?? []} disabled={Boolean(action)} onchange={value => void saveServerValue(field, value)} />
                {:else if field.type === "toggle"}
                  <span class="settings-toggle"><Checkbox id={field.key} name={field.key} checked={Boolean(fieldValue(config, field))} disabled={Boolean(action)} onCheckedChange={value => void saveServerValue(field, String(value))} /></span>
                {:else}
                  <div class="source-setting-control">
                  <input
                    id={field.key}
                    name={field.key}
                    type={field.sensitive ? "password" : field.type === "number" ? "number" : field.type === "url" ? "url" : "text"}
                    value={field.sensitive ? "" : String(fieldValue(config, field))}
                    min={field.min ?? undefined}
                    max={field.max ?? undefined}
                    required={field.required}
                    autocomplete="off"
                    disabled={Boolean(action)}
                    onchange={event => { if (event.currentTarget.checkValidity()) void saveServerValue(field, event.currentTarget.value); }}
                  />
                  {#if field.defaultValueJson != null && !field.sensitive}
                    <Button type="button" variant="secondary" size="sm" disabled={Boolean(action)} aria-label={`Reset ${field.label} to default`} onclick={(event) => resetServerField(event, field)}>Reset to default</Button>
                  {/if}
                  </div>
                {/if}
              </div>
            {/each}
{/snippet}

{#snippet servicePage(source: ProviderDefinition)}
  {@const sourceAccounts = providerAccounts(source.id)}
  {@const fields = accountSettings(source)}
  {@const streams = source.categories?.some((item) => ["streaming", "download"].includes(item.toLowerCase())) ?? false}
  {@const usesAccounts = sourceNeedsAccount(source) || fields.length > 0 || sourceAccounts.length > 0}
  <section class="service-page service-advanced" aria-label={`${source.name} settings and tests`}>
    <p class="service-description">{sourcePurpose(source)}</p>
    <div class="panel-secondary-actions">
      {#if administrator && streams && !sourceNeedsAccount(source)}<Button variant="outline" size="sm" disabled={Boolean(action)} onclick={() => void measureSource(source)}>Measure start time</Button>{/if}
      {#if canConnectAccounts && usesAccounts && source.id !== "audiomuse-ai"}<Button variant="outline" size="sm" onclick={() => { connectProviderId = source.id; connectOpen = true; }}>Connect account</Button>{/if}
    </div>
    {#if source.id === "spicylyrics"}
      <section class="settings-card" aria-label="SpicyLyrics setup">
        <div class="settings-row">
          <div><strong>Connect a free server key</strong><small>Create your own key, then choose Connect account. Allstarr stores the key encrypted. Testing needs only a Spotify track link.</small></div>
          <Button variant="secondary" href="https://developers.spicylyrics.org/docs/keys" target="_blank" rel="noopener noreferrer">Get a free API key</Button>
        </div>
      </section>
    {/if}

    {#if source.id === "audiomuse-ai"}
      <section class="settings-card">
        <div class="settings-row">
          <div><strong>Sound analysis (AudioMuse)</strong><small>Manage the connection and household mix publishing in Settings.</small></div>
          {#if administrator}<Button href="#/settings/discovery">Configure</Button>{/if}
        </div>
      </section>
    {:else}
      {#if usesAccounts}
        <section class="settings-card service-accounts" aria-labelledby="service-accounts-title">
          <header><div><h3 id="service-accounts-title">Accounts</h3><small>{sourceNeedsAccount(source) ? "This service needs an account to work." : "Optional accounts unlock more of this service."}</small></div></header>
          {#each sourceAccounts as account (account.id)}
            {@const capabilities = health.filter((item) => item.providerAccountId === account.id)}
            {@const accountCts = measurements.find((item) => item.providerAccountId === account.id)}
            {@const ready = capabilities.filter((item) => item.ready).length}
            <article class="service-account" id={`connection-${account.id}`} class:focused={account.id === focusAccount}>
              <header>
                <span class="service-account-title">
                  <strong>{providerDisplayName(providers, account.providerId)}</strong>
                  <small>{accountOwnershipLabel(account)}{source.usesSignedSession || account.secret.configured && !account.secret.revoked ? "" : " · Setup needed"}</small>
                </span>
                <Badge state={!account.enabled ? "disabled" : readinessClass(capabilities.length > 0 && capabilities.every((item) => item.ready), capabilities.some((item) => item.health === "degraded") ? "degraded" : null)}>
                  {!account.enabled ? "Turned off" : capabilities.length && ready === capabilities.length ? "Working" : `${ready} of ${capabilities.length} ready`}
                </Badge>
                <span class="service-account-actions">
                  {#if administrator && account.canManage}<Button variant="secondary" size="sm" disabled={!account.enabled || Boolean(action)} onclick={() => void test(account)}>{action === `${account.id}:all` ? "Testing…" : "Test connection"}</Button>{/if}
                  {#if administrator && account.canManage && streams}<Button variant="secondary" size="sm" disabled={!account.enabled || Boolean(action)} onclick={() => void measure(account)}>{action === `${account.id}:cts` ? "Measuring…" : "Measure start time"}</Button>{/if}
                  {#if account.canManage}<Button variant="text" size="sm" onclick={() => configure(account)}>Edit configuration</Button>{/if}
                  {#if account.canManage || account.canChangeAudience}
                    <DropdownMenu.Root>
                      <DropdownMenu.Trigger class="icon-button" disabled={Boolean(action)} aria-label={`Actions for ${account.displayName}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                      <DropdownMenu.Portal>
                        <DropdownMenu.Content preventScroll={false} class="bits-menu" sideOffset={6} align="end">
                          {#if account.canChangeAudience}<DropdownMenu.Item class="bits-menu-item" onSelect={() => manageAccess(account)}>Edit access</DropdownMenu.Item>{/if}
                          {#if account.canManage}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void toggle(account)}>{account.enabled ? "Disable" : "Enable"}</DropdownMenu.Item>{/if}
                          <DropdownMenu.Separator />
                          {#if account.canManage}<DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action)} onSelect={() => { removal = account; removeOpen = true; }}>Remove</DropdownMenu.Item>{/if}
                        </DropdownMenu.Content>
                      </DropdownMenu.Portal>
                    </DropdownMenu.Root>
                  {/if}
                </span>
              </header>
              {#if fields.length}
                <dl class="service-settings">
                  {#each fields as field}
                    <div><dt>{field.label}</dt><dd>{accountSettingValue(account, field)}</dd></div>
                  {/each}
                </dl>
              {/if}
              {#if capabilities.length}
                <ul class="service-checks" aria-label={`Checks for ${account.displayName}`}>
                  {#each capabilities as capability}
                    {@render checkRow(checkName(capability.capability), capability, testResults[`${account.id}:${capability.capability}`], () => void test(account, capability.capability), administrator && capability.canTest && account.canManage && account.enabled, `${account.id}:${capability.capability}`, administrator && account.canManage && account.enabled && tryable.has(capability.capability.toLowerCase()) ? () => openTry(source, account, capability.capability) : undefined)}
                  {/each}
                </ul>
              {/if}
              {#if capabilities.some((item) => item.reasonCode === "apple-web-token-unavailable")}
                <p class="service-note" role="status">Apple's web-player token couldn't be fetched. Run this account's test again later.</p>
              {/if}
              {#if accountCts}<p class="service-note">Click to stream: {ctsMeasurementLabel(accountCts)} · {relativeTime(accountCts.testedAt)}</p>{/if}
              {#if account.providerId === "spotify" && account.scope === "Personal" && account.enabled && account.ownerUserId === currentUserId}
                <Button variant="secondary" href={`#/library/import?account=${encodeURIComponent(account.id)}`}>Move your Spotify collection</Button>
              {/if}
            </article>
          {:else}
            <p class="service-empty">No accounts connected yet.{#if fields.length}{" "}These settings are saved with an account.{/if}</p>
          {/each}
        </section>
      {/if}

      {#if !panelAccount && ["lastfm", "listenbrainz", "multi-scrobbler", "koito"].includes(source.id)}
        {#key source.id}{#await import("$lib/components/SourceDeliveryGroups.svelte") then { default: SourceDeliveryGroups }}<SourceDeliveryGroups {accounts} targetId={source.id} onConfigure={configure} />{/await}{/key}
      {/if}

      {#if (source.runtimeCapabilities ?? []).length && !sourceAccounts.length}
        <section class="settings-card" aria-labelledby="service-checks-title">
          <header><div><h3 id="service-checks-title">Checks</h3><small>What this service can do right now.</small></div></header>
          <ul class="service-checks">
            {#each groupSourceChecks(source.runtimeCapabilities ?? []) as group}
              {@const check = group.checks[0]}
              {@const single = group.checks.length === 1}
              {@render checkRow(group.label, check, testResults[`${source.id}:${check.id}`], () => void testWithoutAccount(source, check.id), administrator && single && Boolean(check.canTest), `${source.id}:${check.id}`, administrator && single && tryable.has(check.id.toLowerCase()) ? () => openTry(source, null, check.id) : undefined)}
            {/each}
          </ul>
          {#if (source.runtimeCapabilities ?? []).some((item) => item.reasonCode === "apple-download-gateway-update-required")}
            <p class="service-note" role="status">The Apple download gateway runs an older version than Allstarr supports. Update the gateway, then refresh this page. Catalog and playlists keep working meanwhile.</p>
          {/if}
        </section>
      {/if}

      {#if source.id === "apple-musickit"}
        <section class="settings-card apple-checklist" aria-label="Apple Music setup">
          <header><div><h3>Apple Music setup</h3><small>Use the same Apple account for steps 1 and 2.</small></div></header>
          <ol>
            <li><div><strong>1. Apple Music account</strong><p>Media User Token and storefront enable catalog, search and playlists.</p><Badge state={!appleAccount ? "suggested" : !appleAccount.secret.configured || appleAccount.secret.revoked ? "degraded" : "healthy"}>{!appleAccount ? "Not connected" : !appleAccount.secret.configured || appleAccount.secret.revoked ? "Needs attention" : `Connected (${String(appleAccount.configuration?.storefront || "us")})`}</Badge></div></li>
            <li id="apple-playback-sign-in"><div><strong>2. Apple Music sign-in for playback</strong><p>The Apple ID session enables streaming, downloads and synced lyrics.</p><Badge state={appleStatus?.logged_in ? "healthy" : "suggested"}>{appleSessionLabel}</Badge>
              {#if appleStatus?.account?.maskedIdentifier}<p>Signed-in account: {appleStatus.account.maskedIdentifier}</p>{/if}
              {#if appleLoginState.includes("mismatch")}<p class="notice-error">Use the same Apple account for steps 1 and 2, then test again.</p>{/if}
              {#if appleStatusError}<p class="service-note">{appleStatusError}</p>{/if}
            </div>{#if administrator}<Button variant="secondary" onclick={() => appleDownloadOpen = true}>{appleStatus?.logged_in ? "Manage Apple Music sign-in" : "Sign in to Apple Music"}</Button>{/if}</li>
            <li><div><strong>3. Test playback</strong><p>Check the saved account and measure how quickly its audio starts.</p>{#if applePlaybackResult}<p role="status">{applePlaybackResult}</p>{/if}</div>
              {#if administrator}<Button variant="outline" disabled={!appleAccount?.canManage || Boolean(action)} onclick={() => void testApplePlayback()}>Test playback</Button>{/if}
            </li>
          </ol>
        </section>
      {/if}

      {#if (source.connectionKind === "operator_managed" || source.id === "apple-musickit") && source.configSchema?.length}
        {#if administrator}
          <form class="settings-card source-configuration-form" aria-label={`${source.name} settings`} onsubmit={event => { event.preventDefault(); if (document.activeElement instanceof HTMLInputElement) document.activeElement.blur(); }}>
            <header><div><h3>Server settings</h3><small>Shared by everyone. Changes save as you make them.</small></div></header>
            {#if source.id === "apple-musickit"}
              <AdvancedFold label="Advanced server settings">{@render serverFields(source, source.configSchema ?? [])}</AdvancedFold>
            {:else}
              {@const fields = sourceServerFields(source)}
              {@render serverFields(source, fields.standard)}
              {#if fields.advanced.length}<AdvancedFold>{@render serverFields(source, fields.advanced)}</AdvancedFold>{/if}
            {/if}

          </form>
        {:else}
          <p class="service-empty">Only an administrator can change this service's server settings.</p>
        {/if}
      {/if}

      {#if administrator}{#await import("$lib/components/ExtensionsView.svelte") then { default: ExtensionsView }}<ExtensionsView view="package" extensionId={source.id} onchanged={refreshAndFollow} />{/await}{/if}
    {/if}
  </section>
{/snippet}

{#if loading}
  <section aria-label={accountsPage ? "Loading Accounts" : "Loading Services"} aria-busy="true"><RowsSkeleton count={6} /></section>
{:else if !schema}
  <ErrorState title={`${accountsPage ? "Accounts" : "Services"} could not be loaded`} detail={error} onretry={refresh} />
{:else}
  <section class="sources-redesign" class:panel-open={Boolean(pageId)} aria-busy={refreshing}>
    <header class="sources-heading">
      <h2 class="sr-only">{accountsPage ? "Accounts" : "Services"}</h2>
      <PageStatus separated={accountsPage} label={accountsPage ? "Accounts status" : "Services status"} items={accountsPage
        ? [{ label: countLabel(accounts.length, "account") }, ...(deliverySummary.waiting ? [{ label: `${deliverySummary.waiting.toLocaleString()} waiting`, tone: "warn" as const }] : []), ...(deliverySummary.unsendable ? [{ label: `${deliverySummary.unsendable.toLocaleString()} can't be sent`, tone: "stop" as const, href: "#/sources/accounts?section=scrobble-problems" }] : [])]
        : [{ label: `${mySources.length - needsSetup.length} working` }, { label: `${countLabel(needsSetup.length, "needs", "need")} attention`, tone: needsSetup.length ? "stop" : "neutral" }, { label: `${mySources.length} in use of ${providers.length}` }]} />
      <div class="sources-heading-actions"><RefreshButton onrefresh={async () => { await refresh(); if (accountsPage) deliveryRefresh++; }} />
        {#if accountsPage}{#if canConnectAccounts}<Button onclick={() => { connectProviderId = ""; connectOpen = true; }}><Plus size={16} aria-hidden="true" />Connect account</Button>{/if}
        {:else if administrator}<Button onclick={() => addSourceOpen = true}><PackagePlus size={16} aria-hidden="true" />Add extension</Button>{/if}
      </div>
    </header>
    {#if error}<ErrorState title="Source readiness may be stale." detail={error} onretry={refresh} />{/if}
    {#if accountsPage}
      <div id="scrobble-problems" bind:this={deliverySection} tabindex="-1">{#await import("$lib/components/SourceDeliveryGroups.svelte") then { default: SourceDeliveryGroups }}<SourceDeliveryGroups {accounts} showRefresh={false} onConfigure={configure} refreshKey={String(deliveryRefresh)} onloaded={(items, complete) => { deliveries = items; deliveryCountComplete = complete; }} onchanged={() => deliveryRefresh++} />{/await}</div>
      <PlaylistConsent {backend} focus={initialSection === "playlist-editing"} />
      <SearchField hiddenLabel bind:value={search} label="Search accounts" placeholder="Search accounts" />
      {@render accountList()}
      <p class="source-filter-note">Personal accounts belong to one person; shared accounts work for everyone. Passwords and tokens are never shown again after you save them.</p>
    {:else}
      <div class="source-toolbar">
        <FilterSegments storageKey="allstarr.sources.filter" label="Filter sources" bind:value={listFilter} options={[
          { id: "attention", label: "Needs attention", count: needsSetup.length }, { id: "in-use", label: "In use", count: mySources.length },
          { id: "off", label: "Off", count: offSources.length }, { id: "all", label: "All", count: providers.length },
        ]} />
        <SearchField hiddenLabel bind:value={search} label="Search sources" placeholder="Search sources" />
        <SelectField bind:value={kind} label="Source kind" options={[{ value: "all", label: "All kinds" }, ...kinds.map(value => ({ value, label: humanize(value) }))]} />
      </div>
      <div class="service-rows" role="list" aria-label="Services" data-sveltekit-keepfocus data-sveltekit-noscroll>
        {#each filteredSources as item (item.id)}
          <div class="source-row" role="listitem">
            <ItemRow title={item.name} subtitle={sourceFact(item)} href={servicePath(item.id)} actionLabel={`Open ${item.name}`} selected={pageId === item.id} attention={needsSetup.includes(item)}>
              {#snippet leading()}<ProviderArtwork id={item.id} definition={item} decorative />{/snippet}
              {#snippet pill()}<StatePill tone={sourceTone(item)} text={sourceLabel(item)} />{/snippet}
            </ItemRow>
          </div>
        {:else}<EmptyState title={providers.length ? "No sources in this view" : "No services connected"} description={providers.length ? "Choose another filter or search for a source." : "Connect a service so Allstarr can find songs you don't have."}>{#snippet icon()}<Radio size={24} />{/snippet}</EmptyState>{/each}
      </div>
      {#if administrator}{#await import("$lib/components/ExtensionsView.svelte") then { default: ExtensionsView }}<ExtensionsView view="attention" extensionId={pageId} onchanged={refresh} />{/await}{/if}
      {#if administrator && !pageId}{#await import("$lib/components/SourceHelpers.svelte") then { default: SourceHelpers }}<SourceHelpers {schema} {config} {refresh} />{/await}{/if}
    {/if}
  </section>

  {#if pageId}
    {#await import("$lib/components/patterns/SidePanel.svelte") then { default: SidePanel }}
      <SidePanel open={true} title={panelAccount ? providerDisplayName(providers, panelAccount.providerId) : pageSource?.name ?? "Source unavailable"}
        description={panelAccount ? `${panelAccount.scope === "Personal" ? "Personal account" : "Shared account"} · ${panelAccount.ownerDisplayName || panelAccount.displayName}` : pageSource ? `${sourceOriginLabel(pageSource)} · ${(pageSource.categories ?? []).map(humanize).join(", ")}` : "This source may have been removed"}
        onclose={closePanel} previousLabel={panelAccount ? "Previous account" : "Previous source"} nextLabel={panelAccount ? "Next account" : "Next source"} onPrevious={panelIndex > 0 ? () => panelNavigate(-1) : undefined} onNext={panelIndex < panelCount - 1 ? () => panelNavigate(1) : undefined}>
        {#snippet artwork()}<ProviderArtwork id={pageId} definition={pageSource ?? undefined} decorative />{/snippet}
        <div class="source-panel-content">
          {#if panelAccount}
            {#if ["lastfm", "listenbrainz", "multi-scrobbler", "koito"].includes(panelAccount.providerId)}
              {#await import("$lib/components/SourceDeliveryGroups.svelte") then { default: SourceDeliveryGroups }}<SourceDeliveryGroups {accounts} accountId={panelAccount.id} onConfigure={configure} refreshKey={String(deliveryRefresh)} onchanged={() => deliveryRefresh++} />{/await}
            {/if}
            <dl class="source-facts"><dt>Owner</dt><dd>{panelAccount.ownerDisplayName || panelAccount.creatorDisplayName || "Unknown"}</dd><dt>Shared with</dt><dd>{accountSubtitle(panelAccount)}</dd><dt>Connected</dt><dd>{new Date(panelAccount.createdAt).toLocaleDateString()}</dd>
              {#each accountSettings(pageSource ?? { id: panelAccount.providerId, name: panelAccount.displayName }) as field}<dt>{field.label}</dt><dd>{accountSettingValue(panelAccount, field)}</dd>{/each}
            </dl>
            {#if panelAccount.canManage}
              <div class="account-send-setting"><div><strong>{["lastfm", "listenbrainz", "multi-scrobbler", "koito"].includes(panelAccount.providerId) ? "Send listens" : "Use this account"}</strong><small>Pause without removing this account</small></div>{#key `${panelAccount.id}:${panelAccount.revision}:${action}`}<Switch checked={panelAccount.enabled} aria-label="Use this account" disabled={Boolean(action)} onchange={() => void toggle(panelAccount)} />{/key}</div>
              <Button variant="outline" onclick={() => configure(panelAccount)}>Reconnect</Button>
            {/if}
            {#if panelAccount.canChangeAudience}<Button variant="text" onclick={() => manageAccess(panelAccount)}>Edit access</Button>{/if}
            {#if panelAccount.providerId === "spotify" && panelAccount.scope === "Personal" && panelAccount.enabled && panelAccount.ownerUserId === currentUserId}<Button variant="outline" href={`#/library/import?account=${encodeURIComponent(panelAccount.id)}`}>Move your Spotify collection</Button>{/if}
            {#if pageSource}<AdvancedFold placement="flush" class="source-more" label="Settings and tests" open>{@render servicePage(pageSource)}</AdvancedFold>{/if}
          {:else if pageSource}
            <div class="source-summary" data-tone={sourceTone(pageSource)} role="status"><StatePill tone={sourceTone(pageSource)} text={sourceLabel(pageSource)} /><p>{sourceFact(pageSource)}</p>{#if needsSetup.includes(pageSource)}<p>{providerHealth(pageSource.id).some(check => !check.canAttempt && check.enabled && check.supported) ? "Playback skips this source while it is paused. Other sources can still play." : "Playback can use other available sources. Open the latest checks for the next step."}</p>{/if}</div>
            {#if administrator}
              {#key pageSource.id}{#await import("$lib/components/SourceHealthHistory.svelte") then { default: SourceHealthHistory }}<SourceHealthHistory checksSupported={sourceHasChecks(pageSource, providerHealth(pageSource.id))} providerId={pageSource.id} statuses={providerHealth(pageSource.id)} automatic={pageSource.useMode !== "manualOnly" && pageSource.useMode !== "off"} refreshKey={JSON.stringify(providerHealth(pageSource.id))} onRetryWindow={refresh}>{#snippet afterStrip()}{@render sourceUseAndFacts(pageSource!)}{/snippet}</SourceHealthHistory>{/await}{/key}
            {:else}{@render sourceUseAndFacts(pageSource)}{/if}

            <AdvancedFold placement="flush" class="source-more" label="Settings, accounts and tests" bind:open={moreOpen}>{@render servicePage(pageSource)}</AdvancedFold>
          {:else}<EmptyState title="This source isn't available" description="It may have been removed or turned off." />{#if administrator}{#await import("$lib/components/ExtensionsView.svelte") then { default: ExtensionsView }}<ExtensionsView view="package" extensionId={pageId} onchanged={refreshAndFollow} />{/await}{/if}{/if}
        </div>
        {#snippet footer()}
          {#if sourceUndo}<div class="source-setting-undo" role="status"><span>{providerDisplayName(providers, sourceUndo.providerId)} setting saved.</span><Button variant="text" disabled={undoing} onclick={() => void undoSourceSetting()}>{undoing ? "Restoring…" : "Undo"}</Button></div>{/if}
          <div class="source-panel-footer">
            {#if panelAccount?.canManage}<Button variant="text" class="source-remove" onclick={() => { removal = panelAccount ?? null; removeOpen = true; }}>Remove…</Button>{/if}
            {#if !panelAccount && pageSource}
              {#if administrator && pageSource.implementationOrigin === "extension"}{#await import("$lib/components/ExtensionsView.svelte") then { default: ExtensionsView }}<ExtensionsView view="remove" extensionId={pageSource.id} onchanged={refreshAndFollow} />{/await}{/if}
              <Button variant="outline" href="#/sources/accounts">Accounts</Button>
            {/if}
            {#if administrator && pageSource && (!panelAccount || !["lastfm", "listenbrainz", "multi-scrobbler", "koito"].includes(panelAccount.providerId))}<Button disabled={Boolean(action) || (panelAccount ? !panelAccount.canManage : !canCheckSource(pageSource))} onclick={() => panelAccount ? void test(panelAccount) : void checkSource(pageSource!)}>{action ? "Checking…" : "Check now"}</Button>{/if}
          </div>
        {/snippet}
      </SidePanel>
    {/await}
  {/if}

  {#if ConnectSourceDialog}
    <ConnectSourceDialog bind:open={connectOpen} {providers} administrator={canManageAllAccounts} users={audienceUsers} {currentUserId} testConnection={administrator} initialProviderId={connectProviderId} onSaved={completed} />
    <ConnectSourceDialog bind:open={configureOpen} {providers} administrator={canManageAllAccounts} users={audienceUsers} {currentUserId} testConnection={administrator} account={selectedAccount} onSaved={completed} />
  {/if}
  {#if ProviderTryDialog && tryTarget}<ProviderTryDialog bind:open={tryOpen} provider={tryTarget.provider} account={tryTarget.account} capability={tryTarget.capability} searchSources={lyricSearchSources} providerDefinitions={providers} />{/if}
  {#if AppleDownloadDialog}<AppleDownloadDialog bind:open={appleDownloadOpen} onStatus={(status) => { appleRequest++; appleStatus = status; appleStatusError = ""; }} />{/if}
  {#if administrator && addSourceOpen}{#await import("$lib/components/ExtensionsView.svelte") then { default: ExtensionsView }}<ExtensionsView view="catalog" bind:open={addSourceOpen} onchanged={refresh} />{/await}{/if}
  {#if AccountAccessDialog}<AccountAccessDialog bind:open={accessOpen} account={selectedAccount} users={audienceUsers} onSaved={completed} />{/if}

  <ConfirmDialog
    bind:open={removeOpen}
    title={`Remove ${removal?.sourceDisplayName || removal?.displayName || "this account"}?`}
    description="This account stops being used. Your listening history stays. You can connect it again later."
    confirmLabel="Remove account" disabled={Boolean(action)} closeOnConfirm={false}
    onConfirm={remove}
  />
{/if}

<style>
  :global(.bits-menu-heading) { padding: .5rem .7rem .25rem; color: var(--color-ink-muted); font-size: .6875rem; font-weight: 500; }
  #scrobble-problems:has(:global(.delivery-groups[hidden])) { display: none; }
  .sources-redesign { display: grid; min-width: 0; gap: var(--space-4); }
  .sources-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
  .sources-heading :global(.status-items [data-tone="stop"]), .sources-heading :global(.status-items [data-tone="stop"] strong) { color: var(--danger); }
  .sources-heading-actions, .panel-secondary-actions { min-width: 0; max-width: 100%; display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-2); }
  .sources-heading-actions :global([data-slot="button"]) { min-width: 44px; min-height: 44px; max-width: 100%; }
  .source-toolbar { display: flex; min-width: 0; align-items: center; flex-wrap: wrap; gap: var(--space-3); }
  .source-toolbar :global(.search-field) { flex: 1; min-width: 10rem; }
  .service-rows, .accounts-table-wrap { overflow: hidden; border: 1px solid var(--color-edge); border-radius: var(--radius-lg); }
  .accounts-table-wrap { container-type: inline-size; }
  .service-rows .source-row { display: block; padding: 0; border: 0; }
  .source-row + .source-row { border-top: 1px solid var(--color-edge); }
  .accounts-table { width: 100%; border-collapse: collapse; background: var(--color-panel); font-size: var(--text-sm); table-layout: fixed; }
  .accounts-table th:first-child { width: 30%; }
  .accounts-table th:nth-child(5) { width: 8rem; }
  .account-menus { display: flex; flex-wrap: wrap; position: relative; z-index: 1; }
  .accounts-table th:last-child { width: calc(44px + var(--space-3) * 2); }
  .accounts-table td { overflow-wrap: anywhere; }
  .accounts-table td[data-label="State"] :global(.state-pill) { max-width: 100%; white-space: normal; }
  .account-opener strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .accounts-table th { color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 500; text-align: left; }
  .accounts-table th, .accounts-table td { padding: var(--space-2) var(--space-3); border-bottom: 1px solid var(--color-edge); }
  .accounts-table tbody tr { position: relative; }
  .accounts-table tbody tr:last-child td { border-bottom: 0; }
  .accounts-table tbody tr:hover, .account-selected { background: var(--color-panel-raised); }
  .account-opener { display: flex; align-items: center; gap: var(--space-3); min-height: 44px; color: var(--color-ink); }
  .account-opener::after { position: absolute; inset: 0; content: ""; }
  .accounts-table td:last-child { position: relative; pointer-events: none; }
  .accounts-table td:last-child :global(button) { pointer-events: auto; }
  .account-opener :global(.provider-art) { width: 28px; height: 28px; flex-shrink: 0; }
  .accounts-table small { display: block; color: var(--color-ink-muted); }
  .source-filter-note, .muted { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .source-panel-content { display: grid; min-width: 0; gap: var(--space-5); }
  .source-summary { display: grid; gap: var(--space-2); border-radius: var(--radius-md); padding: var(--space-3); background: var(--color-panel-raised); }
  .source-summary[data-tone="stop"] { background: var(--md-sys-color-error-container); color: var(--md-sys-color-on-error-container); }
  .source-summary[data-tone="warn"] { background: var(--warning-container); color: var(--on-warning-container); }
  .source-summary[data-tone="stop"] p, .source-summary[data-tone="warn"] p { color: inherit; }
  .source-summary p { margin: 0; font-size: var(--text-sm); color: var(--color-ink-muted); }
  .source-facts { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.5fr); gap: var(--space-3); margin: 0; font-size: var(--text-sm); }
  .source-facts dt { color: var(--color-ink-muted); }.source-facts dd { margin: 0; overflow-wrap: anywhere; }
  .source-setting-undo { display: flex; width: 100%; min-width: 0; align-items: center; justify-content: space-between; gap: var(--space-2); font-size: var(--text-sm); }
  .source-setting-undo span { overflow-wrap: anywhere; }
  .source-panel-footer { display: flex; width: 100%; gap: var(--space-2); justify-content: flex-end; flex-wrap: wrap; }
  .source-panel-footer :global(.source-remove) { margin-right: auto; color: var(--danger); }
  .account-send-setting { display: flex; gap: var(--space-3); align-items: center; justify-content: space-between; }
  .account-send-setting strong, .account-send-setting small { display: block; font-size: var(--text-sm); }.account-send-setting small { color: var(--color-ink-muted); }
  .service-advanced { padding: var(--space-3) 0 0; min-width: 0; display: grid; gap: var(--space-3); }
  /* Each card in a service's settings is outlined like its server settings, so card padding never reads as a stray indent. */
  .service-advanced > :global(.settings-card) { border: 1px solid var(--color-edge); border-radius: var(--radius-md); }
  .service-description { margin: 0; font-size: var(--text-sm); color: var(--color-ink-muted); }
  .source-panel-content :global(.settings-row), .source-panel-content :global(.service-check) { display: flex; align-items: center; flex-wrap: wrap; gap: var(--space-2); }
  .source-panel-content :global(.settings-card) { min-width: 0; }
  .source-panel-content :global(button), .source-panel-content :global([data-slot="button"]) { min-height: 44px; }
  :global(.side-panel:has(.source-panel-content) .panel-controls button) { width: 44px; height: 44px; min-width: 44px; min-height: 44px; }
  :global(.side-panel:has(.source-panel-content) .panel-footer) { flex-wrap: wrap; gap: var(--space-2); }
  .source-panel-content :global(.service-account header), .source-panel-content :global(.service-account-actions) { flex-wrap: wrap; }
  .source-panel-content :global(.extension-row) { display: flex; flex-wrap: wrap; }
  .source-panel-footer :global(.extension-remove-link) { margin-right: auto; }
  .source-panel-content :global(output) { display: flex; align-items: center; gap: var(--space-2); overflow-wrap: anywhere; }
  @media (max-width: 899px) {
    .source-toolbar :global(.search-field) { flex: 1 1 10rem; min-width: min(10rem, 100%); }
    .source-toolbar :global(.search-field input) { width: 100%; min-width: 0; }
  }
  @media (max-width: 650px) {
    .sources-heading-actions { display: grid; width: 100%; grid-template-columns: 48px minmax(0, 1fr); }
    .sources-heading-actions :global([data-slot="button"]) { width: 100%; white-space: normal; }
    .sources-heading-actions :global(button), .service-advanced :global(button), .service-advanced :global([data-slot="button"]) { min-height: 44px; }
  }
  @media (max-width: 380px) { .sources-heading-actions { grid-template-columns: minmax(0, 1fr); } }
  /* Beside a docked panel the table keeps its rows and drops the audience column; only phones get stacked rows. */
  @container (min-width: 600px) and (max-width: 700px) { .accounts-table .shared-column, .accounts-table td[data-label="Shared with"] { display: none; } }
  @container (max-width: 599px) {
    .accounts-table, .accounts-table tbody, .accounts-table tr, .accounts-table td { display: block; }
    .accounts-table td:empty { display: none; }
    .accounts-table thead { display: none; }.accounts-table tr { display: grid; grid-template-columns: minmax(0, 1fr) auto; padding: var(--space-3); border-bottom: 1px solid var(--color-edge); }
    .accounts-table tr:last-child { border: 0; }.accounts-table td { padding: var(--space-1); border: 0; min-width: 0; }.accounts-table td:first-child { grid-column: 1; }.accounts-table td:nth-child(2), .accounts-table td:nth-child(3), .accounts-table td:nth-child(4) { grid-column: 1; padding-left: calc(28px + var(--space-4)); color: var(--color-ink-muted); font-size: var(--text-xs); }
    .accounts-table td:nth-child(5) { grid-column: 2; grid-row: 1; align-self: center; }.accounts-table td:last-child { grid-column: 2; grid-row: 2 / 5; justify-self: end; }
  }
  @media (max-width: 599px) { .source-toolbar { align-items: stretch; }.source-toolbar :global(.seg.filter-segments) { width: 100%; flex-wrap: nowrap; overflow-x: auto; } }
  .source-setting-control {
    display: flex;
    min-width: 0;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--space-2);
  }

  .source-setting-control input {
    flex: 1 1 12rem;
    min-width: 0;
  }

  @layer components {
    :global {
      .service-check-actions {
        display: inline-flex;
        align-items: center;
      }

      .service-check-actions > :last-child:not(:only-child) {
        border-inline-start-color: color-mix(in srgb, var(--md-sys-color-on-secondary-container) 22%, transparent);
      }

      .service-check-actions > [data-slot="button"] {
        min-width: var(--control-md);
        min-height: var(--control-md);
      }
    }
  }
</style>
