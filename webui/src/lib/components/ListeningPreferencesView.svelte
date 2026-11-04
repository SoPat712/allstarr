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
    <header class="preferences-intro">

      <h2>Tune your listening experience</h2>
      <p>
        These choices affect only your listener account. They save as you change them. Native library titles remain untouched.
      </p>
    </header>

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


    <div class="panel preferences-panel">
      <header class="panel-heading preferences-heading">
        <div>

          <h3>What you hear</h3>
          <p>Choose which songs appear in your Allstarr results.</p>
        </div>

      </header>

      <fieldset class="filter-options" disabled={controlsDisabled || snapshot.cleanOnlyLocked}>
        <legend>Release filter</legend>
        {#if snapshot.cleanOnlyLocked}<p class="unknown-value">Your administrator turned this on.</p>{/if}
        {#each filterOptions as option}
          <label class:active={explicitFilter === option.value}>
            <input checked={explicitFilter === option.value} onchange={() => void changePreference("explicitFilter", option.value)} type="radio" name="explicit-filter" value={option.value} />
            <span><strong>{option.label}</strong><small>{option.description}</small></span>
          </label>
        {/each}
        {#if !selectedFilter}
          <p class="unknown-value" role="status">
            Saved explicit filter: <strong>{snapshot.values.explicitFilter}</strong>. Choose a supported option to replace it.
          </p>
        {/if}
      </fieldset>

      <section class="label-options" aria-labelledby="track-labels-title">
        <div>
          <h3 id="track-labels-title">Track labels</h3>
          <p>Keep source and content cues visible without changing any track title.</p>
        </div>
        <label>
          <span class="settings-toggle"><Switch checked={showExternalLabel} disabled={controlsDisabled || !selectedFilter} onchange={event => void changePreference("showExternalLabel", event.currentTarget.checked)} aria-label="Show [A] for external tracks" /></span>
          <span><strong>Show [A] for external tracks</strong><small>Marks tracks supplied outside the native library.</small></span>
        </label>
        <label>
          <span class="settings-toggle"><Switch checked={showExplicitLabel} disabled={controlsDisabled || !selectedFilter} onchange={event => void changePreference("showExplicitLabel", event.currentTarget.checked)} aria-label="Show [E] for explicit tracks" /></span>
          <span><strong>Show [E] for explicit tracks</strong><small>Marks tracks identified as explicit.</small></span>
        </label>
      </section>

      <section class="preference-options" aria-labelledby="playback-start-title">
        <div>
          <h3 id="playback-start-title">Playback start</h3>
          <p>Fast start chooses quick lossless playback. Balanced favors the best quality when recent starts take 3 seconds or less. Best quality prefers the highest quality.</p>
        </div>
        <ThreeDetentSwitch label="Playback start" value={playbackStart} options={playbackStartOptions} compact disabled={controlsDisabled || !selectedFilter} onchange={value => void changePreference("playbackStart", value)} />
      </section>

      <section class="preference-options" aria-labelledby="quality-limit-title">
        <div class="preference-link-row">
          <div>
            <h3 id="quality-limit-title">Quality limit</h3>
            <p>The highest quality Allstarr fetches for you. You can choose less than your household's limit, not more. Songs already kept stay as they are.</p>
          </div>
          <SelectField value={qualityLimit} label="Quality limit" options={qualityChoices} disabled={controlsDisabled || !selectedFilter} onchange={value => void changePreference("qualityLimit", value)} />
        </div>
        {#if administrator}<Button variant="outline" href="#/settings/quality">Change the household limit</Button>{/if}
      </section>

    </div>

    <div class="panel preferences-panel">
      <section class="preference-options" aria-label="Lyrics source order">
        <div>
          <h3>Lyrics source order</h3>
          <p>Your saved choice for a song takes precedence. Otherwise, word timing wins over line timing, then plain text. This order breaks ties. Sources must be enabled and available to your account.</p>
        </div>
        {#if !lyricsOrder.length}<EmptyState title="No lyrics sources available" description="Available lyrics sources appear here when they are enabled for your account." />{:else}
        <ol class="lyrics-order-list">
          {#each lyricsOrder.slice(0, lyricsExpanded ? undefined : 6) as providerId, index (providerId)}
            <li draggable={!controlsDisabled && Boolean(selectedFilter)} ondragstart={event => { draggedLyrics = index; event.dataTransfer?.setData("text/plain", String(index)); }} ondragover={event => event.preventDefault()} ondrop={event => dropLyrics(event, index)} ondragend={() => draggedLyrics = -1}>
              <span class="lyrics-grip" aria-hidden="true"><GripVertical size={18} /></span>
              <strong>{lyricsName(providerId)}</strong>
              <span class="lyrics-order-actions">
                <Button variant="ghost" size="icon" aria-label={`Move ${lyricsName(providerId)} up`} disabled={controlsDisabled || !selectedFilter || index === 0} onclick={() => moveLyrics(index, -1)}><ArrowUp size={18} aria-hidden="true" /></Button>
                <Button variant="ghost" size="icon" aria-label={`Move ${lyricsName(providerId)} down`} disabled={controlsDisabled || !selectedFilter || index === lyricsOrder.length - 1} onclick={() => moveLyrics(index, 1)}><ArrowDown size={18} aria-hidden="true" /></Button>
              </span>
            </li>
          {/each}
        </ol>
        {#if lyricsOrder.length > 6}<Button variant="text" onclick={() => lyricsExpanded = !lyricsExpanded}>{lyricsExpanded ? "Show less" : `Show ${lyricsOrder.length - 6} more`}</Button>{/if}
        {/if}
      </section>

    </div>

    <div class="panel preferences-panel elsewhere-panel">
      <header class="panel-heading"><h3>Elsewhere</h3></header>
      <section class="preference-options" aria-labelledby="problem-alerts-title">
        <div class="preference-link-row">
          <div>
            <h3 id="problem-alerts-title">Alerts</h3>
            <p>Choose where Allstarr tells you about problems.</p>
          </div>
          <Button variant="secondary" href="#/settings/alerts">Manage alerts</Button>
        </div>
      </section>

    </div>

      <footer class="preferences-actions">
        <p>
          Household default: {snapshot.householdDefaults.explicitFilter === "ExplicitOnly" ? "Hide clean/edited versions" : snapshot.householdDefaults.explicitFilter === "CleanOnly" ? "Clean only" : snapshot.householdDefaults.explicitFilter}
          · [A] {snapshot.householdDefaults.showExternalLabel ? "on" : "off"}
          · [E] {snapshot.householdDefaults.showExplicitLabel ? "on" : "off"}
          · Playback: {playbackStartLabel(snapshot.householdDefaults.playbackStart)}
        </p>
        <div>
          <Button variant="outline" disabled={controlsDisabled || snapshot.usesHouseholdDefaults} onclick={() => void reset()}>
            Reset to household defaults
          </Button>

        </div>
      </footer>
  </section>
{/if}

<style>
  .preference-link-row { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: var(--space-3); }
  .preference-link-row > div { min-width: 0; max-width: 100%; }
  .preference-link-row :global(.select-root) { width: min(100%, 28rem); }
  .preference-link-row :global(.select-trigger > span) { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .preference-link-row p { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .preferences-view { display: grid; min-width: 0; gap: var(--section-gap); }
  .preference-options { display: grid; min-width: 0; grid-template-columns: minmax(0, 1fr); gap: var(--space-3); border-top: 1px solid var(--color-edge); padding: var(--surface-gutter); }
  .preference-options h3 { margin: 0 0 .3rem; font-size: 1rem; font-weight: 600; }
  .preference-options p { margin: 0; color: var(--color-ink-muted); line-height: 1.55; }
  .lyrics-order-list { counter-reset: lyrics-rank; display: grid; gap: var(--space-2); margin: 0; padding: 0; list-style: none; }
  .lyrics-order-list li { counter-increment: lyrics-rank; display: grid; grid-template-columns: 20px 1.5rem minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); border-bottom: 1px solid var(--color-edge); padding: var(--space-2) 0; }
  .lyrics-order-list strong { min-width: 0; overflow-wrap: anywhere; font-size: var(--text-sm); }
  .lyrics-order-list li::before { grid-column: 2; grid-row: 1; content: counter(lyrics-rank); color: var(--color-ink-muted); font-size: var(--text-sm); font-variant-numeric: tabular-nums; text-align: center; }
  .lyrics-order-list li:last-child { border-bottom: 0; }
  .lyrics-grip { grid-column: 1; grid-row: 1; color: var(--color-ink-muted); cursor: grab; }
  .lyrics-order-actions { display: flex; gap: var(--space-1); }
  .preferences-intro { padding-block: .2rem; }
  .preferences-intro h2 { margin: 0 0 .25rem; font-size: 1.375rem; line-height: 1.75rem; font-weight: 600; }
  .preferences-intro p:last-child, .preferences-heading p:last-child, .label-options p, .preferences-actions p {
    margin: 0; color: var(--color-ink-muted); line-height: 1.55;
  }
  .preferences-panel { min-width: 0; overflow: hidden; }
  .preferences-heading h3, .label-options h3 { margin: .15rem 0 .3rem; }
  .filter-options { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--space-3); margin: 0; border: 0; padding: var(--surface-gutter); }
  .filter-options legend { width: 100%; margin-bottom: var(--space-3); font-weight: 750; }
  .filter-options label { display: grid; min-width: 0; min-height: var(--control-lg); grid-template-columns: auto minmax(0, 1fr); align-items: start; gap: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); cursor: pointer; }
  .filter-options label.active { border-color: var(--color-signal); background: color-mix(in srgb, var(--color-signal) 8%, transparent); }
  .preferences-panel { border-radius: var(--radius-lg); }
  .preferences-heading h3, .label-options h3 { font-size: 1rem; font-weight: 600; }
  .filter-options input { width: 1.15rem; height: 1.15rem; margin-top: .1rem; accent-color: var(--color-signal); }
  .filter-options strong, .filter-options small, .label-options strong, .label-options small { display: block; overflow-wrap: anywhere; }
  .filter-options small, .label-options small { margin-top: .2rem; color: var(--color-ink-muted); line-height: 1.4; }
  .unknown-value { grid-column: 1 / -1; margin: 0; border-radius: var(--radius-md); background: var(--color-panel-raised); padding: var(--space-3); overflow-wrap: anywhere; }
  .label-options { display: grid; grid-template-columns: minmax(0, 1fr); align-items: start; gap: var(--space-4); border-top: 1px solid var(--color-edge); padding: var(--surface-gutter); }
  .label-options label { display: grid; min-width: 0; min-height: var(--control-md); grid-template-columns: auto minmax(0, 1fr); align-items: start; gap: var(--space-3); padding-block: .35rem; cursor: pointer; }
  .preferences-panel > .preference-options:first-child { border-top: 0; }
  .elsewhere-panel h3 { margin: 0; font-size: var(--text-md); font-weight: 600; }
  .preferences-actions { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: var(--space-4); padding-block: var(--space-2); }
  .preferences-actions > div { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: var(--space-3); }
  @media (max-width: 760px) {
    .filter-options, .label-options { grid-template-columns: minmax(0, 1fr); }
    .preferences-actions { grid-template-columns: minmax(0, 1fr); }
    .preferences-actions > div { display: grid; grid-template-columns: minmax(0, 1fr); }
    .preferences-actions :global([data-slot="button"]) { width: 100%; white-space: normal; }
  }
  @media (prefers-reduced-motion: reduce) {
    .filter-options label { transition: none; }
  }
</style>
