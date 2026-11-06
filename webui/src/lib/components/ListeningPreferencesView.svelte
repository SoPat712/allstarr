<script lang="ts">
  import { createOrderSave } from "$lib/order-save";
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { ArrowDown, ArrowUp, GripVertical } from "@lucide/svelte";
  import {
    ApiError,
    listeningPreferences,
    home,
    type ProviderDefinition,
    type UiSchema,
    type ExplicitFilter,
    type PlaybackStart,
    type PersonalListeningPreferences,
  } from "$lib/api";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import { canonicalProviderId, lyricsSourceDisplayName } from "$lib/sources";
  import { audioQualityOptions, move, playbackStartLabel, playbackStartOptions } from "$lib/settings";
  import SelectField from "$lib/components/SelectField.svelte";

  let { administrator = false }: { administrator?: boolean } = $props();

  const filterOptions: Array<{ value: ExplicitFilter; label: string; description: string }> = [
    { value: "All", label: "All", description: "Show clean and explicit releases." },
    { value: "ExplicitOnly", label: "Hide clean/edited versions", description: "Keep naturally clean, explicit, and unrated releases." },
    { value: "CleanOnly", label: "Clean only", description: "Hides explicit songs and songs whose rating isn't known." },
  ];

  let snapshot = $state<PersonalListeningPreferences | null>(null);
  let explicitFilter = $state("");
  let showExternalLabel = $state(false);
  let showExplicitLabel = $state(false);
  let playbackStart = $state<PlaybackStart>("Balanced");
  let qualityLimit = $state("");
  let providerDefinitions = $state<ProviderDefinition[]>([]);
  let lyricsOrder = $state<string[]>([]);
  let lyricsExpanded = $state(false), draggedLyrics = $state(-1);
  let initialLyricsOrder = $state("");
  let loading = $state(true);
  let busy = $state(false);
  let saving = $state(false);
  let error = $state("");
  let conflict = $state(false);
  const controlsDisabled = $derived(busy || saving || conflict);

  const householdQualityIndex = $derived(audioQualityOptions.findIndex(option => option.value === snapshot?.householdQuality));
  const householdQualityLabel = $derived(audioQualityOptions[householdQualityIndex]?.label ?? "Best available");
  const qualityChoices = $derived([
    { value: "", label: `Same as household · ${householdQualityLabel}` },
    ...audioQualityOptions.filter((_, index) => householdQualityIndex < 0 || index < householdQualityIndex),
  ]);

  const selectedFilter = $derived(
    filterOptions.find((option) => option.value === explicitFilter)?.value ?? null,
  );
  type PreferenceValues = {
    explicitFilter: string;
    showExternalLabel: boolean;
    showExplicitLabel: boolean;
    playbackStart: PlaybackStart;
    qualityLimit: string;
  };
  const valuesFor = (value: PersonalListeningPreferences): PreferenceValues => ({
    explicitFilter: value.cleanOnlyLocked ? "CleanOnly" : value.values.explicitFilter,
    showExternalLabel: value.values.showExternalLabel,
    showExplicitLabel: value.values.showExplicitLabel,
    playbackStart: value.values.playbackStart ?? "Balanced",
    qualityLimit: value.values.qualityLimit ?? "",
  });
  const dirty = $derived(Boolean(snapshot) && (
    explicitFilter !== snapshot?.values.explicitFilter ||
    showExternalLabel !== snapshot?.values.showExternalLabel ||
    showExplicitLabel !== snapshot?.values.showExplicitLabel ||
    playbackStart !== (snapshot?.values.playbackStart ?? "Balanced") ||
    qualityLimit !== (snapshot?.values.qualityLimit ?? "")
  ));

  function completeLyricsOrder(value?: string | null) {
    const configured = (value ?? "").split(",").map(id => canonicalProviderId(id.trim())).filter(Boolean);
    const candidates = [...["backend", "kept"].filter(id => !configured.includes(id)), ...configured, ...providerDefinitions
      .filter(provider => provider.categories?.includes("lyrics")).map(provider => provider.id).sort()];
    const included = new Set<string>();
    return candidates.map(id => id.trim()).filter(id => {
      const canonical = canonicalProviderId(id);
      if (!id || included.has(canonical)) return false;
      included.add(canonical);
      return true;
    });
  }

  function moveLyrics(index: number, direction: -1 | 1) {
    changeLyrics(move(lyricsOrder, index, direction));
  }

  const orderSave = createOrderSave({
    save: async (_, order) => {
      if (!snapshot) return;
      busy = true;
      try {
        const next = await listeningPreferences.save({
          explicitFilter: snapshot.values.explicitFilter as ExplicitFilter,
          showExternalLabel: snapshot.values.showExternalLabel,
          showExplicitLabel: snapshot.values.showExplicitLabel,
          playbackStart: snapshot.values.playbackStart ?? "Balanced",
          lyricsOrder: order.join(","),
          expectedRevision: snapshot.revision,
        });
        snapshot = next;
        initialLyricsOrder = completeLyricsOrder(next.values.lyricsOrder).join(",");
      } finally { busy = false; }
    },
    saved: (_, order, previous) => {
      error = "";
      conflict = false;
      if (lyricsOrder.join(",") === order.join(",")) lyricsOrder = initialLyricsOrder.split(",");
      toast.success("Order saved", { label: "Undo", run: () => changeLyrics(previous) });
    },
    failed: async (_, cause) => {
      const message = messageFor(cause, "Lyrics source order could not be saved.");
      if (!conflict && snapshot) apply(snapshot);
      error = message;
      toast.error(message);
    },
  });

  function changeLyrics(order: string[]) {
    if (!snapshot || controlsDisabled || !selectedFilter) return;
    const previous = lyricsOrder;
    if (previous.join(",") === order.join(",")) return;
    lyricsOrder = order;
    orderSave.change("lyrics", order, previous);
  }

  function dropLyrics(event: DragEvent, index: number) {
    event.preventDefault();
    if (draggedLyrics < 0 || draggedLyrics === index || controlsDisabled) return;
    const order = [...lyricsOrder];
    const [item] = order.splice(draggedLyrics, 1);
    order.splice(index, 0, item);
    draggedLyrics = -1;
    changeLyrics(order);
  }

  const lyricsName = (id: string) => lyricsSourceDisplayName(providerDefinitions, id);

  function apply(next: PersonalListeningPreferences) {
    snapshot = next;
    explicitFilter = next.cleanOnlyLocked ? "CleanOnly" : next.values.explicitFilter;
    showExternalLabel = next.values.showExternalLabel;
    showExplicitLabel = next.values.showExplicitLabel;
    playbackStart = next.values.playbackStart ?? "Balanced";
    qualityLimit = next.values.qualityLimit ?? "";
    lyricsOrder = completeLyricsOrder(next.values.lyricsOrder);
    initialLyricsOrder = lyricsOrder.join(",");
  }

  function messageFor(cause: unknown, action: string) {
    if (cause instanceof ApiError) {
      if (cause.status === 409) {
        conflict = true;
        return "These preferences changed in another session. Reload the latest settings before continuing.";
      }
      if (cause.status === 403)
        return "Your current session cannot update listening preferences. Sign in again and retry.";
      if (cause.status === 400)
        return "The preferences were not accepted. Review each choice and try again.";
    }
    return cause instanceof Error ? cause.message : action;
  }

  async function load(passive = false) {
    if (passive && (busy || saving || conflict || dirty || lyricsOrder.join(",") !== initialLyricsOrder)) return;
    loading = !snapshot;
    busy = Boolean(snapshot);
    error = "";
    conflict = false;
    try {
      const [preferences, catalog] = await Promise.allSettled([listeningPreferences.get(), home.schema()]);
      if (preferences.status === "rejected") throw preferences.reason;
      providerDefinitions = catalog.status === "fulfilled" ? (catalog.value as UiSchema).providers ?? [] : [];
      apply(preferences.value);
    } catch (cause) {
      error = messageFor(cause, "Listening preferences could not be loaded.");
    } finally {
      loading = false;
      busy = false;
    }
  }

  async function changePreference<Key extends keyof PreferenceValues>(field: Key, value: PreferenceValues[Key]) {
    if (!snapshot || controlsDisabled) return;
    const previous = snapshot;
    const values = { ...valuesFor(snapshot), [field]: value };
    if (valuesFor(snapshot)[field] === value || !filterOptions.some(option => option.value === values.explicitFilter)) return;
    explicitFilter = values.explicitFilter;
    showExternalLabel = values.showExternalLabel;
    showExplicitLabel = values.showExplicitLabel;
    playbackStart = values.playbackStart;
    qualityLimit = values.qualityLimit;
    saving = true;
    error = "";
    try {
      await orderSave.flush();
      if (!snapshot || conflict) return;
      apply(await listeningPreferences.save({
        ...values,
        explicitFilter: values.explicitFilter as ExplicitFilter,
        ...(values.qualityLimit !== (snapshot.values.qualityLimit ?? "") ? { qualityLimit: values.qualityLimit } : { qualityLimit: undefined }),
        expectedRevision: snapshot.revision,
      }));
      toast.success("Listening preferences saved.", { label: "Undo", run: async () => {
        if (!snapshot || controlsDisabled) return;
        const restored = { ...valuesFor(snapshot), [field]: valuesFor(previous)[field] };
        if (previous.usesHouseholdDefaults && JSON.stringify(restored) === JSON.stringify(valuesFor(previous)) &&
            (snapshot.values.lyricsOrder ?? "") === (previous.values.lyricsOrder ?? "")) await reset();
        else await changePreference(field, valuesFor(previous)[field]);
      } });
    } catch (cause) {
      error = messageFor(cause, "Listening preferences could not be saved.");
      if (!conflict && snapshot) apply(snapshot);
      toast.error(error);
    } finally { saving = false; }
  }

  async function restore(previous: PersonalListeningPreferences) {
    if (!snapshot || controlsDisabled) return;
    const values = valuesFor(previous);
    if (!filterOptions.some(option => option.value === values.explicitFilter)) return;
    saving = true;
    error = "";
    try {
      await orderSave.flush();
      if (!snapshot || conflict) return;
      apply(await listeningPreferences.save({
        ...values, explicitFilter: values.explicitFilter as ExplicitFilter,
        lyricsOrder: previous.values.lyricsOrder ?? "", expectedRevision: snapshot.revision,
      }));
      toast.success("Listening preferences restored.");
    } catch (cause) {
      error = messageFor(cause, "Listening preferences could not be restored.");
      toast.error(error);
    } finally { saving = false; }
  }

  async function reset() {
    if (!snapshot || controlsDisabled) return;
    saving = true;
    error = "";
    try {
      await orderSave.flush();
      if (!snapshot || conflict) return;
      const previous = snapshot;
      apply(await listeningPreferences.reset(snapshot.revision));
      toast.success("Household defaults restored.", filterOptions.some(option => option.value === previous.values.explicitFilter)
        ? { label: "Undo", run: () => restore(previous) } : undefined);
    } catch (cause) {
      error = messageFor(cause, "Household defaults could not be restored.");
      toast.error(error);
    } finally { saving = false; }
  }

  const scheduler = createRefreshScheduler(() => load(true));
  onMount(() => {
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduler.schedule);
    return () => { unsubscribe(); scheduler.cancel(); void orderSave.flush(); };
  });
</script>

{#if loading}
  <RowsSkeleton count={6} />
{:else if !snapshot}
  <ErrorState title="Listening preferences could not be loaded." detail={error} onretry={load} />
{:else}
  <section class="preferences-view" aria-busy={busy || saving}>
    <PageStatus label="Listening preferences status" items={[
      { label: snapshot.usesHouseholdDefaults ? "Using household defaults" : "Personal choices" },
      { label: busy || saving ? "Saving…" : conflict ? "Reload needed" : "Changes save automatically", tone: conflict ? "warn" : "neutral" },
    ]} />
    {#if error}
      <div class="degraded-banner" role="alert">
        <span aria-hidden="true">!</span>
        <p><strong>Preferences were not changed.</strong> {error}</p>
        {#if conflict}
          <Button variant="secondary" disabled={busy || saving} onclick={() => void load()}>Reload latest</Button>
        {/if}
      </div>
    {/if}


    <section class="settings-card preferences-panel" aria-label="What you hear">
      <header><h3>What you hear</h3></header>
      <div class="settings-row">
        <div><strong>Explicit songs</strong><small>{snapshot.cleanOnlyLocked ? "Your administrator turned this on." : filterOptions.find(option => option.value === explicitFilter)?.description ?? "Choose a release filter."}</small></div>
        <SelectField label="Explicit songs" value={explicitFilter} options={filterOptions} disabled={controlsDisabled || snapshot.cleanOnlyLocked} onchange={value => void changePreference("explicitFilter", value)} />
      </div>
      {#if !selectedFilter}<p class="unknown-value" role="status">Saved explicit filter: <strong>{snapshot.values.explicitFilter}</strong>. Choose a supported option to replace it.</p>{/if}
      <div class="settings-row"><div><strong>Mark external songs</strong><small>Show an A next to songs from outside your library.</small></div><span class="settings-toggle"><Switch checked={showExternalLabel} disabled={controlsDisabled || !selectedFilter} onchange={event => void changePreference("showExternalLabel", event.currentTarget.checked)} aria-label="Show [A] for external tracks" /></span></div>
      <div class="settings-row"><div><strong>Mark explicit songs</strong><small>Show an E next to explicit songs in Allstarr.</small></div><span class="settings-toggle"><Switch checked={showExplicitLabel} disabled={controlsDisabled || !selectedFilter} onchange={event => void changePreference("showExplicitLabel", event.currentTarget.checked)} aria-label="Show [E] for explicit tracks" /></span></div>
      <div class="settings-row playback-row"><div><strong>Playback start</strong><small>Balanced favors quality when starts stay under 3 seconds.</small></div><ThreeDetentSwitch label="Playback start" value={playbackStart} options={playbackStartOptions} disabled={controlsDisabled || !selectedFilter} onchange={value => void changePreference("playbackStart", value)} /></div>
      <div class="settings-row"><div><strong>Quality limit</strong><small>You can choose less than the household’s limit, not more.</small></div><SelectField value={qualityLimit} label="Quality limit" options={qualityChoices} disabled={controlsDisabled || !selectedFilter} onchange={value => void changePreference("qualityLimit", value)} /></div>
    </section>

    <div class="settings-card preferences-panel">
      <section class="preference-options" aria-label="Lyrics source order">
        <div>
          <h3>Lyrics source order</h3>
          <p>A version you picked for a song always wins. Otherwise word timing beats line timing, then plain text; this order breaks ties.</p>
        </div>
        {#if !lyricsOrder.length}<EmptyState title="No lyrics sources available" description="Available lyrics sources appear here when they are enabled for your account." />{:else}
        <ol class="lyrics-order-list">
          {#each lyricsOrder.slice(0, lyricsExpanded ? undefined : 6) as providerId, index (providerId)}
            <li draggable={!controlsDisabled && Boolean(selectedFilter)} ondragstart={event => { draggedLyrics = index; event.dataTransfer?.setData("text/plain", String(index)); }} ondragover={event => event.preventDefault()} ondrop={event => dropLyrics(event, index)} ondragend={() => draggedLyrics = -1}>
              <span class="lyrics-grip" aria-hidden="true"><GripVertical size={18} /></span>
              <strong>{lyricsName(providerId)}</strong>
              <span class="lyrics-order-actions">
                <Button variant="text" size="icon" aria-label={`Move ${lyricsName(providerId)} up`} disabled={controlsDisabled || !selectedFilter || index === 0} onclick={() => moveLyrics(index, -1)}><ArrowUp size={18} aria-hidden="true" /></Button>
                <Button variant="text" size="icon" aria-label={`Move ${lyricsName(providerId)} down`} disabled={controlsDisabled || !selectedFilter || index === lyricsOrder.length - 1} onclick={() => moveLyrics(index, 1)}><ArrowDown size={18} aria-hidden="true" /></Button>
              </span>
            </li>
          {/each}
        </ol>
        {#if lyricsOrder.length > 6}<Button variant="text" onclick={() => lyricsExpanded = !lyricsExpanded}>{lyricsExpanded ? "Show less" : `Show ${lyricsOrder.length - 6} more`}</Button>{/if}
        {/if}
      </section>

    </div>

    <div class="settings-card preferences-panel elsewhere-panel">
      <header class="panel-heading"><h3>Elsewhere</h3></header>
      <section class="preference-options" aria-labelledby="library-profile-title">
        <div class="preference-link-row">
          <div><h3 id="library-profile-title">Adding to the library</h3><p>Choose file names, quality and extras for your library copies.</p></div>
          <Button variant="secondary" href="#/settings/adding-to-library">Library profile</Button>
        </div>
      </section>
      <section class="preference-options" aria-labelledby="problem-alerts-title">
        <div class="preference-link-row">
          <div>
            <h3 id="problem-alerts-title">Alerts</h3>
            <p>Where Allstarr tells you about your own accounts.</p>
          </div>
          <Button variant="text" href="#/settings/alerts">Manage alerts</Button>
        </div>
      </section>

      {#if administrator}<div class="settings-row"><div><strong>Household limits</strong><small>The highest quality everyone can use.</small></div><Button variant="text" href="#/settings/quality">Change the household limit</Button></div>{/if}
    </div>

      <footer class="preferences-actions">
        <p>
          Household default: {snapshot.householdDefaults.explicitFilter === "ExplicitOnly" ? "Hide clean/edited versions" : snapshot.householdDefaults.explicitFilter === "CleanOnly" ? "Clean only" : snapshot.householdDefaults.explicitFilter}
          · [A] {snapshot.householdDefaults.showExternalLabel ? "on" : "off"}
          · [E] {snapshot.householdDefaults.showExplicitLabel ? "on" : "off"}
          · Playback: {playbackStartLabel(snapshot.householdDefaults.playbackStart)}
        </p>
        <div>
          <Button variant="text" disabled={controlsDisabled || snapshot.usesHouseholdDefaults} onclick={() => void reset()}>
            Reset to household defaults
          </Button>

        </div>
      </footer>
  </section>
{/if}

<style>
  .preferences-view { display: grid; min-width: 0; gap: var(--space-4); font-size: var(--text-sm); }
  .preferences-panel { min-width: 0; overflow: hidden; }
  .preferences-panel > header { padding: var(--space-3) var(--space-4); }
  .preferences-panel h3 { margin: 0; font-size: var(--text-sm); font-weight: 600; }
  .preferences-panel .settings-row :global(.select-root) { min-width: 0; }
  .preference-options { min-width: 0; }
  .preference-options > div:first-child { padding: var(--space-3) var(--space-4); }
  .preference-options p, .preferences-actions p { margin: var(--space-2) 0 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
  .lyrics-order-list { counter-reset: lyrics-rank; margin: 0; padding: 0; list-style: none; }
  .lyrics-order-list li { counter-increment: lyrics-rank; display: grid; grid-template-columns: 20px 1.5rem minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); border-top: 1px solid var(--color-edge); padding: var(--space-2) var(--space-4); }
  .lyrics-order-list strong { min-width: 0; font-size: var(--text-sm); }
  .lyrics-order-list li::before { grid-column: 2; grid-row: 1; content: counter(lyrics-rank); color: var(--color-ink-muted); font-variant-numeric: tabular-nums; text-align: center; }
  .lyrics-grip { grid-column: 1; grid-row: 1; color: var(--color-ink-muted); cursor: grab; }
  .lyrics-order-actions { display: flex; }
  .preferences-actions { display: flex; align-items: start; justify-content: space-between; gap: var(--space-3); }
  .preferences-actions p { margin: 0; font-size: var(--text-xs); }
  .unknown-value { padding: var(--space-3) var(--space-4); font-size: var(--text-sm); }
  .preference-link-row { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  .preference-link-row p { margin: var(--space-1) 0 0; color: var(--color-ink-muted); font-size: var(--text-sm); }
  @media (max-width: 900px) { .playback-row { grid-template-columns: minmax(0, 1fr) !important; } }
  @media (max-width: 599px) { .preferences-actions, .preference-link-row { flex-wrap: wrap; } .lyrics-order-list li { gap: var(--space-2); } }
</style>
