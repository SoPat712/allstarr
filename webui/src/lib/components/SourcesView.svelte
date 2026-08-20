<script lang="ts">
  import { onMount } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { MoreHorizontal, X } from "@lucide/svelte";
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
  import RouteError from "$lib/components/RouteError.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { fieldValue } from "$lib/settings";
  import { relativeTime } from "$lib/activity";
  import {
    accountSettings,
    audienceLabel,
    ctsMeasurementLabel,
    humanize,
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
  }: {
    mode?: "services" | "accounts";
    administrator: boolean;
    initialSource?: string;
    initialSection?: string;
  } = $props();

  let schema = $state<UiSchema | null>(null);
  let accounts = $state<ProviderAccount[]>([]);
  let audienceUsers = $state<{ id: string; displayName: string }[]>([]);
  let health = $state<ProviderHealth[]>([]);
  let summaries = $state<ProviderSummary[]>([]);
  let measurements = $state<CtsMeasurement[]>([]);
  let config = $state<Record<string, unknown>>({});
  let managementMode = $state("");
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
  const canManage = $derived(
    managementMode !== "AdminManaged" || administrator,
  );

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
        managementMode: string;
        audienceUsers?: { id: string; displayName: string }[];
        accounts: ProviderAccount[];
      };
      accounts = response.accounts;
      audienceUsers = response.audienceUsers ?? [];
      managementMode = response.managementMode;
    } else if (!administrator) {
      managementMode = schema?.providerAccountManagementMode ?? "AdminManaged";
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
    if (!initialSource || !schema) return;
    const item = schema.providers.find((provider) =>
      provider.id.toLowerCase() === initialSource.toLowerCase());
    if (!item) return;
    selectedSource = item;
    selectedAccount = null;
    detailKind = "source";
    detailTab = initialSection === "configuration" ? "configuration" : "data";
    detailOpen = true;
  });

  function sourcePurpose(item: ProviderDefinition) {
    if (item.id === "apple-download")
      return "GAMDL downloads, streaming, and cached synced-lyrics artifacts.";
    if (item.id === "apple-musickit")
      return "Optional personal-library and playlist access requiring an Apple Developer Program token and a Music User Token.";
    if (item.id === "spotiflac-apple-music")
      return "Apple Music extension metadata and Media User Token lyrics, including configured translation or pronunciation.";
    return item.description || "Configure this Source and its accounts here.";
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
      feedback = `${provider(account.providerId)?.name ?? account.providerId} click-to-stream measured.`;
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
      <Button variant="secondary" size="sm" onclick={() => void refresh()}>Retry</Button>
    </div>
  {/if}

  <div class="sources-layout" aria-busy={refreshing}>
    {#if mode === "services"}
    <section class="panel sources-panel">
      <header class="sources-heading">
        <div><p class="eyebrow">Provider-neutral services</p><h2>Services</h2><p>Capabilities describe what a Service can do. Latency appears after a health or click-to-stream check reports timing.</p></div>
        <div class="sources-heading-actions">
          <Button variant="secondary" onclick={() => void refresh()}>Refresh</Button>
          {#if canManage}<Button onclick={() => { connectProviderId = ""; connectOpen = true; }}>Connect Source</Button>{/if}
        </div>
      </header>
      {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

      <div class="operational-table-scroll">
        <table class="operational-table sources-table">
          <thead>
            <tr>
              <th>Source</th>
              <th>Capabilities</th>
              <th>Readiness</th>
              <th>Enabled</th>
              <th>Latency</th>
            </tr>
          </thead>
          <tbody>
            {#each providers as item (item.id)}
              {@const state = sourceStatus(item, accounts, health)}
              {@const metrics = sourceMetrics(item, summary(item.id), providerHealth(item.id))}
              {@const connected = providerAccounts(item.id)}
              {@const cts = measurements.find((measurement) =>
                measurement.providerId.toLowerCase() === item.id.toLowerCase() &&
                (!measurement.providerAccountId || connected.some((account) => account.id === measurement.providerAccountId)))}
              {@const timing = sourceTimingLabel(item, summary(item.id))}
              <tr data-state={state}>
                <td>
                  <button class="operational-row-identity" type="button" onclick={() => inspectSource(item)}>
                    <ProviderArtwork id={item.id} definition={item} />
                    <span><strong>{item.name}</strong><small>{implementationLabel(item)} · {sourcePurpose(item)}</small></span>
                  </button>
                  <Badge class="operational-mobile-state" state={state}>{state === "needs_config" ? "Needs setup" : humanize(state)}</Badge>
                  <details class="operational-mobile-detail">
                    <summary class="disclosure-summary compact"><DisclosureLabel title="More details" description="Capabilities and timing" /></summary>
                    <dl>
                      <div><dt>Capabilities</dt><dd>{(item.categories ?? []).map(humanize).join(", ") || "Pending"}</dd></div>
                      <div><dt>Latency</dt><dd>{timing}{#if cts}{#if timing} · {/if}<Badge state={cts.health === "healthy" ? "healthy" : "degraded"}>CTS {ctsMeasurementLabel(cts)}</Badge>{/if}</dd></div>
                    </dl>
                  </details>
                </td>
                <td>{(item.categories ?? []).map(humanize).join(", ") || "Pending"}</td>
                <td><Badge state={state}>{state === "needs_config" ? "Needs setup" : humanize(state)}</Badge><small>{metrics.passing}/{metrics.total || 0} passing · {relativeTime(metrics.checkedAt)}</small></td>
                <td><Badge state={state === "disabled" ? "suggested" : "healthy"}>{state === "disabled" ? "Disabled" : "Enabled"}</Badge>{connected.length ? ` · ${connected.filter((account) => account.enabled).length} account${connected.length === 1 ? "" : "s"}` : ""}</td>
                <td>{timing}{#if cts}{#if timing} · {/if}<Badge state={cts.health === "healthy" ? "healthy" : "degraded"}>CTS {ctsMeasurementLabel(cts)}</Badge>{/if}</td>
              </tr>
            {:else}
              <tr><td colspan="5"><div class="compact-empty"><strong>No Sources are registered</strong><p>Enable a built-in provider or install an extension.</p></div></td></tr>
            {/each}
          </tbody>
        </table>
      </div>
    </section>
    {/if}

    {#if mode === "accounts"}
    <section class="panel connections-panel">
      <header class="connections-heading">
        <div><p class="eyebrow">Encrypted account access</p><h2>Accounts</h2><p>{managementMode || schema.providerAccountManagementMode || "Managed"} · credentials are never returned to the browser.</p></div>
        <span>{accounts.length}</span>
      </header>
      <div class="operational-table-scroll">
        <table class="operational-table accounts-table">
          <thead><tr><th>Account</th><th>Provider</th><th>Owner</th><th>Audience</th><th>State</th><th>Health</th><th><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {#each accounts as account (account.id)}
              {@const definition = provider(account.providerId)}
              {@const capabilities = health.filter((item) => item.providerAccountId === account.id)}
              {@const cts = measurements.find((item) => item.providerAccountId === account.id)}
              <tr id={`connection-${account.id}`}>
                <td>
                  <button class="operational-row-identity" type="button" onclick={() => inspectAccount(account)}>
                    <ProviderArtwork id={account.providerId} definition={definition} />
                    <span><strong>{account.sourceDisplayName || account.displayName}</strong><small>{account.secret.configured && !account.secret.revoked ? "Account details stored" : "Setup needed"}</small></span>
                  </button>
                  <Badge class="operational-mobile-state" state={account.enabled ? "healthy" : "disabled"}>{account.enabled ? "Enabled" : "Disabled"}</Badge>
                  <details class="operational-mobile-detail">
                    <summary class="disclosure-summary compact"><DisclosureLabel title="More details" description="Owner, audience, and health" /></summary>
                    <dl>
                      <div><dt>Owner</dt><dd>{account.creatorDisplayName || account.ownerDisplayName || "Unknown"}</dd></div>
                      <div><dt>Audience</dt><dd>{audienceLabel(account)}</dd></div>
                      <div><dt>Health</dt><dd><Badge state={readinessClass(capabilities.length > 0 && capabilities.every((item) => item.ready), capabilities.some((item) => item.health === "degraded") ? "degraded" : null)}>{capabilities.filter((item) => item.ready).length}/{capabilities.length} ready</Badge>{#if cts} · <Badge state={cts.health === "healthy" ? "healthy" : "degraded"}>CTS {ctsMeasurementLabel(cts)}</Badge>{/if}</dd></div>
                    </dl>
                  </details>
                </td>
                <td>{definition?.name ?? account.providerId}</td>
                <td>{account.creatorDisplayName || account.ownerDisplayName || "Unknown"}</td>
                <td>{audienceLabel(account)}</td>
                <td><Badge state={account.enabled ? "healthy" : "disabled"}>{account.enabled ? "Enabled" : "Disabled"}</Badge></td>
                <td><Badge state={readinessClass(capabilities.length > 0 && capabilities.every((item) => item.ready), capabilities.some((item) => item.health === "degraded") ? "degraded" : null)}>{capabilities.filter((item) => item.ready).length}/{capabilities.length} ready</Badge>{#if cts} · <Badge state={cts.health === "healthy" ? "healthy" : "degraded"}>CTS {ctsMeasurementLabel(cts)}</Badge>{/if}</td>
                <td>
                  <DropdownMenu.Root>
                    <DropdownMenu.Trigger class="icon-button" aria-label={`Actions for ${account.displayName}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                    <DropdownMenu.Portal>
                      <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                        <DropdownMenu.Item class="bits-menu-item" onSelect={() => void toggle(account)}>{account.enabled ? "Disable" : "Enable"}</DropdownMenu.Item>
                        <DropdownMenu.Separator />
                        <DropdownMenu.Item class="bits-menu-item danger-item" onSelect={() => { removal = account; removeOpen = true; }}>Remove</DropdownMenu.Item>
                      </DropdownMenu.Content>
                    </DropdownMenu.Portal>
                  </DropdownMenu.Root>
                </td>
              </tr>
            {:else}
              <tr><td colspan="7"><div class="compact-empty connections-empty">
                <strong>{canManage ? "No Source accounts yet" : "Accounts are administrator-managed"}</strong>
                <p>{canManage ? "Connect an account to activate personal or shared Source capabilities." : "Available shared Sources appear without exposing credentials."}</p>
                {#if canManage}<Button onclick={() => { connectProviderId = ""; connectOpen = true; }}>Connect Source</Button>{/if}
              </div></td></tr>
            {/each}
          </tbody>
        </table>
      </div>
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
                <Dialog.Title>{selectedAccount?.sourceDisplayName || selectedAccount?.displayName || selectedSource?.name}</Dialog.Title>
                <Dialog.Description>{detailKind === "account" ? `${selectedSource?.name ?? selectedAccount?.providerId} account` : selectedSource?.description || "Source capability and readiness"}</Dialog.Description>
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
                <div><dt>Last check</dt><dd>{relativeTime(metrics.checkedAt)}</dd></div>
                <div><dt>API timing</dt><dd>{sourceTimingLabel(selectedSource, summary(selectedSource.id))}</dd></div>
                <div><dt>Click to stream</dt><dd>{#if cts}<Badge state={cts.health === "healthy" ? "healthy" : "degraded"}>{ctsMeasurementLabel(cts)}</Badge> · {relativeTime(cts.testedAt)}{:else if selectedSource.categories?.some((item) => ["streaming", "download"].includes(item.toLowerCase()))}Awaiting first sample{:else}Not applicable{/if}</dd></div>
              </dl>
              <div class="source-detail-capabilities">
                {#each selectedSource.capabilityRoutes ?? [] as route (route.routeId)}
                  <span><strong>{route.name || selectedSource.name}</strong><small>{humanize(route.origin || "built_in")} · {route.capabilities.map(humanize).join(", ") || "No published capabilities"}</small></span>
                {/each}
                {#each selectedSource.runtimeCapabilities ?? [] as capability}
                  <span><strong>{humanize(capability.id)}</strong><Badge state={readinessClass(capability.ready, capability.health)}>{capability.ready ? "Ready" : humanize(capability.reasonCode || capability.configuration || capability.health || "Unavailable")}</Badge></span>
                {:else}<p>No runtime capability probes are published for this Source.</p>{/each}
              </div>
            {:else if detailTab === "data" && selectedAccount}
              {@const capabilities = health.filter((item) => item.providerAccountId === selectedAccount?.id)}
              {@const cts = measurements.find((item) => item.providerAccountId === selectedAccount?.id)}
              <dl class="source-detail-data">
                <div><dt>Provider</dt><dd>{selectedSource?.name ?? selectedAccount.providerId}</dd></div>
                <div><dt>Owner</dt><dd>{selectedAccount.creatorDisplayName || selectedAccount.ownerDisplayName || "Unknown"}</dd></div>
                <div><dt>Audience</dt><dd>{audienceLabel(selectedAccount)}</dd></div>
                <div><dt>State</dt><dd><Badge state={selectedAccount.enabled ? "healthy" : "suggested"}>{selectedAccount.enabled ? "Enabled" : "Disabled"}</Badge></dd></div>
                <div><dt>Account details</dt><dd><Badge state={selectedAccount.secret.configured && !selectedAccount.secret.revoked ? "healthy" : "needs_config"}>{selectedAccount.secret.configured && !selectedAccount.secret.revoked ? "Stored" : "Setup needed"}</Badge></dd></div>
                <div><dt>Health</dt><dd><Badge state={readinessClass(capabilities.length > 0 && capabilities.every((item) => item.ready), capabilities.some((item) => item.health === "degraded") ? "degraded" : null)}>{capabilities.filter((item) => item.ready).length}/{capabilities.length} ready</Badge></dd></div>
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
                  {#if selectedSource.id === "apple-download"}
                    <Button onclick={() => { detailOpen = false; appleDownloadOpen = true; }}>Manage Apple Music – GAMDL</Button>
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
                          <span><strong>{account.sourceDisplayName || account.displayName}</strong><small>Encrypted account configuration</small></span>
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
                        <footer><Button onclick={() => configure(account)}>Edit configuration</Button></footer>
                      </section>
                    {:else}
                      <p class="credential-safety">These settings are saved on an encrypted Source account. Connect one to configure them.</p>
                    {/each}
                  </div>
                  <div class="source-detail-actions">
                    <Button disabled={!canManage} onclick={() => { connectProviderId = selectedSource!.id; detailOpen = false; connectOpen = true; }}>{sourceAccounts.length ? "Connect another account" : "Connect account"}</Button>
                  </div>
                {/if}
                {#if selectedSource.connectionKind === "operator_managed" && selectedSource.configSchema?.length}
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
                <Button variant="secondary" disabled={Boolean(action)} onclick={() => void toggle(selectedAccount!)}>{selectedAccount.enabled ? "Disable" : "Enable"} account</Button>
                <Button variant="secondary" disabled={!selectedAccount.enabled || Boolean(action)} onclick={() => void test(selectedAccount!)}>Test connection</Button>
                {#if administrator && supportsPlaybackDiagnostic(capabilities)}
                  <Button variant="secondary" disabled={!selectedAccount.enabled || Boolean(action)} onclick={() => void measure(selectedAccount!)}>Measure CTS</Button>
                {/if}
                <Button onclick={() => configure(selectedAccount!)}>Edit configuration</Button>
              </div>
              <div class="source-detail-capabilities">
                {#each capabilities as capability}
                  {@const result = testResults[`${selectedAccount.id}:${capability.capability}`]}
                  <span>
                    <strong>{humanize(capability.capability)}</strong>
                    <Badge state={readinessClass(capability.ready, capability.health)}>{capability.ready ? "Ready" : humanize(capability.reasonCode || capability.configuration)}</Badge>
                    {#if result?.bars != null}<ConnectivityBars bars={result.healthy ?? result.success ? result.bars : 0} latency={result.latencyMs} />{/if}
                    {#if capability.canTest}<Button variant="secondary" disabled={!selectedAccount.enabled || Boolean(action)} onclick={() => void test(selectedAccount!, capability.capability)}>Test</Button>{/if}
                  </span>
                {/each}
              </div>
            {:else if detailTab === "access" && selectedAccount}
              <dl class="source-detail-data">
                <div><dt>Audience</dt><dd>{audienceLabel(selectedAccount)}</dd></div>
                <div><dt>Owner</dt><dd>{selectedAccount.ownerDisplayName || "Current user"}</dd></div>
                <div><dt>Scope</dt><dd>{selectedAccount.scope}</dd></div>
              </dl>
              {#if administrator}<Button onclick={() => manageAccess(selectedAccount!)}>Edit access</Button>{/if}
            {/if}
          </div>
        {/if}
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>

  <ConnectSourceDialog bind:open={connectOpen} {providers} {administrator} initialProviderId={connectProviderId} onSaved={completed} />
  <ConnectSourceDialog bind:open={configureOpen} {providers} {administrator} account={selectedAccount} onSaved={completed} />
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
