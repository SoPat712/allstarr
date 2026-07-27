<script lang="ts">
  import { onMount } from "svelte";
  import { DropdownMenu } from "bits-ui";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    home,
    sources,
    type ConnectivityResult,
    type CtsMeasurement,
    type ProviderAccount,
    type ProviderDefinition,
    type ProviderHealth,
    type ProviderSummary,
    type UiSchema,
  } from "$lib/api";
  import AccountAccessDialog from "$lib/components/AccountAccessDialog.svelte";
  import ConnectSourceDialog from "$lib/components/ConnectSourceDialog.svelte";
  import ConnectivityBars from "$lib/components/ConnectivityBars.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import {
    accountSettings,
    audienceLabel,
    humanize,
    sourceMetrics,
    sourceStatus,
  } from "$lib/sources";
  import { liveUpdates } from "$lib/live-updates.svelte";

  let { administrator }: { administrator: boolean } = $props();

  let schema = $state<UiSchema | null>(null);
  let accounts = $state<ProviderAccount[]>([]);
  let audienceUsers = $state<{ id: string; displayName: string }[]>([]);
  let health = $state<ProviderHealth[]>([]);
  let summaries = $state<ProviderSummary[]>([]);
  let measurements = $state<CtsMeasurement[]>([]);
  let managementMode = $state("");
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let feedback = $state("");
  let action = $state("");
  let connectOpen = $state(false);
  let configureOpen = $state(false);
  let accessOpen = $state(false);
  let selectedAccount = $state<ProviderAccount | null>(null);
  let removal = $state<ProviderAccount | null>(null);
  let removeOpen = $state(false);
  let testResults = $state<Record<string, ConnectivityResult>>({});
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  const providers = $derived(
    [...(schema?.providers ?? [])].toSorted((left, right) => {
      const section = (item: ProviderDefinition) =>
        sourceStatus(item, accounts, health) === "disabled"
          ? 2
          : providerAccounts(item.id).some((account) => account.enabled) ? 0 : 1;
      const order = { degraded: 0, needs_config: 1, partial_config: 1, available: 2, configured: 3, healthy: 4, disabled: 5 };
      return section(left) - section(right) ||
        (order[sourceStatus(left, accounts, health) as keyof typeof order] ?? 2) -
        (order[sourceStatus(right, accounts, health) as keyof typeof order] ?? 2) ||
        left.name.localeCompare(right.name);
    }),
  );
  const disabledSourceCount = $derived(
    providers.filter((item) => sourceStatus(item, accounts, health) === "disabled").length,
  );
  const enabledSourceCount = $derived(providers.length - disabledSourceCount);
  const activeSourceCount = $derived(
    providers.filter((item) =>
      sourceStatus(item, accounts, health) !== "disabled" &&
      providerAccounts(item.id).some((account) => account.enabled)).length,
  );
  const availableSourceCount = $derived(enabledSourceCount - activeSourceCount);
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

  function relativeTime(value?: string | null) {
    if (!value) return "Not checked";
    const minutes = Math.round((new Date(value).getTime() - Date.now()) / 60_000);
    const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
    return formatter.format(Math.round(hours / 24), "day");
  }

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const requests: Promise<unknown>[] = [home.schema(), sources.accounts()];
    if (administrator) requests.push(home.providers(), sources.health(), sources.cts());
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
    }
    const failed = results.filter((result) => result.status === "rejected");
    if (failed.length)
      error = failed[0].reason instanceof Error ? failed[0].reason.message : "Source state is unavailable.";
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

  async function completed(message: string) {
    feedback = message;
    await refresh();
  }

  function configure(account: ProviderAccount) {
    selectedAccount = account;
    configureOpen = true;
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
      const result = await sources.deepStream(account);
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
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if loading}
  <section class="panel sources-panel skeleton-panel" aria-label="Loading Sources" aria-busy="true"></section>
{:else if !schema}
  <RouteError
    eyebrow="Sources unavailable"
    title="Allstarr could not load the Source catalog."
    message={error}
    onRetry={refresh}
  />
{:else}
  {#if error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span><p><strong>Source readiness may be stale.</strong> {error}</p>
      <button type="button" onclick={() => void refresh()}>Retry</button>
    </div>
  {/if}

  <div class="sources-layout" aria-busy={refreshing}>
    <section class="panel sources-panel">
      <header class="sources-heading">
        <div><p class="eyebrow">Provider-neutral routing</p><h2>Sources</h2><p>Capabilities describe what a Source can do. Connections grant secure account access without exposing credentials.</p></div>
        <div class="sources-heading-actions">
          <button class="button-secondary" type="button" onclick={() => void refresh()}>Refresh</button>
          {#if canManage}<button class="button-primary" type="button" onclick={() => connectOpen = true}>Connect Source</button>{/if}
        </div>
      </header>
      {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

      <div class="source-catalog">
        {#each providers as item, index (item.id)}
          {@const state = sourceStatus(item, accounts, health)}
          {@const metrics = sourceMetrics(item, summary(item.id), providerHealth(item.id))}
          {@const connected = providerAccounts(item.id)}
          {#if availableSourceCount && index === activeSourceCount}
            <header class="source-section-heading">
              <div><h3>Available Sources</h3><p>Built in or installed, but not connected to an account.</p></div>
              <span>{availableSourceCount}</span>
            </header>
          {:else if disabledSourceCount && index === enabledSourceCount}
            <header class="source-section-heading">
              <div><h3>Disabled Sources</h3><p>Installed but excluded from routing and health evaluation.</p></div>
              <span>{disabledSourceCount}</span>
            </header>
          {/if}
          <article class="source-card" data-state={state}>
            <header>
              <div class="source-identity">
                <ProviderArtwork id={item.id} definition={item} />
                <span><strong>{item.name}</strong><small>{item.description || item.notes?.join(" · ") || "Provider capability Source"}</small></span>
              </div>
              <div class="source-card-actions">
                {#if connected.length}
                  <button type="button" onclick={() => document.getElementById(`connection-${connected[0].id}`)?.scrollIntoView({ behavior: "smooth" })}>Manage</button>
                {:else if canManage && accountSettings(item).length}
                  <button type="button" onclick={() => connectOpen = true}>Connect</button>
                {/if}
                <span class={`status-pill ${state}`}>{state === "needs_config" ? "Needs setup" : humanize(state)}</span>
              </div>
            </header>
            <div class="source-capabilities" aria-label={`${item.name} capabilities`}>
              {#each item.categories ?? [] as capability}
                <span class:ready={(item.runtimeCapabilities ?? []).find((entry) => entry.id === capability)?.ready}>
                  {humanize(capability)}
                </span>
              {:else}<span>Capability details pending</span>{/each}
            </div>
            <dl class="source-metrics">
              <div><dt>Connections</dt><dd>{connected.filter((account) => account.enabled).length}</dd></div>
              <div><dt>Passing</dt><dd>{metrics.total ? `${metrics.passing}/${metrics.total}` : "—"}</dd></div>
              <div><dt>Failures</dt><dd class:danger-text={metrics.failed > 0}>{metrics.failed || "—"}</dd></div>
              <div><dt>Last check</dt><dd>{relativeTime(metrics.checkedAt)}</dd></div>
            </dl>
            <details class="source-details">
              <summary>Capability and implementation details</summary>
              <div>
                {#each item.runtimeCapabilities ?? [] as capability}
                  <span><strong>{humanize(capability.id)}</strong><small>{capability.ready ? "Ready" : humanize(capability.configuration || capability.health || "Unavailable")}{capability.reasonCode ? ` · ${humanize(capability.reasonCode)}` : ""}</small></span>
                {:else}<p>No runtime capability probes are published for this Source.</p>{/each}
              </div>
              <dl>
                <div><dt>Source ID</dt><dd>{item.id}</dd></div>
                <div><dt>Implementation</dt><dd>{humanize(item.implementationOrigin || "built in")}</dd></div>
                {#if item.audience}<div><dt>Default audience</dt><dd>{humanize(item.audience)}</dd></div>{/if}
              </dl>
            </details>
          </article>
        {:else}
          <div class="compact-empty"><strong>No Sources are registered</strong><p>Enable a built-in provider or install an extension.</p></div>
        {/each}
      </div>
    </section>

    <section class="panel connections-panel">
      <header class="connections-heading">
        <div><p class="eyebrow">Encrypted account access</p><h2>Connections</h2><p>{managementMode || schema.providerAccountManagementMode || "Managed"} · credentials are never returned to the browser.</p></div>
        <span>{accounts.length}</span>
      </header>
      <div class="connection-list">
        {#each accounts as account (account.id)}
          {@const definition = provider(account.providerId)}
          {@const capabilities = health.filter((item) => item.providerAccountId === account.id)}
          {@const cts = measurements.find((item) => item.providerAccountId === account.id)}
          <article class="connection-card" id={`connection-${account.id}`}>
            <header>
              <div class="source-identity">
                <ProviderArtwork id={account.providerId} definition={definition} />
                <span>
                  <strong>{account.sourceDisplayName || account.displayName}</strong>
                  <small>{definition?.name ?? account.providerId} connection · Connected by {account.creatorDisplayName || account.ownerDisplayName || "unknown user"}</small>
                </span>
              </div>
              <DropdownMenu.Root>
                <DropdownMenu.Trigger class="icon-button" aria-label={`Actions for ${account.displayName}`}>•••</DropdownMenu.Trigger>
                <DropdownMenu.Portal>
                  <DropdownMenu.Content class="bits-menu" sideOffset={6} align="end">
                    <DropdownMenu.Item class="bits-menu-item" onSelect={() => void toggle(account)}>
                      {account.enabled ? "Disable" : "Enable"}
                    </DropdownMenu.Item>
                    <DropdownMenu.Separator />
                    <DropdownMenu.Item class="bits-menu-item danger-item" onSelect={() => { removal = account; removeOpen = true; }}>
                      Remove
                    </DropdownMenu.Item>
                  </DropdownMenu.Content>
                </DropdownMenu.Portal>
              </DropdownMenu.Root>
            </header>
            <div class="connection-state">
              <span class={`status-pill ${account.enabled ? "healthy" : "disabled"}`}>{account.enabled ? "Enabled" : "Disabled"}</span>
              <button class="audience-button" type="button" disabled={!administrator} aria-label={`Audience ${audienceLabel(account)}`} onclick={() => manageAccess(account)}>
                <span>Audience</span><strong>{audienceLabel(account)} <i aria-hidden="true">›</i></strong>
              </button>
              <span class={`credential-state ${account.secret.configured && !account.secret.revoked ? "ready" : "warning"}`}>
                {account.secret.configured && !account.secret.revoked ? "Account details stored" : "Setup needed"}
              </span>
            </div>
            {#if capabilities.length}
              <div class="connection-capabilities">
                {#each capabilities as capability}
                  {@const result = testResults[`${account.id}:${capability.capability}`]}
                  <div>
                    <span><strong>{humanize(capability.capability)}</strong><small>{capability.ready ? "Ready" : humanize(capability.reasonCode || capability.configuration)}</small></span>
                    {#if result?.bars != null}<ConnectivityBars bars={result.healthy ?? result.success ? result.bars : 0} latency={result.latencyMs} />{/if}
                    <span class={`status-pill ${capability.health}`}>{humanize(capability.health)}</span>
                    {#if capability.canTest}
                      <button type="button" disabled={!account.enabled || Boolean(action)} onclick={() => void test(account, capability.capability)}>
                        {action === `${account.id}:${capability.capability}` ? "Testing…" : "Test"}
                      </button>
                    {/if}
                  </div>
                {/each}
              </div>
            {:else}
              <p class="connection-empty">No automatic capability tests are published for this connection.</p>
            {/if}
            {#if cts || testResults[`${account.id}:cts`]}
              {@const measurement = testResults[`${account.id}:cts`] ?? cts}
              <div class="cts-summary">
                <span><strong>Click to stream</strong><small>{measurement.testedAt ? relativeTime(measurement.testedAt) : "Just measured"}</small></span>
                <ConnectivityBars bars={measurement.bars ?? 0} latency={measurement.latencyMs} label="Click to stream" />
              </div>
            {/if}
            <footer>
              <button class="button-secondary" type="button" disabled={!account.enabled || Boolean(action)} onclick={() => void test(account)}>
                {action === `${account.id}:all` ? "Testing…" : "Test connection"}
              </button>
              {#if administrator && (definition?.categories ?? []).includes("streaming")}
                <button class="button-secondary" type="button" disabled={!account.enabled || Boolean(action)} onclick={() => void measure(account)}>
                  {action === `${account.id}:cts` ? "Measuring…" : "Measure CTS"}
                </button>
              {/if}
              <button class="button-primary" type="button" onclick={() => configure(account)}>Configure</button>
            </footer>
          </article>
        {:else}
          <div class="compact-empty connections-empty">
            <strong>{canManage ? "No Source connections yet" : "Connections are administrator-managed"}</strong>
            <p>{canManage ? "Connect an account to activate personal or shared Source capabilities." : "Available shared Sources appear in the catalog without exposing their credentials."}</p>
            {#if canManage}<button class="button-primary" type="button" onclick={() => connectOpen = true}>Connect Source</button>{/if}
          </div>
        {/each}
      </div>
    </section>
  </div>

  <ConnectSourceDialog bind:open={connectOpen} {providers} {administrator} onSaved={completed} />
  <ConnectSourceDialog bind:open={configureOpen} {providers} {administrator} account={selectedAccount} onSaved={completed} />
  <AccountAccessDialog bind:open={accessOpen} account={selectedAccount} users={audienceUsers} onSaved={completed} />

  <ConfirmDialog
    bind:open={removeOpen}
    title="Remove this Source connection?"
    description="The encrypted credential is revoked and this account can no longer route provider requests. Audit history remains."
    confirmLabel="Remove connection"
    onConfirm={remove}
  />
{/if}
