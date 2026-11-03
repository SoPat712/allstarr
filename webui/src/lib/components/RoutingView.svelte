<script lang="ts">
  import { toast } from "$lib/toasts";
  let { embedded = false }: { embedded?: boolean } = $props();
  import { onMount } from "svelte";
  import { ArrowDown, ArrowUp, ChevronDown, GripVertical, Lock, Sparkles } from "@lucide/svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { home, settings, sources, type PriorityGroup, type ProviderScore, type UiSchema } from "$lib/api";
  import AudioQualityField from "$lib/components/AudioQualityField.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import ProviderScoreDetails from "$lib/components/ProviderScoreDetails.svelte";
  import { capabilityScore, isRanked, scoreTier, sortByScore as orderByScore } from "$lib/provider-score";
  import RouteError from "$lib/components/RouteError.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import { canonicalProviderId, humanize, lyricsSourceDisplayName, sourceOriginLabel } from "$lib/sources";
  import { fieldValue, mergeRoutingOrders, move } from "$lib/settings";
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
  const toggleScore = (key: string) => { openScore = openScore === key ? "" : key; };
  const scoreFor = (providerId: string) => scores.find((item) => item.providerId.toLowerCase() === providerId.toLowerCase());

  // Manual sources sink to the bottom whatever their score. Nothing saves until Save order.
  function sortByScore(group: PriorityGroup) {
    const order = orders[group.id] ?? group.providers;
    const fixed: string[] = order.filter((id) => id === "kept" || id === "backend");
    const sorted = orderByScore(order.filter((id) => !fixed.includes(id)),
      (id) => capabilityScore(scoreFor(id), group.id), (id) => isRanked(provider(id)?.useMode));
    orders = { ...orders, [group.id]: [...fixed, ...sorted] };
    if (!dirtyOrderIds.includes(group.id)) dirtyOrderIds = [...dirtyOrderIds, group.id];
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
  let policyDirty = $state(false);
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

  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    error = "";
    const results = await Promise.allSettled([home.schema(), settings.config()]);
    if (results[0].status === "fulfilled") schema = results[0].value as UiSchema;
    const nextConfig = results[1].status === "fulfilled"
      ? results[1].value as Record<string, unknown>
      : null;
    if (nextConfig && !policyDirty) config = nextConfig;
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
  }

  const refreshScheduler = createRefreshScheduler(refresh);
  const scheduleRefresh = refreshScheduler.schedule;

  async function saveOrder(group: PriorityGroup) {
    if (action) return;
    action = group.id;
    try {
      await settings.save({ [group.envKey]: (orders[group.id] ?? []).join(",") });
      dirtyOrderIds = dirtyOrderIds.filter((id) => id !== group.id);
      toast.success(`${group.label} saved.`);
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Provider routing could not be saved.");
    } finally {
      action = "";
    }
  }

  function moveProvider(group: PriorityGroup, index: number, direction: -1 | 1) {
    const order = orders[group.id] ?? [];
    const providerId = order[index];
    orders = { ...orders, [group.id]: move(order, index, direction) };
    if (!dirtyOrderIds.includes(group.id)) dirtyOrderIds = [...dirtyOrderIds, group.id];
    toast.success(`${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + direction + 1}.`);
  }

  function dropProvider(group: PriorityGroup, index: number) {
    if (!dragging || dragging.groupId !== group.id || dragging.index === index) return;
    const order = [...(orders[group.id] ?? [])];
    const [providerId] = order.splice(dragging.index, 1);
    order.splice(index, 0, providerId);
    orders = { ...orders, [group.id]: order };
    if (!dirtyOrderIds.includes(group.id)) dirtyOrderIds = [...dirtyOrderIds, group.id];
    toast.success(`${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + 1}.`);
    dragging = null;
  }

  async function savePolicy(event: SubmitEvent) {
    event.preventDefault();
    if (action) return;
    action = "policy";
    const data = new FormData(event.currentTarget as HTMLFormElement);
    const updates = Object.fromEntries(policyFields.map((field) => [field.key, String(data.get(field.key) ?? "")]));
    try {
      await settings.save(updates);
      loadScores();
      policyDirty = false;
      toast.success("Playback and matching preferences saved.");
      await refresh();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Playback and matching preferences could not be saved.");
    } finally {
      action = "";
    }
  }

  onMount(() => {
    void refresh();
    loadScores();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
    };
  });
</script>

{#if loading}
  <Skeleton class="panel settings-panel skeleton-panel" aria-label="Loading routing" aria-busy="true" />
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

    {#if policyFields.length}
      <form class="settings-card routing-policy" aria-label="Playback and matching" oninput={() => { policyDirty = true; }} onsubmit={savePolicy}>
        <header><div><h3>Quality and matching</h3><small>Match scores describe the recording evidence. Streaming priority breaks score ties and guides playback; preference windows apply only to tentative matches.</small></div></header>
        {#each policyFields as field (field.key)}
          {#if field.type === "audio-quality"}
            <div class="settings-row routing-quality audio-quality-field">
              <div><strong>{field.label}</strong>{#if field.helpText}<small>{field.helpText}</small>{/if}</div>
              <AudioQualityField name={field.key} value={String(fieldValue(config, field))} onchange={() => { policyDirty = true; }} />
            </div>
          {:else}
            <label class="settings-row">
              <div><strong>{field.label}</strong>{#if field.helpText}<small>{field.helpText}</small>{/if}</div>
              <input
                name={field.key}
                type="number"
                value={String(fieldValue(config, field))}
                min={field.min ?? undefined}
                max={field.max ?? undefined}
              />
            </label>
          {/if}
        {/each}
        <footer>
          {#if policyDirty}<span>Unsaved changes</span>{/if}
          <Button type="submit" disabled={Boolean(action) || !policyDirty}>{action === "policy" ? "Saving…" : "Save playback and matching"}</Button>
        </footer>
      </form>
    {/if}
    {#if groups.length > 1}
      <SegmentedNav label="Source orders" items={groups.map((item) => ({ id: item.id, label: tabNames[item.id] ?? item.label.replace(/ (?:priority |search |discovery )?order$/i, "") }))}
        active={shown ?? ""} onchange={chooseTab} />
    {/if}
    <div class="routing-groups">
      {#each groups.filter((item) => item.id === shown) as group}
        <section class="settings-card routing-group">
          <header>
            <div><h3>{tabNames[group.id] ?? group.label.replace(/ order$/i, "")} order</h3><small>{group.id === "lyrics" ? "A lyrics choice saved for a song always wins. Otherwise the best-timed lyrics win: word by word, then line by line, then plain text. This order decides between equally timed lyrics." : group.description || "Drag services, or use the arrows, to change the order Allstarr tries them."}</small></div>
            {#if scores.length && group.id !== "lyrics"}<Button variant="outline" onclick={() => sortByScore(group)}><Sparkles size={16} aria-hidden="true" />Sort by score</Button>{/if}
            <Button aria-label={`Save ${group.label}`} disabled={Boolean(action) || !dirtyOrderIds.includes(group.id)} onclick={() => void saveOrder(group)}>{action === group.id ? "Saving…" : "Save order"}</Button>
          </header>
          <ol>
            {#if group.pinnedProvider}
              <li class="pinned">
                <span class="routing-index" aria-hidden="true"><Lock size={14} /></span>
                <ProviderArtwork id={group.pinnedProvider.id} label={group.pinnedProvider.name} />
                <span class="routing-copy"><strong>{group.pinnedProvider.name}</strong><small>{group.pinnedProvider.reason} · always tried first</small></span>
              </li>
            {/if}
            {#each orders[group.id] ?? group.providers as providerId, index}
              {@const definition = provider(providerId)}
              {@const label = group.id === "lyrics" ? lyricsSourceDisplayName(schema?.providers ?? [], providerId) : definition?.name ?? providerId}
              {@const scoreKey = `${group.id}:${providerId}`}
              <!-- svelte-ignore a11y_no_noninteractive_element_interactions, a11y_click_events_have_key_events -->
              <li
                draggable={true}
                class:has-score={Boolean(scoreFor(providerId))}
                onclick={(event) => { if (scoreFor(providerId) && !(event.target as Element).closest("button, a, input, .score-details")) toggleScore(scoreKey); }}
                class:dragging={dragging?.groupId === group.id && dragging.index === index}
                ondragstart={() => { dragging = { groupId: group.id, index }; }}
                ondragover={(event) => event.preventDefault()}
                ondrop={() => dropProvider(group, index)}
                ondragend={() => { dragging = null; }}
              >
                <span class="routing-index" aria-hidden="true"><GripVertical size={16} />{index + 1}</span>
                <ProviderArtwork id={providerId} definition={definition} label={label} />
                <span class="routing-copy"><span class="routing-name"><strong>{group.id === "lyrics" ? label : definition?.name ?? humanize(providerId)}</strong>{#if definition}<Badge tone="neutral" class="origin-badge">{sourceOriginLabel(definition)}</Badge>{/if}{#if scoreFor(providerId)?.quality}<span class="routing-quality-label" class:lossy={!scoreFor(providerId)?.quality?.lossless}>{scoreFor(providerId)?.quality?.typical}</span>{/if}</span><small>{providerId === "backend" ? "Lyrics supplied by your media server" : providerId === "kept" ? "Saved lyrics from your library" : definition?.categories?.map(humanize).join(" · ") || "Provider service"}</small></span>
                {#if scoreFor(providerId)}
                  {@const ranked = isRanked(definition?.useMode)}
                  {@const value = ranked ? capabilityScore(scoreFor(providerId), group.id) : null}
                  <button type="button" class="score-toggle" aria-expanded={openScore === scoreKey}
                    aria-label={ranked ? `${label} score ${value ?? "not rated yet"}. ${openScore === scoreKey ? "Hide" : "Show"} details.` : `${label} is ${definition?.useMode === "off" ? "turned off" : "manual only"} and isn't scored. ${openScore === scoreKey ? "Hide" : "Show"} details.`}
                    onclick={() => toggleScore(scoreKey)}>
                    <span class="score-chip" data-tier={scoreTier(value)}>{ranked ? value ?? "New" : definition?.useMode === "off" ? "Off" : "Manual"}</span>
                    <span class="score-toggle-label">{openScore === scoreKey ? "Hide" : "Details"}</span>
                    <ChevronDown size={16} aria-hidden="true" />
                  </button>
                {/if}
                <span class="routing-actions">
                  <Button variant="ghost" size="icon" aria-label={`Move ${label} up`} disabled={index === 0} onclick={() => moveProvider(group, index, -1)}><ArrowUp size={18} aria-hidden="true" /></Button>
                  <Button variant="ghost" size="icon" aria-label={`Move ${label} down`} disabled={index === (orders[group.id] ?? group.providers).length - 1} onclick={() => moveProvider(group, index, 1)}><ArrowDown size={18} aria-hidden="true" /></Button>
                </span>
                {#if openScore === scoreKey && scoreFor(providerId)}
                  <ProviderScoreDetails score={scoreFor(providerId)!} capability={group.id} ranked={isRanked(definition?.useMode)} {qualityTarget} onclose={() => toggleScore(scoreKey)} />
                {/if}
              </li>
            {/each}
          </ol>
        </section>
      {/each}
    </div>
  </div>
{/if}

<style>
  :global(.routing-group li) { grid-template-columns: 3rem 2.5rem minmax(0, 1fr) auto auto; }
  :global(.routing-group li.pinned) { grid-template-columns: 3rem 2.5rem minmax(0, 1fr); }
  .routing-name { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); }
  @media (max-width: 560px) {
    :global(.routing-group li) { grid-template-columns: 2.25rem 2.5rem minmax(0, 1fr) auto; row-gap: var(--space-2); }
    :global(.routing-group li.pinned) { grid-template-columns: 2.25rem 2.5rem minmax(0, 1fr); }
    .routing-copy { grid-column: 3 / -1; }
    .score-toggle { grid-column: 3; grid-row: 2; justify-self: start; }
    .routing-actions { grid-column: 4; grid-row: 2; }
  }
  .routing-quality-label { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .routing-quality-label.lossy { color: var(--on-warning-container); }
  :global(.dark) .routing-quality-label.lossy { color: var(--warning); }
  :global(.routing-group li.has-score) { cursor: pointer; transition: background-color 200ms var(--ease-emphasized); }
  :global(.routing-group li.has-score:hover) { background: color-mix(in srgb, var(--color-ink) 4%, transparent); }
  .score-toggle { display: inline-flex; align-items: center; gap: var(--space-2); min-height: 2.5rem; padding: var(--space-1) var(--space-2); border: 0; border-radius: 999px; background: transparent; color: var(--color-ink-muted); font-size: 0.8125rem; font-weight: 500; cursor: pointer; transition: background-color 200ms var(--ease-emphasized), color 200ms var(--ease-emphasized); }
  .score-toggle:hover { background: color-mix(in srgb, var(--color-ink) 8%, transparent); color: var(--color-ink); }
  .score-toggle:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 2px; }
  .score-toggle :global(svg) { transition: transform 240ms var(--ease-emphasized); }
  .score-toggle[aria-expanded="true"] :global(svg) { transform: rotate(180deg); }
  .score-toggle-label { min-width: 2.75rem; text-align: left; }
  .score-chip { display: inline-flex; align-items: center; justify-content: center; min-width: 2.75rem; min-height: 1.75rem; padding-inline: var(--space-2); border: 1px solid transparent; border-radius: 999px; font-size: var(--text-sm); font-variant-numeric: tabular-nums; font-weight: 700; }
  .score-chip[data-tier="great"] { background: var(--success-container); color: var(--on-success-container); }
  .score-chip[data-tier="good"] { background: var(--md-sys-color-secondary-container); color: var(--md-sys-color-on-secondary-container); }
  .score-chip[data-tier="fair"] { background: var(--warning-container); color: var(--on-warning-container); }
  .score-chip[data-tier="poor"] { background: var(--md-sys-color-error-container); color: var(--md-sys-color-on-error-container); }
  .score-chip[data-tier="none"] { border-color: var(--color-edge); color: var(--color-ink-muted); }
</style>
