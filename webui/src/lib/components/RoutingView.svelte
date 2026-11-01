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
  import { canonicalProviderId, humanize, lyricsSourceDisplayName, sourceOriginLabel } from "$lib/sources";
  import { fieldValue, keptLyricsFirst, mergeRoutingOrders, move } from "$lib/settings";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";

  let scores = $state<ProviderScore[]>([]);
  let openScore = $state("");
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
      if (orders.lyrics) orders.lyrics = keptLyricsFirst(orders.lyrics);
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
    if (group.id === "lyrics" && (order[index] === "kept" || order[index + direction] === "kept")) return;
    const providerId = order[index];
    orders = { ...orders, [group.id]: move(order, index, direction) };
    if (!dirtyOrderIds.includes(group.id)) dirtyOrderIds = [...dirtyOrderIds, group.id];
    toast.success(`${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + direction + 1}.`);
  }

  function dropProvider(group: PriorityGroup, index: number) {
    if (!dragging || dragging.groupId !== group.id || dragging.index === index) return;
    const order = [...(orders[group.id] ?? [])];
    if (group.id === "lyrics" && (order[index] === "kept" || order[dragging.index] === "kept")) {
      dragging = null;
      return;
    }
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
    void sources.scores().then((result) => { scores = result.providers ?? []; }).catch(() => {});
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
    <div class="routing-groups">
      {#each schema.priorityGroups ?? [] as group}
        <section class="settings-card routing-group">
          <header>
            <div><h3>{group.label.replace(/ order$/i, "")} order</h3><small>{group.id === "lyrics" ? "Your saved choice for a song takes precedence, then Kept LRC. Other sources follow this order, with a short priority window for slower services." : group.description || "Drag services, or use the arrows, to change the order Allstarr tries them."}</small></div>
            {#if scores.length && group.id !== "lyrics"}<Button variant="ghost" onclick={() => sortByScore(group)}><Sparkles size={16} aria-hidden="true" />Sort by score</Button>{/if}
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
              {@const fixed = group.id === "lyrics" && providerId === "kept"}
              <li
                draggable={!fixed}
                class:pinned={fixed}
                class:dragging={dragging?.groupId === group.id && dragging.index === index}
                ondragstart={(event) => { if (fixed) event.preventDefault(); else dragging = { groupId: group.id, index }; }}
                ondragover={(event) => event.preventDefault()}
                ondrop={() => dropProvider(group, index)}
                ondragend={() => { dragging = null; }}
              >
                <span class="routing-index" aria-hidden="true">{#if fixed}<Lock size={14} />{:else}<GripVertical size={16} />{index + 1}{/if}</span>
                <ProviderArtwork id={providerId} definition={definition} label={label} />
                <span class="routing-copy"><span class="routing-name"><strong>{group.id === "lyrics" ? label : definition?.name ?? humanize(providerId)}</strong>{#if definition}<Badge tone="neutral" class="origin-badge">{sourceOriginLabel(definition)}</Badge>{/if}{#if scoreFor(providerId)?.quality}<span class="routing-quality-label" class:lossy={!scoreFor(providerId)?.quality?.lossless}>{scoreFor(providerId)?.quality?.typical}</span>{/if}</span><small>{providerId === "backend" ? "Lyrics supplied by your media server" : fixed ? "Saved lyrics from your library" : definition?.categories?.map(humanize).join(" · ") || "Provider service"}</small></span>
                {#if scoreFor(providerId)}
                  {@const ranked = isRanked(definition?.useMode)}
                  {@const value = ranked ? capabilityScore(scoreFor(providerId), group.id) : null}
                  <button type="button" class="score-chip" data-tier={scoreTier(value)} aria-expanded={openScore === `${group.id}:${providerId}`}
                    aria-label={ranked ? `${label} score ${value ?? "not rated yet"}. Show details.` : `${label} is ${definition?.useMode === "off" ? "turned off" : "manual only"} and isn't scored. Show details.`}
                    onclick={() => openScore = openScore === `${group.id}:${providerId}` ? "" : `${group.id}:${providerId}`}>
                    {ranked ? value ?? "New" : definition?.useMode === "off" ? "Off" : "Manual"}<ChevronDown size={14} aria-hidden="true" />
                  </button>
                {/if}
                <span class="routing-actions">
                  <Button variant="ghost" size="icon" aria-label={`Move ${label} up`} disabled={fixed || index === 0 || group.id === "lyrics" && orders[group.id]?.[index - 1] === "kept"} onclick={() => moveProvider(group, index, -1)}><ArrowUp size={18} aria-hidden="true" /></Button>
                  <Button variant="ghost" size="icon" aria-label={`Move ${label} down`} disabled={fixed || index === (orders[group.id] ?? group.providers).length - 1} onclick={() => moveProvider(group, index, 1)}><ArrowDown size={18} aria-hidden="true" /></Button>
                </span>
                {#if openScore === `${group.id}:${providerId}` && scoreFor(providerId)}
                  <ProviderScoreDetails score={scoreFor(providerId)!} capability={group.id} ranked={isRanked(definition?.useMode)} />
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
  .routing-quality-label { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .routing-quality-label.lossy { color: var(--on-warning-container); }
  :global(.dark) .routing-quality-label.lossy { color: var(--warning); }
  .score-chip { display: inline-flex; align-items: center; gap: 2px; min-height: 2rem; padding-inline: var(--space-3) var(--space-2); border: 1px solid transparent; border-radius: 999px; font-size: var(--text-sm); font-weight: 600; cursor: pointer; transition: background-color 160ms var(--ease-standard), scale 160ms var(--ease-standard); }
  .score-chip :global(svg) { transition: transform 240ms var(--ease-emphasized); }
  .score-chip[aria-expanded="true"] :global(svg) { transform: rotate(180deg); }
  .score-chip[data-tier="great"] { background: var(--success-container); color: var(--on-success-container); }
  .score-chip[data-tier="good"] { background: var(--md-sys-color-secondary-container); color: var(--md-sys-color-on-secondary-container); }
  .score-chip[data-tier="fair"] { background: var(--warning-container); color: var(--on-warning-container); }
  .score-chip[data-tier="poor"] { background: var(--md-sys-color-error-container); color: var(--md-sys-color-on-error-container); }
  .score-chip[data-tier="none"] { border-color: var(--color-edge); color: var(--color-ink-muted); }
  .score-chip:active { scale: 0.96; }
  .score-chip:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 2px; }
</style>
