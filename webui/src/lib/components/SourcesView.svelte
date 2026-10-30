<script lang="ts">
  import ServicePolicyCard from "$lib/components/ServicePolicyCard.svelte";
  import ProviderUseControl from "$lib/components/ProviderUseControl.svelte";
  import { diagnosticFailureMessage, diagnosticIsNeutral, type DiagnosticFailure } from "$lib/diagnostics";
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { ArrowLeft, ChevronRight, MoreHorizontal, PackagePlus, Plus } from "@lucide/svelte";
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
    type ProviderSummary,
    type UiSchema,
  } from "$lib/api";
  import ScrobbleDeliveries from "$lib/components/ScrobbleDeliveries.svelte";
  import AccountAccessDialog from "$lib/components/AccountAccessDialog.svelte";
  import AppleDownloadDialog from "$lib/components/AppleDownloadDialog.svelte";
  import ConnectSourceDialog from "$lib/components/ConnectSourceDialog.svelte";
  import ProviderTryDialog from "$lib/components/ProviderTryDialog.svelte";
  import ConnectivityBars from "$lib/components/ConnectivityBars.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import PlaylistConsent from "$lib/components/PlaylistConsent.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import ExtensionsView from "$lib/components/ExtensionsView.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { fieldValue } from "$lib/settings";
  import { relativeTime } from "$lib/activity";
  import {
    accountSettings,
    accountSubtitle,
    accountOwnershipLabel,
    providerDisplayName,
    audienceLabel,
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
    sourceNeedsAccount,
    sourceOriginLabel,
    sourceStatus,
    sourceIsInUse,
    sourceUpdateNeedsRefresh,
    sourceTimingLabel,
    settingDefault,
    sourceServerFields,
    supportsPlaybackDiagnostic,
  } from "$lib/sources";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";

  let {
    mode = "services",
    administrator,
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
  let listFilter = $state<"mine" | "all" | "attention" | "accounts">("mine");

  $effect(() => {
    listFilter = mode === "accounts" ? "accounts" : "mine";
  });
  let addSourceOpen = $state(false);
  let removal = $state<ProviderAccount | null>(null);
  let removeOpen = $state(false);
  let testResults = $state<Record<string, ConnectivityResult>>({});
  let consumedSourceLink = "";

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
  const canConnectAccounts = $derived(administrator || listenersCanConnectOwnAccounts);
  const mySources = $derived(providers.filter(item => sourceIsInUse(item, accounts, health)));
  const needsSetup = $derived(mySources.filter((item) =>
    ["degraded", "needs_verification", "needs_config", "partial_config"].includes(sourceStatus(item, accounts, health))));
  const pageId = $derived(serviceId || (initialSource && !(initialConnect && mode === "accounts") ? initialSource : ""));
  const pageSource = $derived(pageId ? provider(pageId) ?? null : null);
  const backHref = $derived(from === "accounts" ? "#/integrations/accounts" : "#/integrations/services");
  const backLabel = $derived(from === "accounts" ? "Accounts" : "Sources");

  $effect(() => {
    selectedSource = pageSource;
  });

  function servicePath(id: string, account?: string) {
    const query = new URLSearchParams();
    if (listFilter === "accounts") query.set("from", "accounts");
    if (account) query.set("account", account);
    const search = query.toString();
    return `#/integrations/services/${encodeURIComponent(id)}${search ? `?${search}` : ""}`;
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
    if (item.description) return item.description;
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

  function implementationLabel(item: ProviderDefinition) {
    const count = item.capabilityRoutes?.length ?? 0;
    return count > 1 ? `${count} interchangeable implementations` : `${sourceOriginLabel(item)} implementation`;
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

  async function saveSourceConfiguration(event: SubmitEvent) {
    event.preventDefault();
    if (!selectedSource || action) return;
    action = `configure:${selectedSource.id}`;
    const fields = selectedSource.configSchema ?? [];
    const data = new FormData(event.currentTarget as HTMLFormElement);
    const updates = Object.fromEntries(fields
      .filter((field) => !field.readOnly && field.ownership !== "deployment")
      .map((field) => [
        field.key,
        field.type === "toggle" ? String(data.get(field.key) === "on") : String(data.get(field.key) ?? ""),
      ]));
    try {
      await settings.save(updates);
      toast.success(`${selectedSource.name} configuration saved.`);
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Source configuration could not be saved.");
    } finally {
      action = "";
    }
  }

  function resetServerField(event: MouseEvent, field: ConfigField) {
    const input = (event.currentTarget as HTMLButtonElement).form?.elements.namedItem(field.key);
    if (input instanceof HTMLInputElement) input.value = String(settingDefault(field));
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
      await completed(`${account.sourceDisplayName || account.displayName} ${account.enabled ? "disabled" : "enabled"}.`);
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
      if (result.seekRung) message += ` Playback: ${humanize(result.seekRung)}${result.sampleBytes ? ` (${result.sampleBytes.toLocaleString()} bytes sampled)` : ""}.`;
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
      await completed(`${removal.sourceDisplayName || removal.displayName} removed.`);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The saved connection could not be removed.");
    } finally {
      action = "";
    }
  }

  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(update => {
      if (sourceUpdateNeedsRefresh(update)) scheduleRefresh();
    });
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
    };
  });
</script>

{#snippet accountList()}
      <ul class="source-list account-list" aria-label={`${accounts.length} account${accounts.length === 1 ? "" : "s"}`}>
        {#each accounts as account (account.id)}
          {@const definition = provider(account.providerId)}
          {@const capabilities = health.filter((item) => item.providerAccountId === account.id)}
          {@const cts = measurements.find((item) => item.providerAccountId === account.id)}
          {@const ready = capabilities.filter((item) => item.ready).length}
          <li class="source-row" id={`connection-${account.id}`}>
            <a class="source-row-main" href={servicePath(account.providerId, account.id)} aria-label={`Open ${providerDisplayName(providers, account.providerId)} account`}>
              <ProviderArtwork id={account.providerId} definition={definition} />
              <span class="source-row-copy">
                <span class="source-name"><strong>{providerDisplayName(providers, account.providerId)}</strong>{#if definition}<Badge tone="neutral" class="origin-badge">{sourceOriginLabel(definition)}</Badge>{/if}</span>
                <small>{accountOwnershipLabel(account)}{definition?.usesSignedSession || account.secret.configured && !account.secret.revoked ? "" : " · Setup needed"}</small>
              </span>
              <span class="source-row-owner">
                <small>Added by</small>
                {account.creatorDisplayName || account.ownerDisplayName || "Unknown"}
              </span>
              <span class="source-row-status">
                <Badge state={!account.enabled ? "disabled" : readinessClass(capabilities.length > 0 && capabilities.every((item) => item.ready), capabilities.some((item) => item.health === "degraded") ? "degraded" : null)}>
                  {!account.enabled ? "Turned off" : capabilities.length && ready === capabilities.length ? "Working" : `${ready} of ${capabilities.length} ready`}
                </Badge>
                {#if cts}<small>CTS {ctsMeasurementLabel(cts)}</small>{/if}
              </span>
              <ChevronRight class="source-row-chevron" size={18} aria-hidden="true" />
            </a>
            {#if account.canManage || account.canChangeAudience}
              <DropdownMenu.Root>
                <DropdownMenu.Trigger class="icon-button source-row-menu" disabled={Boolean(action)} aria-label={`Actions for ${account.displayName}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                <DropdownMenu.Portal>
                  <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                    {#if account.canChangeAudience}
                      <DropdownMenu.Item class="bits-menu-item" onSelect={() => manageAccess(account)}>Edit access</DropdownMenu.Item>
                    {/if}
                    {#if account.canManage}<DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void toggle(account)}>{account.enabled ? "Disable" : "Enable"}</DropdownMenu.Item>{/if}
                    <DropdownMenu.Separator />
                    {#if account.canManage}<DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action)} onSelect={() => { removal = account; removeOpen = true; }}>Remove</DropdownMenu.Item>{/if}
                  </DropdownMenu.Content>
                </DropdownMenu.Portal>
              </DropdownMenu.Root>
            {:else}<span class="source-row-readonly">Read only</span>{/if}
          </li>
        {:else}
          <li class="compact-empty connections-empty">
            <strong>No accounts connected</strong>
            <p>Connect Spotify, Deezer or another service to import playlists and play songs you don't have locally.</p>
            {#if canConnectAccounts}<Button onclick={() => { connectProviderId = ""; connectOpen = true; }}>Connect Source</Button>{/if}
          </li>
        {/each}
      </ul>
{/snippet}

{#snippet checkRow(label: string, check: Omit<SourceCheck, "id">, result?: ConnectivityResult, onTest?: () => void, canTest = false, testKey = "", onTry?: () => void)}
  {@const guide = check.ready ? null : checkGuide(check.reasonCode)}
  <li class="service-check">
    <span class="service-check-name">{label}</span>
    <span class="service-check-status"><Badge state={checkStatusState(check)}>{checkStatusLabel(check)}</Badge>{#if guide}<small>{guide.step}</small>{#if guide.signIn}<button type="button" class="text-button" onclick={goToSignIn}>Go to sign-in</button>{/if}{/if}{#if check.reasonCode === "apple-download-sign-in-required"}{#if administrator}<button type="button" class="text-button" onclick={() => appleDownloadOpen = true}>Open step 2: Apple Music sign-in</button>{:else}<a href="#/integrations/services/apple-musickit">See step 2: Apple Music sign-in</a>{/if}{/if}</span>
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
                  <output>{field.sensitive ? "Stored" : String(fieldValue(config, field))}</output>
                {:else if field.type === "select"}
                  <SelectField name={field.key} label={source.id === "apple-musickit" ? "Optional playback gateway URL" : field.label} value={field.sensitive ? "" : String(fieldValue(config, field))} options={field.options ?? []} />
                {:else if field.type === "toggle"}
                  <span class="settings-toggle"><Checkbox id={field.key} name={field.key} checked={Boolean(fieldValue(config, field))} /></span>
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
  {@const state = sourceStatus(source, accounts, health)}
  {@const metrics = sourceMetrics(source, providerHealth(source.id))}
  {@const sourceAccounts = providerAccounts(source.id)}
  {@const sourceCts = measurements.find((item) => item.providerId.toLowerCase() === source.id.toLowerCase() && !item.providerAccountId)}
  {@const fields = accountSettings(source)}
  {@const streams = source.categories?.some((item) => ["streaming", "download"].includes(item.toLowerCase())) ?? false}
  {@const usesAccounts = sourceNeedsAccount(source) || fields.length > 0 || sourceAccounts.length > 0}
  <section class="service-page" aria-labelledby="service-title">
    <a class="service-back" href={backHref}><ArrowLeft size={16} aria-hidden="true" />{backLabel}</a>
    <header class="service-head">
      <ProviderArtwork id={source.id} definition={source} />
      <div class="service-head-copy">
        <div class="source-name source-title"><h2 id="service-title">{source.name}</h2><Badge tone="neutral" class="origin-badge">{sourceOriginLabel(source)}</Badge></div>
        <p>{sourcePurpose(source)}</p>
        <div class="service-head-meta">
          <Badge state={state}>{state === "degraded" ? sourceProblemLabel(source, accounts, health) ?? sourceStatusLabel(state) : sourceStatusLabel(state)}</Badge>
          <span>{metrics.checkedAt ? `${metrics.passing} of ${metrics.total || 0} checks passed · ${relativeTime(metrics.checkedAt)}` : "Not checked yet"}</span>
        </div>
      </div>
      <div class="sources-heading-actions">
        {#if administrator && streams && !sourceNeedsAccount(source)}
          <Button variant="secondary" disabled={Boolean(action)} onclick={() => void measureSource(source)}>{action === `${source.id}:cts` ? "Measuring…" : "Measure CTS"}</Button>
        {/if}
        {#if canConnectAccounts && usesAccounts && source.id !== "audiomuse-ai"}
          <Button onclick={() => { connectProviderId = source.id; connectOpen = true; }}><Plus size={16} aria-hidden="true" />{sourceAccounts.length ? "Connect another account" : "Connect account"}</Button>
        {/if}
      </div>
    </header>

    <ProviderUseControl providerId={source.id} {administrator} onSaved={refresh} />

    <section class="settings-card service-overview" aria-label="Overview">
      <dl>
        <div><dt>Status</dt><dd>{state === "degraded" ? sourceProblemLabel(source, accounts, health) ?? sourceStatusLabel(state) : sourceStatusLabel(state)}</dd></div>
        <div><dt>Checks</dt><dd>{metrics.total ? `${metrics.passing} passed · ${metrics.failed} failing` : "None yet"}</dd></div>
        <div><dt>Last checked</dt><dd>{relativeTime(metrics.checkedAt, "Not checked yet")}</dd></div>
        <div><dt>Response time</dt><dd>{sourceTimingLabel(source, summary(source.id))}</dd></div>
        <div><dt>Click to stream</dt><dd>{#if sourceCts}{ctsMeasurementLabel(sourceCts)} · {relativeTime(sourceCts.testedAt)}{:else if streams}Not measured yet{:else}Not applicable{/if}</dd></div>
        <div><dt>Built from</dt><dd>{implementationLabel(source)}</dd></div>
      </dl>
    </section>

    {#if administrator}
      {#key source.id}
        {#await import("$lib/components/SourceHealthHistory.svelte") then { default: SourceHealthHistory }}
          <SourceHealthHistory providerId={source.id} statuses={providerHealth(source.id)} automatic={source.useMode !== "manualOnly" && source.useMode !== "off"}
            refreshKey={JSON.stringify(providerHealth(source.id))} onRetryWindow={refresh} />
        {/await}
      {/key}
    {/if}

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
          {#if administrator}<Button href="#/settings/sound-analysis">Configure</Button>{/if}
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
                  {#if administrator && account.canManage && streams}<Button variant="secondary" size="sm" disabled={!account.enabled || Boolean(action)} onclick={() => void measure(account)}>{action === `${account.id}:cts` ? "Measuring…" : "Measure CTS"}</Button>{/if}
                  {#if account.canManage}<Button size="sm" onclick={() => configure(account)}>Edit configuration</Button>{/if}
                  {#if account.canManage || account.canChangeAudience}
                    <DropdownMenu.Root>
                      <DropdownMenu.Trigger class="icon-button" disabled={Boolean(action)} aria-label={`Actions for ${account.displayName}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                      <DropdownMenu.Portal>
                        <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
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

      {#if ["lastfm", "listenbrainz", "multi-scrobbler", "koito"].includes(source.id)}
        {#key source.id}<ScrobbleDeliveries {accounts} targetId={source.id} onConfigure={configure} />{/key}
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
              {#if administrator}<Button disabled={!appleAccount?.canManage || Boolean(action)} onclick={() => void testApplePlayback()}>Test playback</Button>{/if}
            </li>
          </ol>
        </section>
      {/if}

      {#if (source.connectionKind === "operator_managed" || source.id === "apple-musickit") && source.configSchema?.length}
        {#if administrator}
          <form class="settings-card source-configuration-form" aria-label={`${source.name} settings`} onsubmit={(event) => void saveSourceConfiguration(event)}>
            <header><div><h3>Server settings</h3><small>Shared by everyone using this service.</small></div></header>
            {#if source.id === "apple-musickit"}
              <details class="source-advanced-settings"><summary>Advanced server settings</summary>{@render serverFields(source, source.configSchema ?? [])}</details>
            {:else}
              {@const fields = sourceServerFields(source)}
              {@render serverFields(source, fields.standard)}
              {#if fields.advanced.length}<details class="source-advanced-settings"><summary>Advanced</summary>{@render serverFields(source, fields.advanced)}</details>{/if}
            {/if}
            {#if source.id !== "apple-musickit" || !(config.appleDownload as { endpointManagedByDeployment?: boolean })?.endpointManagedByDeployment}<footer><Button type="submit" disabled={Boolean(action)}>{action === `configure:${source.id}` ? "Saving…" : "Save configuration"}</Button></footer>{/if}
          </form>
        {:else}
          <p class="service-empty">Only an administrator can change this service's server settings.</p>
        {/if}
      {/if}

      {#if administrator}<ExtensionsView view="package" extensionId={source.id} onchanged={refreshAndFollow} />{/if}
    {/if}
  </section>
{/snippet}

{#if loading}
  <Skeleton class="panel sources-panel skeleton-panel" aria-label={`Loading ${mode === "accounts" ? "Accounts" : "Services"}`} aria-busy="true" />
{:else if !schema}
  <RouteError
    eyebrow={`${mode === "accounts" ? "Accounts" : "Services"} unavailable`}
    title={`Allstarr could not load ${mode === "accounts" ? "provider accounts" : "the Service catalog"}.`}
    message={error}
    onRetry={refresh}
  />
{:else}
  {#if error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span><p><strong>Source readiness may be stale.</strong> {error}</p>
      <Button variant="secondary" size="sm" disabled={refreshing} onclick={() => void refresh()}>{refreshing ? "Trying again…" : "Retry"}</Button>
    </div>
  {/if}


  {#if mode === "accounts" && !pageId}
    <ScrobbleDeliveries {accounts} onConfigure={configure} />
  {/if}

  <div class="sources-layout" aria-busy={refreshing}>
    {#if pageId}
      {#if pageSource}
        {@render servicePage(pageSource)}
      {:else}
        <section class="service-page">
          <a class="service-back" href={backHref}><ArrowLeft size={16} aria-hidden="true" />{backLabel}</a>
          <div class="compact-empty"><strong>This source isn't available</strong><p>It may have been removed or turned off.</p></div>
          {#if administrator}<ExtensionsView view="package" extensionId={pageId} onchanged={refreshAndFollow} />{/if}
        </section>
      {/if}
    {:else}
    <section class="sources-panel" aria-labelledby="services-title">
      <header class="sources-heading">
        <div>
          <h2 id="services-title" class="sr-only">Sources</h2>
          <p>Everywhere Allstarr finds music, playlists, lyrics and scrobbling. Open a source to see its checks, accounts and settings.</p>
        </div>
        <div class="sources-heading-actions">
          <RefreshButton variant="secondary" onrefresh={refresh} />
          {#if administrator}<Button variant="secondary" onclick={() => { addSourceOpen = true; }}><PackagePlus size={16} aria-hidden="true" />Add a source</Button>{/if}
          {#if canConnectAccounts}<Button onclick={() => { connectProviderId = ""; connectOpen = true; }}><Plus size={16} aria-hidden="true" />Connect Source</Button>{/if}
        </div>
      </header>

      {#if administrator}<ExtensionsView view="attention" onchanged={refresh} />{/if}

      <div class="source-filters" role="group" aria-label="Show">
        <button type="button" class="filter-chip" aria-pressed={listFilter === "mine"} onclick={() => listFilter = "mine"}>My sources <span class="segmented-tab-count">{mySources.length}</span></button>
        <button type="button" class="filter-chip" aria-pressed={listFilter === "attention"} onclick={() => listFilter = "attention"}>Needs attention <span class="segmented-tab-count">{needsSetup.length}</span></button>
        <button type="button" class="filter-chip" aria-pressed={listFilter === "accounts"} onclick={() => listFilter = "accounts"}>Accounts <span class="segmented-tab-count">{accounts.length}</span></button>
        <button type="button" class="filter-chip" aria-pressed={listFilter === "all"} onclick={() => listFilter = "all"}>All available <span class="segmented-tab-count">{providers.length}</span></button>
      </div>

      {#if listFilter === "accounts"}
        <PlaylistConsent />
        <p class="source-filter-note">Personal accounts belong to one person. Shared accounts work for everyone. Passwords and tokens are never shown again after you save them.</p>
        {@render accountList()}
      {:else}
      <ul class="source-list" aria-label="Services">
        {#each listFilter === "attention" ? needsSetup : listFilter === "mine" ? mySources : providers as item (item.id)}
          {@const state = sourceStatus(item, accounts, health)}
          {@const metrics = sourceMetrics(item, providerHealth(item.id))}
          {@const connected = providerAccounts(item.id)}
          {@const cts = measurements.find((measurement) =>
            measurement.providerId.toLowerCase() === item.id.toLowerCase() &&
            (!measurement.providerAccountId || connected.some((account) => account.id === measurement.providerAccountId)))}
          {@const timing = sourceTimingLabel(item, summary(item.id))}
          {@const appleReadiness = item.runtimeCapabilities?.find((capability) =>
            capability.reasonCode?.startsWith("apple-download-"))?.reasonCode}
          <li class="source-row" data-state={state}>
            <a class="source-row-main" href={servicePath(item.id)}>
              <ProviderArtwork id={item.id} definition={item} />
              <span class="source-row-copy">
                <span class="source-name"><strong>{item.name}</strong><Badge tone="neutral" class="origin-badge">{sourceOriginLabel(item)}</Badge>{#if item.useMode === "manualOnly"}<Badge tone="neutral" class="origin-badge">Manual only</Badge>{:else if item.useMode === "off"}<Badge tone="neutral" class="origin-badge">Off</Badge>{/if}</span>
                <small>{sourcePurpose(item)}</small>
                {#if appleReadiness}<small class="source-row-warning" role="status">{providerReadinessLabel(appleReadiness)}</small>{/if}
              </span>
              <span class="source-row-capabilities">
                {#each (item.categories ?? []).slice(0, 4) as category}<span>{humanize(category)}</span>{/each}
              </span>
              <span class="source-row-status">
                <Badge state={state}>{state === "degraded" ? sourceProblemLabel(item, accounts, health) ?? sourceStatusLabel(state) : sourceStatusLabel(state)}</Badge>
                <small>{metrics.checkedAt ? `${metrics.passing} of ${metrics.total || 0} checks passed · ${relativeTime(metrics.checkedAt)}` : "Not checked yet"}</small>
                <small class="source-row-timing">{connected.length ? `${connected.filter((account) => account.enabled).length} of ${connected.length} account${connected.length === 1 ? "" : "s"} on · ` : ""}{timing}{#if cts} · CTS {ctsMeasurementLabel(cts)}{/if}</small>
              </span>
              <ChevronRight class="source-row-chevron" size={18} aria-hidden="true" />
            </a>
          </li>
        {:else}
          <li class="compact-empty"><strong>No services yet</strong><p>Turn on a built-in service or install an extension to add one.</p></li>
        {/each}
      </ul>
      {/if}
    </section>
    {/if}

  </div>

  {#if administrator && mode === "services" && !pageId && schema}
    <ServicePolicyCard fields={(schema.configSections ?? []).flatMap((section) => section.fields).filter((field) => /^(ODESLI_|EXTENSIONS_ALLOW_REMOTE_INSTALL)/.test(field.key))} {config} />
  {/if}

  <ConnectSourceDialog bind:open={connectOpen} {providers} administrator={canManageAllAccounts} users={audienceUsers} {currentUserId} testConnection={administrator} initialProviderId={connectProviderId} onSaved={completed} />
  <ConnectSourceDialog bind:open={configureOpen} {providers} administrator={canManageAllAccounts} users={audienceUsers} {currentUserId} testConnection={administrator} account={selectedAccount} onSaved={completed} />
  {#if tryTarget}<ProviderTryDialog bind:open={tryOpen} provider={tryTarget.provider} account={tryTarget.account} capability={tryTarget.capability} searchSources={lyricSearchSources} providerDefinitions={providers} />{/if}
  <AppleDownloadDialog bind:open={appleDownloadOpen} onStatus={(status) => { appleRequest++; appleStatus = status; appleStatusError = ""; }} />
  {#if administrator}<ExtensionsView view="catalog" bind:open={addSourceOpen} onchanged={refresh} />{/if}
  <AccountAccessDialog bind:open={accessOpen} account={selectedAccount} users={audienceUsers} onSaved={completed} />

  <ConfirmDialog
    bind:open={removeOpen}
    title="Remove this Source connection?"
    description="The encrypted credential is revoked and this account can no longer route provider requests. Audit history remains."
    confirmLabel="Remove connection"
    onConfirm={remove}
  />
{/if}

<style>
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
