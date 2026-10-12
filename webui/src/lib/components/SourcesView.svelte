<script lang="ts">
  import { onMount } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { ChevronRight, MoreHorizontal, Plus, RefreshCw, X } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import {
    home,
    settings,
    sources,
    type ConnectivityResult,
    type CtsMeasurement,
    type ProviderAccount,
    type ProviderDefinition,
    type ProviderHealth,
    type ProviderSetting,
    type ProviderSummary,
    type UiSchema,
  } from "$lib/api";
  import AccountAccessDialog from "$lib/components/AccountAccessDialog.svelte";
  import AppleDownloadDialog from "$lib/components/AppleDownloadDialog.svelte";
  import ConnectSourceDialog from "$lib/components/ConnectSourceDialog.svelte";
  import ConnectivityBars from "$lib/components/ConnectivityBars.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import PlaylistConsent from "$lib/components/PlaylistConsent.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { fieldValue } from "$lib/settings";
  import { relativeTime } from "$lib/activity";
  import {
    accountSettings,
    accountSubtitle,
    providerDisplayName,
    audienceLabel,
    ctsMeasurementLabel,
    humanize,
    providerReadinessLabel,
    sourceMetrics,
    sourceNeedsAccount,
    sourceOriginLabel,
    sourceStatus,
    sourceTimingLabel,
    settingDefault,
    supportsPlaybackDiagnostic,
  } from "$lib/sources";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";

  let {
    mode = "services",
    administrator,
    initialSource = "",
    initialSection = "data",
    initialConnect = false,
  }: {
    mode?: "services" | "accounts";
    administrator: boolean;
    initialSource?: string;
    initialSection?: string;
    initialConnect?: boolean;
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
  let feedback = $state("");
  let action = $state("");
  let connectOpen = $state(false);
  let connectProviderId = $state("");
  let appleDownloadOpen = $state(false);
  let configureOpen = $state(false);
  let accessOpen = $state(false);
  let selectedAccount = $state<ProviderAccount | null>(null);
  let selectedSource = $state<ProviderDefinition | null>(null);
  let detailOpen = $state(false);
  let detailKind = $state<"source" | "account">("source");
  let detailTab = $state("data");
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
      const order = { degraded: 0, needs_config: 1, partial_config: 1, available: 2, configured: 3, healthy: 4, disabled: 5 };
      return section(left) - section(right) ||
        (order[sourceStatus(left, accounts, health) as keyof typeof order] ?? 2) -
        (order[sourceStatus(right, accounts, health) as keyof typeof order] ?? 2) ||
        left.name.localeCompare(right.name);
    }),
  );
  const canConnectAccounts = $derived(administrator || listenersCanConnectOwnAccounts);
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

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
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
    refreshing = false;
  }

  const refreshScheduler = createRefreshScheduler(refresh);
  const scheduleRefresh = refreshScheduler.schedule;

  async function completed(message: string) {
    feedback = message;
    await refresh();
  }

  function configure(account: ProviderAccount) {
    selectedAccount = account;
    detailOpen = false;
    configureOpen = true;
  }

  function inspectSource(item: ProviderDefinition) {
    selectedSource = item;
    selectedAccount = null;
    detailKind = "source";
    detailTab = "data";
    detailOpen = true;
  }

  function inspectAccount(account: ProviderAccount) {
    selectedSource = provider(account.providerId) ?? null;
    selectedAccount = account;
    detailKind = "account";
    detailTab = "data";
    detailOpen = true;
  }

  $effect(() => {
    if (!initialSource) {
      consumedSourceLink = "";
      return;
    }
    const link = `${initialSource}\0${initialSection}\0${initialConnect}`;
    if (!schema || consumedSourceLink === link) return;
    const item = schema.providers.find((provider) =>
      provider.id.toLowerCase() === initialSource.toLowerCase());
    if (!item) return;
    consumedSourceLink = link;
    if (initialConnect && mode === "accounts" && canConnectAccounts) {
      connectProviderId = item.id;
      connectOpen = true;
      return;
    }
    selectedSource = item;
    selectedAccount = null;
    detailKind = "source";
    detailTab = initialSection === "configuration" ? "configuration" : "data";
    detailOpen = true;
  });

  function sourcePurpose(item: ProviderDefinition) {
    if (item.id === "apple-musickit")
      return "Browse the public catalog and connect your personal library with a Media User Token and storefront. Optional downloads use the same account.";
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
      feedback = `${selectedSource.name} configuration saved.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Source configuration could not be saved.";
    } finally {
      action = "";
    }
  }

  function manageAccess(account: ProviderAccount) {
    selectedAccount = account;
    detailOpen = false;
    accessOpen = true;
  }

  async function toggle(account: ProviderAccount) {
    if (action) return;
    action = account.id;
    try {
      await sources.setEnabled(account, !account.enabled);
      await completed(`${account.sourceDisplayName || account.displayName} ${account.enabled ? "disabled" : "enabled"}.`);
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The connection could not be updated.";
    } finally {
      action = "";
    }
  }

  async function test(account: ProviderAccount, capability?: string) {
    const key = `${account.id}:${capability ?? "all"}`;
    if (action) return;
    action = key;
    try {
      const result = await sources.test(account, capability);
      testResults = { ...testResults, [key]: result };
      feedback = `${provider(account.providerId)?.name ?? account.providerId} ${capability ? humanize(capability) : "connection"} ${result.healthy ?? result.success ? "passed" : "needs attention"}.`;
      if (result.seekRung) feedback += ` Playback: ${humanize(result.seekRung)}${result.sampleBytes ? ` (${result.sampleBytes.toLocaleString()} bytes sampled)` : ""}.`;
      if (result.reasonCode) feedback += ` ${humanize(result.reasonCode)}.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The connection test failed.";
    } finally {
      action = "";
    }
  }

  async function measure(account: ProviderAccount) {
    const key = `${account.id}:cts`;
    if (action) return;
    action = key;
    try {
      const result = await sources.deepStream(account.providerId, account.id);
      testResults = { ...testResults, [key]: {
        ...result,
        latencyMs: result.clickToStreamMilliseconds ?? result.latencyMs,
      } };
      feedback = `${provider(account.providerId)?.name ?? account.providerId} click-to-stream measured${result.seekRung ? `: ${humanize(result.seekRung)}` : ""}.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The click-to-stream test failed.";
    } finally {
      action = "";
    }
  }

  async function measureSource(item: ProviderDefinition) {
    const key = `${item.id}:cts`;
    if (action) return;
    action = key;
    try {
      await sources.deepStream(item.id);
      feedback = `${item.name} click-to-stream measured.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The click-to-stream test failed.";
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
      feedback = cause instanceof Error ? cause.message : "The saved connection could not be removed.";
    } finally {
      action = "";
    }
  }

  onMount(() => {
    void refresh();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
    };
  });
</script>

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

  {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}
  <div class="sources-layout" aria-busy={refreshing}>
    {#if mode === "services"}
    <section class="sources-panel" aria-labelledby="services-title">
      <header class="sources-heading">
        <div>
          <h2 id="services-title">Services</h2>
          <p>Where Allstarr finds music, playlists, lyrics and scrobbling. Open one to see its checks and settings.</p>
        </div>
        <div class="sources-heading-actions">
          <Button variant="secondary" disabled={refreshing} onclick={() => void refresh()}><RefreshCw size={16} aria-hidden="true" />{refreshing ? "Refreshing…" : "Refresh"}</Button>
          {#if canConnectAccounts}<Button onclick={() => { connectProviderId = ""; connectOpen = true; }}><Plus size={16} aria-hidden="true" />Connect Source</Button>{/if}
        </div>
      </header>

      <ul class="source-list" aria-label="Services">
        {#each providers as item (item.id)}
          {@const state = sourceStatus(item, accounts, health)}
          {@const metrics = sourceMetrics(item, summary(item.id), providerHealth(item.id))}
          {@const connected = providerAccounts(item.id)}
          {@const cts = measurements.find((measurement) =>
            measurement.providerId.toLowerCase() === item.id.toLowerCase() &&
            (!measurement.providerAccountId || connected.some((account) => account.id === measurement.providerAccountId)))}
          {@const timing = sourceTimingLabel(item, summary(item.id))}
          {@const appleReadiness = item.runtimeCapabilities?.find((capability) =>
            capability.reasonCode?.startsWith("apple-download-"))?.reasonCode}
          <li class="source-row" data-state={state}>
            <button class="source-row-main" type="button" onclick={() => inspectSource(item)}>
              <ProviderArtwork id={item.id} definition={item} />
              <span class="source-row-copy">
                <strong>{item.name}</strong>
                <small>{sourcePurpose(item)}</small>
                {#if appleReadiness}<small class="source-row-warning" role="status">{providerReadinessLabel(appleReadiness)}</small>{/if}
              </span>
              <span class="source-row-capabilities">
                {#each (item.categories ?? []).slice(0, 4) as category}<span>{humanize(category)}</span>{/each}
              </span>
              <span class="source-row-status">
                <Badge state={state}>{state === "needs_config" ? "Needs setup" : state === "disabled" ? "Turned off" : humanize(state)}</Badge>
                <small>{metrics.checkedAt ? `${metrics.passing} of ${metrics.total || 0} checks passed · ${relativeTime(metrics.checkedAt)}` : "Not checked yet"}</small>
                <small class="source-row-timing">{connected.length ? `${connected.filter((account) => account.enabled).length} of ${connected.length} account${connected.length === 1 ? "" : "s"} on · ` : ""}{timing}{#if cts} · CTS {ctsMeasurementLabel(cts)}{/if}</small>
              </span>
              <ChevronRight class="source-row-chevron" size={18} aria-hidden="true" />
            </button>
          </li>
        {:else}
          <li class="compact-empty"><strong>No services yet</strong><p>Turn on a built-in service or install an extension to add one.</p></li>
        {/each}
      </ul>
    </section>
    {/if}

    {#if mode === "accounts"}
    <PlaylistConsent />
    <section class="sources-panel connections-panel" aria-labelledby="accounts-title">
      <header class="sources-heading connections-heading">
        <div>
          <h2 id="accounts-title">Accounts</h2>
          <p>Personal accounts belong to one person. Shared accounts work for everyone. Passwords and tokens are never shown again after you save them.</p>
        </div>
        <div class="sources-heading-actions">
          {#if canConnectAccounts}<Button onclick={() => { connectProviderId = ""; connectOpen = true; }}><Plus size={16} aria-hidden="true" />Connect Source</Button>{/if}
        </div>
      </header>
      <ul class="source-list account-list" aria-label={`${accounts.length} account${accounts.length === 1 ? "" : "s"}`}>
        {#each accounts as account (account.id)}
          {@const definition = provider(account.providerId)}
          {@const capabilities = health.filter((item) => item.providerAccountId === account.id)}
          {@const cts = measurements.find((item) => item.providerAccountId === account.id)}
          {@const ready = capabilities.filter((item) => item.ready).length}
          <li class="source-row" id={`connection-${account.id}`}>
            <button class="source-row-main" type="button" aria-label={`Open ${providerDisplayName(providers, account.providerId)} account`} onclick={() => inspectAccount(account)}>
              <ProviderArtwork id={account.providerId} definition={definition} />
              <span class="source-row-copy">
                <strong>{providerDisplayName(providers, account.providerId)}</strong>
                <small>{accountSubtitle(account)}{account.secret.configured && !account.secret.revoked ? "" : " · Setup needed"}</small>
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
            </button>
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
    </section>
    {/if}
  </div>

  <Dialog.Root bind:open={detailOpen}>
    <Dialog.Portal>
      <Dialog.Overlay class="dialog-overlay" />
      <Dialog.Content class="source-dialog source-detail-dialog">
        {#if selectedSource || selectedAccount}
          <header>
            <div class="source-identity">
              <ProviderArtwork
                id={selectedAccount?.providerId ?? selectedSource?.id ?? ""}
                definition={selectedSource ?? undefined}
              />
              <span>
                <Dialog.Title>{selectedAccount ? providerDisplayName(providers, selectedAccount.providerId) : selectedSource?.name}</Dialog.Title>
                <Dialog.Description>{detailKind === "account" && selectedAccount ? accountSubtitle(selectedAccount) : selectedSource?.description || "Source capability and readiness"}</Dialog.Description>
              </span>
            </div>
            <Dialog.Close class="icon-button" aria-label="Close Source details"><X size={18} aria-hidden="true" /></Dialog.Close>
          </header>

          <SegmentedNav
            items={detailKind === "account"
              ? [
                  { id: "data", label: "Data" },
                  { id: "configuration", label: "Configuration" },
                  { id: "access", label: "Access" },
                ]
              : [
                  { id: "data", label: "Data" },
                  { id: "configuration", label: "Configuration" },
                ]}
            active={detailTab}
            label="Source detail sections"
            class="route-tabs contextual-tabs"
            onchange={(value) => detailTab = value}
          />

          <div class="source-detail-scroll">
            {#if detailTab === "data" && detailKind === "source" && selectedSource}
              {@const state = sourceStatus(selectedSource, accounts, health)}
              {@const metrics = sourceMetrics(selectedSource, summary(selectedSource.id), providerHealth(selectedSource.id))}
              {@const cts = measurements.find((item) =>
                item.providerId.toLowerCase() === selectedSource!.id.toLowerCase() && !item.providerAccountId)}
              <dl class="source-detail-data">
                <div><dt>Status</dt><dd><Badge state={state}>{state === "needs_config" ? "Needs setup" : humanize(state)}</Badge></dd></div>
                <div><dt>Source ID</dt><dd>{selectedSource.id}</dd></div>
                <div><dt>Implementations</dt><dd>{implementationLabel(selectedSource)}</dd></div>
                <div><dt>Capabilities</dt><dd>{(selectedSource.categories ?? []).map(humanize).join(", ") || "Pending"}</dd></div>
                <div><dt>Readiness</dt><dd>{metrics.passing}/{metrics.total || 0} passing · {metrics.failed} failing</dd></div>
                <div><dt>Last check</dt><dd>{relativeTime(metrics.checkedAt, "Not checked yet")}</dd></div>
                <div><dt>API timing</dt><dd>{sourceTimingLabel(selectedSource, summary(selectedSource.id))}</dd></div>
                <div><dt>Click to stream</dt><dd>{#if cts}<Badge state={cts.health === "healthy" ? "healthy" : "degraded"}>{ctsMeasurementLabel(cts)}</Badge> · {relativeTime(cts.testedAt)}{:else if selectedSource.categories?.some((item) => ["streaming", "download"].includes(item.toLowerCase()))}Awaiting first sample{:else}Not applicable{/if}</dd></div>
              </dl>
              <div class="source-detail-capabilities">
                {#each selectedSource.capabilityRoutes ?? [] as route (route.routeId)}
                  <span><strong>{route.name || selectedSource.name}</strong><small>{humanize(route.origin || "built_in")} · {route.capabilities.map(humanize).join(", ") || "No published capabilities"}</small></span>
                {/each}
                {#each selectedSource.runtimeCapabilities ?? [] as capability}
                  <span><strong>{humanize(capability.id)}</strong><Badge state={readinessClass(capability.ready, capability.health)}>{capability.ready ? "Ready" : providerReadinessLabel(capability.reasonCode || capability.configuration || capability.health || "Unavailable")}</Badge></span>
                {:else}<p>No runtime capability probes are published for this Source.</p>{/each}
              </div>
            {:else if detailTab === "data" && selectedAccount}
              {@const capabilities = health.filter((item) => item.providerAccountId === selectedAccount?.id)}
              {@const cts = measurements.find((item) => item.providerAccountId === selectedAccount?.id)}
              <dl class="source-detail-data">
                <div><dt>Provider</dt><dd>{providerDisplayName(providers, selectedAccount.providerId)}</dd></div>
                <div><dt>Owner</dt><dd>{selectedAccount.creatorDisplayName || selectedAccount.ownerDisplayName || "Unknown"}</dd></div>
                <div><dt>Audience</dt><dd>{audienceLabel(selectedAccount)}</dd></div>
                <div><dt>State</dt><dd><Badge state={selectedAccount.enabled ? "healthy" : "suggested"}>{selectedAccount.enabled ? "Enabled" : "Disabled"}</Badge></dd></div>
                <div><dt>Account details</dt><dd><Badge state={selectedAccount.secret.configured && !selectedAccount.secret.revoked ? "healthy" : "needs_config"}>{selectedAccount.secret.configured && !selectedAccount.secret.revoked ? "Stored" : "Setup needed"}</Badge></dd></div>
                <div><dt>Health</dt><dd><Badge state={readinessClass(capabilities.length > 0 && capabilities.every((item) => item.ready), capabilities.some((item) => item.health === "degraded") ? "degraded" : null)}>{capabilities.filter((item) => item.ready).length}/{capabilities.length} ready</Badge></dd></div>
                {#if capabilities.some((item) => item.reasonCode === "apple-web-token-unavailable")}
                  <div><dt>Apple connection</dt><dd role="status">Apple's web-player token could not be fetched. Retry this account's Test action later.</dd></div>
                {/if}
                <div><dt>Click to stream</dt><dd>{#if cts}<Badge state={cts.health === "healthy" ? "healthy" : "degraded"}>{ctsMeasurementLabel(cts)}</Badge> · {relativeTime(cts.testedAt)}{:else if supportsPlaybackDiagnostic(capabilities)}Awaiting first sample{:else}Not applicable{/if}</dd></div>
              </dl>
            {:else if detailTab === "configuration" && detailKind === "source" && selectedSource}
              {#if selectedSource.id === "audiomuse-ai"}
                <p class="source-configuration-copy">AudioMuse-AI powers sound-based discovery, so its connection is configured beside the feature that uses it. Services still shows health and diagnostics.</p>
                <div class="source-detail-actions">
                  <Button href="#/intelligence?section=automation">Configure in Intelligence</Button>
                </div>
              {:else}
                {@const settings = accountSettings(selectedSource)}
                {@const sourceAccounts = providerAccounts(selectedSource.id)}
                <p class="source-configuration-copy">{sourcePurpose(selectedSource)}</p>
                <div class="source-detail-actions">
                  {#if administrator && !sourceNeedsAccount(selectedSource) && selectedSource.categories?.some((item) => ["streaming", "download"].includes(item.toLowerCase()))}
                    <Button variant="secondary" disabled={Boolean(action)} onclick={() => void measureSource(selectedSource!)}>Measure CTS</Button>
                  {/if}
                  {#if !settings.length && selectedSource.connectionKind !== "operator_managed"}
                    <p>No account configuration is required for this extension capability.</p>
                  {/if}
                </div>
                {#if settings.length}
                  <div class="source-account-configurations">
                    {#each sourceAccounts as account}
                      <section class="source-account-configuration">
                        <header>
                          <span><strong>{providerDisplayName(providers, account.providerId)}</strong><small>{accountSubtitle(account)}</small></span>
                          <Badge state={account.enabled ? "healthy" : "suggested"}>{account.enabled ? "Enabled" : "Disabled"}</Badge>
                        </header>
                        <dl class="source-detail-data">
                          {#each settings as field}
                            <div>
                              <dt>{field.label}</dt>
                              <dd>{accountSettingValue(account, field)}</dd>
                              {#if field.helpText}<small>{field.helpText}</small>{/if}
                            </div>
                          {/each}
                        </dl>
                        {#if account.canManage}<footer><Button onclick={() => configure(account)}>Edit configuration</Button></footer>{/if}
                      </section>
                    {:else}
                      <p class="credential-safety">These settings are saved on an encrypted Source account. Connect one to configure them.</p>
                    {/each}
                  </div>
                  <div class="source-detail-actions">
                    {#if canConnectAccounts}<Button onclick={() => { connectProviderId = selectedSource!.id; detailOpen = false; connectOpen = true; }}>{sourceAccounts.length ? "Connect another account" : "Connect account"}</Button>{/if}
                  </div>
                {/if}
                {#if selectedSource.id === "apple-musickit"}
                  <h3>Optional downloads</h3>
                  <p class="source-configuration-copy">GAMDL downloads need a configured gateway whose wrapper is signed in to the same Apple account. Catalog and playlists work without it.</p>
                  {#if administrator}
                    <div class="source-detail-actions"><Button onclick={() => { detailOpen = false; appleDownloadOpen = true; }}>Manage optional downloads</Button></div>
                  {/if}
                {/if}
                {#if (selectedSource.connectionKind === "operator_managed" || selectedSource.id === "apple-musickit") && selectedSource.configSchema?.length}
                  {#if administrator}
                    <form class="settings-fields source-configuration-form" onsubmit={(event) => void saveSourceConfiguration(event)}>
                      {#each selectedSource.configSchema as field}
                        <label class="setting-field" class:read-only={field.readOnly || field.ownership === "deployment"}>
                          <span><strong>{field.label}</strong>{#if field.ownership === "deployment"}<small>Deployment-owned</small>{/if}</span>
                          {#if field.readOnly || field.ownership === "deployment"}
                            <output>{field.sensitive ? "Stored" : String(fieldValue(config, field))}</output>
                          {:else if field.type === "select"}
                            <SelectField name={field.key} label={field.label} value={field.sensitive ? "" : String(fieldValue(config, field))} options={field.options ?? []} />
                          {:else if field.type === "toggle"}
                            <Checkbox name={field.key} checked={Boolean(fieldValue(config, field))} />
                          {:else}
                            <input
                              name={field.key}
                              type={field.sensitive ? "password" : field.type === "number" ? "number" : field.type === "url" ? "url" : "text"}
                              value={field.sensitive ? "" : String(fieldValue(config, field))}
                              min={field.min ?? undefined}
                              max={field.max ?? undefined}
                              required={field.required}
                              autocomplete="off"
                            />
                          {/if}
                          {#if field.helpText}<small>{field.helpText}</small>{/if}
                        </label>
                      {/each}
                      <footer><Button type="submit" disabled={Boolean(action)}>{action === `configure:${selectedSource.id}` ? "Saving…" : "Save configuration"}</Button></footer>
                    </form>
                  {:else}
                    <p class="credential-safety">Only an administrator can change deployment-managed Source settings.</p>
                  {/if}
                {/if}
              {/if}
            {:else if detailTab === "configuration" && selectedAccount}
              {@const capabilities = health.filter((item) => item.providerAccountId === selectedAccount?.id)}
              <div class="source-detail-actions">
                {#if selectedAccount.canManage}<Button variant="secondary" disabled={Boolean(action)} onclick={() => void toggle(selectedAccount!)}>{selectedAccount.enabled ? "Disable" : "Enable"} account</Button>
                {#if administrator}<Button variant="secondary" disabled={!selectedAccount.enabled || Boolean(action)} onclick={() => void test(selectedAccount!)}>Test connection</Button>{/if}{/if}
                {#if administrator && selectedAccount.canManage && selectedSource?.categories?.some((item) => ["streaming", "download"].includes(item.toLowerCase()))}
                  <Button variant="secondary" disabled={!selectedAccount.enabled || Boolean(action)} onclick={() => void measure(selectedAccount!)}>Measure CTS</Button>
                {/if}
                {#if selectedAccount.canManage}<Button onclick={() => configure(selectedAccount!)}>Edit configuration</Button>{/if}
              </div>
              <div class="source-detail-capabilities">
                {#each capabilities as capability}
                  {@const result = testResults[`${selectedAccount.id}:${capability.capability}`]}
                  <span>
                    <strong>{humanize(capability.capability)}</strong>
                    <Badge state={readinessClass(capability.ready, capability.health)}>{capability.ready ? "Ready" : providerReadinessLabel(capability.reasonCode || capability.configuration)}</Badge>
                    {#if result?.bars != null}<ConnectivityBars bars={result.healthy ?? result.success ? result.bars : 0} latency={result.latencyMs} />{/if}
                    {#if administrator && capability.canTest && selectedAccount.canManage}<Button variant="secondary" disabled={!selectedAccount.enabled || Boolean(action)} onclick={() => void test(selectedAccount!, capability.capability)}>Test</Button>{/if}
                  </span>
                {/each}
              </div>
            {:else if detailTab === "access" && selectedAccount}
              <dl class="source-detail-data">
                <div><dt>Audience</dt><dd>{audienceLabel(selectedAccount)}</dd></div>
                <div><dt>Owner</dt><dd>{selectedAccount.ownerDisplayName || "Current user"}</dd></div>
                <div><dt>Scope</dt><dd>{selectedAccount.scope}</dd></div>
              </dl>
              {#if selectedAccount.canChangeAudience}<Button onclick={() => manageAccess(selectedAccount!)}>Edit access</Button>{/if}
            {/if}
          </div>
        {/if}
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>

  <ConnectSourceDialog bind:open={connectOpen} {providers} administrator={canManageAllAccounts} users={audienceUsers} testConnection={administrator} initialProviderId={connectProviderId} onSaved={completed} />
  <ConnectSourceDialog bind:open={configureOpen} {providers} administrator={canManageAllAccounts} users={audienceUsers} testConnection={administrator} account={selectedAccount} onSaved={completed} />
  <AppleDownloadDialog bind:open={appleDownloadOpen} />
  <AccountAccessDialog bind:open={accessOpen} account={selectedAccount} users={audienceUsers} onSaved={completed} />

  <ConfirmDialog
    bind:open={removeOpen}
    title="Remove this Source connection?"
    description="The encrypted credential is revoked and this account can no longer route provider requests. Audit history remains."
    confirmLabel="Remove connection"
    onConfirm={remove}
  />
{/if}
