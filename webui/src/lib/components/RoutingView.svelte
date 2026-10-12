<script lang="ts">
  import { onMount } from "svelte";
  import { ArrowDown, ArrowUp, GripVertical, Lock } from "@lucide/svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { home, settings, type PriorityGroup, type UiSchema } from "$lib/api";
  import AudioQualityField from "$lib/components/AudioQualityField.svelte";
  import ProviderArtwork from "$lib/components/ProviderArtwork.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import { humanize } from "$lib/sources";
  import { fieldValue, mergeRoutingOrders, move } from "$lib/settings";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";

  let schema = $state<UiSchema | null>(null);
  let config = $state<Record<string, unknown>>({});
  let orders = $state<Record<string, string[]>>({});
  let loading = $state(true);
  let refreshing = $state(false);
  let action = $state("");
  let error = $state("");
  let feedback = $state("");
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
    if (schema && nextConfig)
      orders = mergeRoutingOrders(nextConfig, schema.priorityGroups ?? [], orders, dirtyOrderIds);
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
      feedback = `${group.label} saved.`;
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Provider routing could not be saved.";
    } finally {
      action = "";
    }
  }

  function moveProvider(group: PriorityGroup, index: number, direction: -1 | 1) {
    const order = orders[group.id] ?? [];
    const providerId = order[index];
    orders = { ...orders, [group.id]: move(order, index, direction) };
    if (!dirtyOrderIds.includes(group.id)) dirtyOrderIds = [...dirtyOrderIds, group.id];
    feedback = `${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + direction + 1}.`;
  }

  function dropProvider(group: PriorityGroup, index: number) {
    if (!dragging || dragging.groupId !== group.id || dragging.index === index) return;
    const order = [...(orders[group.id] ?? [])];
    const [providerId] = order.splice(dragging.index, 1);
    order.splice(index, 0, providerId);
    orders = { ...orders, [group.id]: order };
    if (!dirtyOrderIds.includes(group.id)) dirtyOrderIds = [...dirtyOrderIds, group.id];
    feedback = `${provider(providerId)?.name ?? humanize(providerId)} moved to position ${index + 1}.`;
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
      feedback = "Playback and matching preferences saved.";
      await refresh();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Playback and matching preferences could not be saved.";
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
    <header class="sources-heading">
      <div>
        <h2>Routing</h2>
        <p>Allstarr always plays from your media server first when it has the song, then tries these services in the order below.</p>
      </div>
    </header>
    {#if error}
      <div class="degraded-banner" role="status">
        <span aria-hidden="true">!</span><p><strong>Routing may be out of date.</strong> {error}</p>
        <Button variant="secondary" size="sm" disabled={refreshing} onclick={() => void refresh()}>{refreshing ? "Trying again…" : "Retry"}</Button>
      </div>
    {/if}
    {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}
    {#if policyFields.length}
      <form class="settings-card routing-policy" aria-label="Playback and matching" oninput={() => { policyDirty = true; }} onsubmit={savePolicy}>
        <header><div><h3>Playback and matching</h3><small>Earlier services in the order below win close matches: the first gets 5 confidence points, the second 3, the third 1.</small></div></header>
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
            <div><h3>{group.label} order</h3><small>{group.description || "Drag services, or use the arrows, to change the order Allstarr tries them."}</small></div>
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
              <li
                draggable="true"
                class:dragging={dragging?.groupId === group.id && dragging.index === index}
                ondragstart={() => { dragging = { groupId: group.id, index }; }}
                ondragover={(event) => event.preventDefault()}
                ondrop={() => dropProvider(group, index)}
                ondragend={() => { dragging = null; }}
              >
                <span class="routing-index" aria-hidden="true"><GripVertical size={16} />{index + 1}</span>
                <ProviderArtwork id={providerId} definition={definition} />
                <span class="routing-copy"><strong>{definition?.name ?? humanize(providerId)}</strong><small>{definition?.categories?.map(humanize).join(" · ") || "Provider service"}</small></span>
                <span class="routing-actions">
                  <Button variant="ghost" size="icon" aria-label={`Move ${definition?.name ?? providerId} up`} disabled={index === 0} onclick={() => moveProvider(group, index, -1)}><ArrowUp size={18} aria-hidden="true" /></Button>
                  <Button variant="ghost" size="icon" aria-label={`Move ${definition?.name ?? providerId} down`} disabled={index === (orders[group.id] ?? group.providers).length - 1} onclick={() => moveProvider(group, index, 1)}><ArrowDown size={18} aria-hidden="true" /></Button>
                </span>
              </li>
            {/each}
          </ol>
        </section>
      {/each}
    </div>
  </div>
{/if}
