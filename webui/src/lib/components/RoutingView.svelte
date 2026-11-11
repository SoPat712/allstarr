<script lang="ts">
  import { toast } from "$lib/toasts";
  let { embedded = false }: { embedded?: boolean } = $props();
  import { onMount } from "svelte";
  import { ArrowDown, ArrowUp, ChevronDown, GripVertical, Lock } from "@lucide/svelte";
  import { Button } from "$lib/components/ui/button";
  import { ApiError, home, settings, sources, type PriorityGroup, type ProviderScore, type UiSchema } from "$lib/api";
  import SettingsFields from "$lib/components/SettingsFields.svelte";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";

  import { capabilityScore, isRanked, scoreTier, sortByScore as orderByScore } from "$lib/provider-score";
  import RouteError from "$lib/components/RouteError.svelte";
  import { canonicalProviderId, humanize, lyricsSourceDisplayName, sourceOriginLabel } from "$lib/sources";
  import { createOrderSave } from "$lib/order-save";
  import { mergeRoutingOrders, move } from "$lib/settings";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";

  // One order at a time; five near-identical lists in a row were hard to tell apart.
  const tabNames: Record<string, string> = { metadata: "Search", download: "Downloads", streaming: "Streaming", playlist: "Playlists", lyrics: "Lyrics" };
  const tabKey = "allstarr.routing-tab";
  let tab = $state((() => { try { return localStorage.getItem(tabKey) ?? ""; } catch { return ""; } })());
  function chooseTab(id: string) { tab = id; try { localStorage.setItem(tabKey, id); } catch { /* Remembering the tab is optional. */ } }
  let scores = $state<ProviderScore[]>([]);
  let qualityTarget = $state<string | undefined>();
  const loadScores = () => void sources.scores().then((result) => { scores = result.providers ?? []; qualityTarget = result.qualityTarget; }).catch(() => {});
  let openScore = $state("");
  let expanded = $state(false);
  let scoreOpen = $state(false);
  const toggleScore = (key: string) => { scoreOpen = !(scoreOpen && openScore === key); openScore = scoreOpen ? key : ""; };
  const scoreFor = (providerId: string) => scores.find((item) => item.providerId.toLowerCase() === providerId.toLowerCase());

  // Audio quality only describes the sources that deliver audio; lyrics from the library or kept files have no score.
  const audioGroups = new Set(["streaming", "download"]);
  const localLyrics = new Set(["kept", "backend"]);

  // Manual sources sink to the bottom whatever their score.
  function sortByScore(group: PriorityGroup) {
    const order = orders[group.id] ?? group.providers;
    const fixed: string[] = order.filter((id) => localLyrics.has(id));
    const sorted = orderByScore(order.filter((id) => !fixed.includes(id)),
      (id) => capabilityScore(scoreFor(id), group.id), (id) => isRanked(provider(id)?.useMode));
    changeOrder(group, [...fixed, ...sorted]);
  }

  let schema = $state<UiSchema | null>(null);
  const groups = $derived(schema?.priorityGroups ?? []);
  const shown = $derived(groups.some((item) => item.id === tab) ? tab : groups[0]?.id);
  let config = $state<Record<string, unknown>>({});
  let orders = $state<Record<string, string[]>>({});
  let loading = $state(true);
  let refreshing = $state(false);
  let action = $state("");
  let error = $state("");
  let dragging = $state<{ groupId: string; index: number } | null>(null);
  let dirtyOrderIds = $state<string[]>([]);
  let moveAnnouncement = $state<{ groupId: string; message: string } | null>(null);
  const policyKeys = new Set([
    "AUDIO_QUALITY",
    "MATCHING_LOCAL_PREFERENCE_PERCENT",
  ]);
  const policyFields = $derived(
    (schema?.configSections ?? []).flatMap((section) => section.fields)
      .filter((field) => policyKeys.has(field.key)),
  );

  function provider(id: string) {
    return schema?.providers.find((item) => item.id.toLowerCase() === id.toLowerCase());
  }

  let refreshPending: Promise<unknown> | null = null;
  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const pending = Promise.allSettled([home.schema(), settings.config()]);
    refreshPending = pending;
    const results = await pending;
    if (results[0].status === "fulfilled") schema = results[0].value as UiSchema;
    const nextConfig = results[1].status === "fulfilled"
      ? results[1].value as Record<string, unknown>
      : null;
    if (nextConfig) config = nextConfig;
    if (schema && nextConfig) {
      // Older saved orders can list one service under two names; show it once.
      orders = Object.fromEntries(Object.entries(mergeRoutingOrders(nextConfig, schema.priorityGroups ?? [], orders, dirtyOrderIds))
        .map(([id, order]) => [id, [...new Set(order.map(canonicalProviderId))]]));
    }
    const failed = results.filter((result) => result.status === "rejected");
    if (failed.length)
      error = failed[0].reason instanceof Error ? failed[0].reason.message : "Routing state is unavailable.";
    loading = false;
    refreshing = false;
    refreshPending = null;
  }

  const refreshScheduler = createRefreshScheduler(refresh);
  const scheduleRefresh = refreshScheduler.schedule;

  const orderSave = createOrderSave({
    save: async (id, order) => {
      const group = groups.find(item => item.id === id);
      if (group) {
        await settings.save({ [group.envKey]: order.join(",") });
        await refreshPending;
        await refresh();
      }
    },
    saved: (id, order, previous) => {
      if (orders[id]?.join(",") === order.join(",")) dirtyOrderIds = dirtyOrderIds.filter(item => item !== id);
      toast.success("Order saved", { label: "Undo", run: () => {
        const group = groups.find(item => item.id === id);
        if (group) changeOrder(group, previous);
      } });
    },
    failed: async (id, cause) => {
      if (cause instanceof ApiError && cause.status === 409) {
        dirtyOrderIds = dirtyOrderIds.filter(item => item !== id);
        await refreshPending;
        await refresh();
      }
      toast.error(cause instanceof Error ? cause.message : "Provider routing could not be saved.");
    },
  });

  function changeOrder(group: PriorityGroup, order: string[]) {
    const previous = orders[group.id] ?? group.providers;
    if (previous.join(",") === order.join(",")) return;
    orders = { ...orders, [group.id]: order };
    if (!dirtyOrderIds.includes(group.id)) dirtyOrderIds = [...dirtyOrderIds, group.id];
    orderSave.change(group.id, order, previous);
  }

  function moveProvider(group: PriorityGroup, index: number, direction: -1 | 1) {
    const order = orders[group.id] ?? [];
    const providerId = order[index];
    changeOrder(group, move(order, index, direction));
    moveAnnouncement = { groupId: group.id, message: `${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + direction + 1}.` };
  }

  function dropProvider(group: PriorityGroup, index: number) {
    if (!dragging || dragging.groupId !== group.id || dragging.index === index) return;
    const order = [...(orders[group.id] ?? [])];
    const [providerId] = order.splice(dragging.index, 1);
    order.splice(index, 0, providerId);
    changeOrder(group, order);
    moveAnnouncement = { groupId: group.id, message: `${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + 1}.` };
    dragging = null;
  }

  onMount(() => {
    void refresh();
    loadScores();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
      orderSave.flush();
    };
  });
</script>

{#if loading}
  <RowsSkeleton />
{:else if !schema}
  <RouteError
    eyebrow="Routing unavailable"
    title="Allstarr could not load provider routing."
    message={error}
    onRetry={refresh}
  />
{:else}
  <div class="routing-view" aria-busy={refreshing}>
    {#if !embedded}
    <header class="sources-heading">
      <div>
        <h2>Routing</h2>
        <p>Allstarr always plays from your media server first when it has the song, then tries these services in the order below.</p>
      </div>
    </header>
    {/if}
    {#if error}
      <div class="degraded-banner" role="status">
        <span aria-hidden="true">!</span><p><strong>Routing may be out of date.</strong> {error}</p>
        <Button variant="secondary" size="sm" disabled={refreshing} onclick={() => void refresh()}>{refreshing ? "Trying again…" : "Retry"}</Button>
      </div>
    {/if}

    {#if !embedded}<PageStatus items={[{ label: `${groups.length} source orders` }, { label: "Changes save as you make them" }]} />{/if}
    {#if policyFields.length}
      <SettingsFields section={{ id: "quality-policy", label: "Audio quality", fields: policyFields }} {config} onChanged={async () => { await refresh(); loadScores(); }} />
    {/if}
    <div class="routing-groups">
      {#each groups.filter((item) => item.id === shown) as group}
        <section class="settings-card routing-group">
          <p class="sr-only" role="status">{moveAnnouncement?.groupId === group.id ? moveAnnouncement.message : ""}</p>
          <header><div><div class="settings-card-title"><h3>{tabNames[group.id] ?? group.label.replace(/ order$/i, "")} source order</h3><span class="scope-tag" data-scope="everyone">Everyone</span></div><small>Drag to reorder. Saves as you drop.</small></div>{#if scores.length}<Button variant="text" onclick={() => sortByScore(group)}>Sort by score</Button>{/if}</header>
          <div class="routing-tabs">
            {#if groups.length > 1}
              <FilterSegments label="Source orders" options={groups.map(item => ({ id: item.id, label: tabNames[item.id] ?? item.label, count: (orders[item.id] ?? item.providers).length }))}
                value={shown ?? ""} storageKey={tabKey} onchange={id => { chooseTab(id); expanded = false; }} />
            {/if}
          </div>
          <ol>
            {#if group.pinnedProvider}
              <li class="pinned">
                <span class="routing-index" aria-hidden="true"><Lock size={14} /></span>
                <ProviderArtwork id={group.pinnedProvider.id} label={group.pinnedProvider.name} />
                <span class="routing-copy"><strong>{group.pinnedProvider.name}</strong><small>{group.pinnedProvider.reason} · always tried first</small></span>
              </li>
            {/if}
            {#each (orders[group.id] ?? group.providers).slice(0, expanded ? undefined : 5) as providerId, index (providerId)}
              {@const definition = provider(providerId)}
              {@const label = group.id === "lyrics" ? lyricsSourceDisplayName(schema?.providers ?? [], providerId) : definition?.name ?? humanize(providerId)}
              {@const scoreKey = `${group.id}:${providerId}`}
              <!-- svelte-ignore a11y_no_noninteractive_element_interactions, a11y_click_events_have_key_events -->
              <li
                draggable={true}
                class:has-score={Boolean(scoreFor(providerId))}
                onclick={(event) => { if (scoreFor(providerId) && !(event.target as Element).closest("button, a, input, details, .score-details")) toggleScore(scoreKey); }}
                class:dragging={dragging?.groupId === group.id && dragging.index === index}
                ondragstart={event => { dragging = { groupId: group.id, index }; event.dataTransfer?.setData("text/plain", providerId); }}
                ondragover={(event) => event.preventDefault()}
                ondrop={event => { event.preventDefault(); dropProvider(group, index); }}
                ondragend={() => { dragging = null; }}
              >
                <span class="routing-index" aria-hidden="true"><GripVertical size={16} />{index + 1}</span>
                <ProviderArtwork id={providerId} definition={definition} label={label} />
                <span class="routing-copy"><strong>{label}</strong><small>{definition ? sourceOriginLabel(definition) : providerId === "backend" ? "Media server" : providerId === "kept" ? "Kept lyrics" : "Source"}</small></span>
                {#if audioGroups.has(group.id)}<span class="routing-quality-label">{scoreFor(providerId)?.quality?.typical ?? "—"}</span>{/if}
                {#if localLyrics.has(providerId)}<span class="score-unavailable">Local</span>
                {:else if scoreFor(providerId)}
                  {@const ranked = isRanked(definition?.useMode)}
                  {@const value = ranked ? capabilityScore(scoreFor(providerId), group.id) : null}
                  <button type="button" class="score-toggle" aria-expanded={scoreOpen && openScore === scoreKey}
                    aria-label={ranked ? `${label} score ${value ?? "not rated yet"}. ${scoreOpen && openScore === scoreKey ? "Hide" : "Show"} details.` : `${label} is ${definition?.useMode === "off" ? "turned off" : "manual only"} and isn't scored. ${scoreOpen && openScore === scoreKey ? "Hide" : "Show"} details.`}
                    onclick={() => toggleScore(scoreKey)}>
                    <span class="score-chip" data-tier={scoreTier(value)}>{ranked ? value ?? "New" : definition?.useMode === "off" ? "Off" : "Manual"}</span>
                  </button>
                {:else}<span class="score-unavailable" aria-label="Score not measured">—</span>
                {/if}
                <details class="routing-move"><summary aria-label={`Reorder ${label}`}><ChevronDown size={16} aria-hidden="true" /></summary><span class="routing-actions">
                  <Button variant="text" size="icon" aria-label={`Move ${label} up`} disabled={index === 0} onclick={() => moveProvider(group, index, -1)}><ArrowUp size={18} aria-hidden="true" /></Button>
                  <Button variant="text" size="icon" aria-label={`Move ${label} down`} disabled={index === (orders[group.id] ?? group.providers).length - 1} onclick={() => moveProvider(group, index, 1)}><ArrowDown size={18} aria-hidden="true" /></Button>
                </span></details>
              </li>
            {/each}
          </ol>
          {#if (orders[group.id] ?? group.providers).length > 5}<footer><Button variant="text" onclick={() => expanded = !expanded}>{expanded ? "Show fewer" : `Show ${(orders[group.id] ?? group.providers).length - 5} more`}</Button></footer>{/if}
        </section>
      {/each}
    </div>
  </div>
{/if}

{#if openScore}
  {@const groupId = openScore.slice(0, openScore.indexOf(":"))}
  {@const providerId = openScore.slice(openScore.indexOf(":") + 1)}
  {@const selectedScore = scoreFor(providerId)}
  {@const scoreOrder = (orders[groupId] ?? []).filter(id => scoreFor(id))}
  {@const scoreIndex = scoreOrder.indexOf(providerId)}
  {#if selectedScore}
    {#await import("./patterns/SidePanel.svelte") then { default: SidePanel }}
      <SidePanel bind:open={scoreOpen} title={provider(providerId)?.name ?? humanize(providerId)} description="Source score" navigationLabel="source" onPrevious={scoreIndex > 0 ? () => toggleScore(`${groupId}:${scoreOrder[scoreIndex - 1]}`) : undefined} onNext={scoreIndex >= 0 && scoreIndex < scoreOrder.length - 1 ? () => toggleScore(`${groupId}:${scoreOrder[scoreIndex + 1]}`) : undefined}>
        <div class="routing-score-panel">{#await import("./ProviderScoreDetails.svelte") then { default: Details }}<Details score={selectedScore} capability={groupId} ranked={isRanked(provider(providerId)?.useMode)} {qualityTarget} />{/await}</div>
      </SidePanel>
    {/await}
  {/if}
{/if}

<style>
  .routing-tabs { min-width: 0; padding: 0 var(--space-4) var(--space-3); overflow-x: auto; }
  .routing-move summary { display: grid; place-items: center; min-height: 44px; min-width: 44px; cursor: pointer; list-style: none; color: var(--color-ink-muted); }
  .routing-move summary::-webkit-details-marker { display: none; }
  .routing-move { position: relative; }
  .routing-move:not([open]) .routing-actions { display: none; }
  .routing-move[open] .routing-actions { display: flex; width: max-content; position: absolute; z-index: 1; right: 0; bottom: 100%; padding: var(--space-1); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-popover); }
  .routing-copy small { display: block; color: var(--color-ink-muted); }

  .routing-score-panel :global(.score-details) { padding: 0; }
  :global(.routing-group li) { grid-template-columns: 3rem 2.5rem minmax(0, 1fr) auto auto auto; }
  :global(.routing-group li.pinned) { grid-template-columns: 3rem 2.5rem minmax(0, 1fr); }
  .score-toggle { display: inline-flex; align-items: center; gap: var(--space-2); min-height: 2.5rem; padding: var(--space-1) var(--space-2); border: 0; border-radius: 999px; background: transparent; color: var(--color-ink-muted); font-size: 0.8125rem; font-weight: 500; cursor: pointer; transition: background-color 200ms var(--ease-emphasized), color 200ms var(--ease-emphasized); }
  @media (max-width: 560px) {
    :global(.routing-group li) { grid-template-columns: 2.25rem 2.5rem minmax(0, 1fr) auto; row-gap: var(--space-2); }
    :global(.routing-group li.pinned) { grid-template-columns: 2.25rem 2.5rem minmax(0, 1fr); }
    .routing-copy { grid-column: 3 / -1; }
    .score-toggle { grid-column: 3; grid-row: 2; justify-self: start; min-height: 44px; }
    .score-unavailable { grid-column: 3; grid-row: 2; justify-self: start; }
    .routing-move { grid-column: 4; grid-row: 2; }
    .routing-quality-label { grid-column: 3; }
  }
  .routing-quality-label { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .score-unavailable { min-width: 2.75rem; color: var(--color-ink-muted); text-align: center; font-size: var(--text-sm); }
  :global(.routing-group li.has-score) { cursor: pointer; transition: background-color 200ms var(--ease-emphasized); }
  :global(.routing-group li.has-score:hover) { background: color-mix(in srgb, var(--color-ink) 4%, transparent); }
  .score-toggle:hover { background: color-mix(in srgb, var(--color-ink) 8%, transparent); color: var(--color-ink); }
  .score-toggle:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 2px; }
  .score-chip { display: inline-flex; align-items: center; justify-content: center; min-width: 2.75rem; min-height: 1.75rem; padding-inline: var(--space-2); border: 1px solid transparent; border-radius: 999px; font-size: var(--text-sm); font-variant-numeric: tabular-nums; font-weight: 700; }
  .score-chip[data-tier="great"] { background: var(--success-container); color: var(--on-success-container); }
  .score-chip[data-tier="good"] { background: var(--md-sys-color-secondary-container); color: var(--md-sys-color-on-secondary-container); }
  .score-chip[data-tier="fair"] { background: var(--warning-container); color: var(--on-warning-container); }
  .score-chip[data-tier="poor"] { background: var(--md-sys-color-error-container); color: var(--md-sys-color-on-error-container); }
  .score-chip[data-tier="none"] { border-color: var(--color-edge); color: var(--color-ink-muted); }
</style>
