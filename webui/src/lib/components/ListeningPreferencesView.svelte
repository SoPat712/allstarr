<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { ArrowDown, ArrowUp } from "@lucide/svelte";
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
  import RouteError from "$lib/components/RouteError.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import { canonicalProviderId, lyricsSourceDisplayName } from "$lib/sources";
  import { keptLyricsFirst, move, playbackStartLabel, playbackStartOptions } from "$lib/settings";

  const filterOptions: Array<{ value: ExplicitFilter; label: string; description: string }> = [
    { value: "All", label: "All", description: "Show clean and explicit releases." },
    { value: "ExplicitOnly", label: "Hide clean/edited versions", description: "Keep naturally clean, explicit, and unrated releases." },
    { value: "CleanOnly", label: "Clean only", description: "Hide releases marked explicit." },
  ];

  let snapshot = $state<PersonalListeningPreferences | null>(null);
  let explicitFilter = $state("");
  let showExternalLabel = $state(false);
  let showExplicitLabel = $state(false);
  let playbackStart = $state<PlaybackStart>("Balanced");
  let providerDefinitions = $state<ProviderDefinition[]>([]);
  let lyricsOrder = $state<string[]>([]);
  let initialLyricsOrder = $state("");
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let conflict = $state(false);

  const selectedFilter = $derived(
    filterOptions.find((option) => option.value === explicitFilter)?.value ?? null,
  );
  const dirty = $derived(Boolean(snapshot) && (
    explicitFilter !== snapshot?.values.explicitFilter ||
    showExternalLabel !== snapshot?.values.showExternalLabel ||
    showExplicitLabel !== snapshot?.values.showExplicitLabel ||
    playbackStart !== (snapshot?.values.playbackStart ?? "Balanced") ||
    lyricsOrder.join(",") !== initialLyricsOrder
  ));

  function completeLyricsOrder(value?: string | null) {
    const candidates = [...(value ?? "").split(","), "backend", "kept", ...providerDefinitions
      .filter(provider => provider.categories?.includes("lyrics")).map(provider => provider.id).sort()];
    const included = new Set<string>();
    return keptLyricsFirst(candidates.map(id => id.trim()).filter(id => {
      const canonical = canonicalProviderId(id);
      if (!id || included.has(canonical)) return false;
      included.add(canonical);
      return true;
    }));
  }

  function moveLyrics(index: number, direction: -1 | 1) {
    if (lyricsOrder[index] === "kept" || lyricsOrder[index + direction] === "kept") return;
    lyricsOrder = move(lyricsOrder, index, direction);
  }

  const lyricsName = (id: string) => lyricsSourceDisplayName(providerDefinitions, id);

  function apply(next: PersonalListeningPreferences) {
    snapshot = next;
    explicitFilter = next.values.explicitFilter;
    showExternalLabel = next.values.showExternalLabel;
    showExplicitLabel = next.values.showExplicitLabel;
    playbackStart = next.values.playbackStart ?? "Balanced";
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

  async function load() {
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

  async function save() {
    if (!snapshot || !selectedFilter || busy) return;
    busy = true;
    error = "";
    conflict = false;
    try {
      apply(await listeningPreferences.save({
        explicitFilter: selectedFilter,
        showExternalLabel,
        showExplicitLabel,
        playbackStart,
        ...(lyricsOrder.join(",") !== initialLyricsOrder ? { lyricsOrder: lyricsOrder.join(",") } : {}),
        expectedRevision: snapshot.revision,
      }));
      toast.success("Listening preferences saved.");
    } catch (cause) {
      error = messageFor(cause, "Listening preferences could not be saved.");
    } finally {
      busy = false;
    }
  }

  async function reset() {
    if (!snapshot || busy) return;
    busy = true;
    error = "";
    conflict = false;
    try {
      apply(await listeningPreferences.reset(snapshot.revision));
      toast.success("Household defaults restored.");
    } catch (cause) {
      error = messageFor(cause, "Household defaults could not be restored.");
    } finally {
      busy = false;
    }
  }

  onMount(() => { void load(); });
</script>

{#if loading}
  <Skeleton class="panel preferences-loading" aria-label="Loading listening preferences" aria-busy="true" />
{:else if !snapshot}
  <RouteError
    eyebrow="Preferences unavailable"
    title="Listening preferences could not be loaded."
    message={error}
    onRetry={load}
  />
{:else}
  <section class="preferences-view" aria-busy={busy}>
    <header class="preferences-intro">

      <h2>Tune your listening experience</h2>
      <p>
        These choices affect only your listener account. Native library titles remain untouched.
      </p>
    </header>

    {#if error}
      <div class="degraded-banner" role="alert">
        <span aria-hidden="true">!</span>
        <p><strong>Preferences were not changed.</strong> {error}</p>
        {#if conflict}
          <Button variant="secondary" disabled={busy} onclick={() => void load()}>Reload latest</Button>
        {/if}
      </div>
    {/if}


    <form class="panel preferences-panel" onsubmit={(event) => { event.preventDefault(); void save(); }}>
      <header class="panel-heading preferences-heading">
        <div>

          <h3>Explicit releases</h3>
          <p>Control which release versions appear in your Allstarr results. Tracks with unknown explicit status always remain visible.</p>
        </div>
        <span class="scope-status">
          {snapshot.usesHouseholdDefaults ? "Using household defaults" : "Personal choices"}
        </span>
      </header>

      <fieldset class="filter-options" disabled={busy}>
        <legend>Release filter</legend>
        {#each filterOptions as option}
          <label class:active={explicitFilter === option.value}>
            <input bind:group={explicitFilter} type="radio" name="explicit-filter" value={option.value} />
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
          <span class="settings-toggle"><Checkbox bind:checked={showExternalLabel} disabled={busy} aria-label="Show [A] for external tracks" /></span>
          <span><strong>Show [A] for external tracks</strong><small>Marks tracks supplied outside the native library.</small></span>
        </label>
        <label>
          <span class="settings-toggle"><Checkbox bind:checked={showExplicitLabel} disabled={busy} aria-label="Show [E] for explicit tracks" /></span>
          <span><strong>Show [E] for explicit tracks</strong><small>Marks tracks identified as explicit.</small></span>
        </label>
      </section>

      <section class="preference-options" aria-labelledby="playback-start-title">
        <div>
          <h3 id="playback-start-title">Playback start</h3>
          <p>Fast start chooses quick lossless playback. Balanced favors the best quality when recent starts take 3 seconds or less. Best quality prefers the highest quality.</p>
        </div>
        <ThreeDetentSwitch label="Playback start" value={playbackStart} options={playbackStartOptions} compact disabled={busy} onchange={value => playbackStart = value} />
      </section>

      <section class="preference-options" aria-label="Lyrics source order">
        <div>
          <h3>Lyrics source order</h3>
          <p>Your saved choice for a song takes precedence, then Kept LRC. Other sources follow this order, with a short priority window for slower services. Sources must be enabled and available to your account.</p>
        </div>
        <ol class="lyrics-order-list">
          {#each lyricsOrder as providerId, index (providerId)}
            <li>
              <strong>{lyricsName(providerId)}</strong>
              <span class="lyrics-order-actions">
                <Button variant="ghost" size="icon" aria-label={`Move ${lyricsName(providerId)} up`} disabled={busy || providerId === "kept" || index === 0 || lyricsOrder[index - 1] === "kept"} onclick={() => moveLyrics(index, -1)}><ArrowUp size={18} aria-hidden="true" /></Button>
                <Button variant="ghost" size="icon" aria-label={`Move ${lyricsName(providerId)} down`} disabled={busy || providerId === "kept" || index === lyricsOrder.length - 1} onclick={() => moveLyrics(index, 1)}><ArrowDown size={18} aria-hidden="true" /></Button>
              </span>
            </li>
          {/each}
        </ol>
      </section>

      <footer class="preferences-actions">
        <p>
          Household default: {snapshot.householdDefaults.explicitFilter === "ExplicitOnly" ? "Hide clean/edited versions" : snapshot.householdDefaults.explicitFilter === "CleanOnly" ? "Clean only" : snapshot.householdDefaults.explicitFilter}
          · [A] {snapshot.householdDefaults.showExternalLabel ? "on" : "off"}
          · [E] {snapshot.householdDefaults.showExplicitLabel ? "on" : "off"}
          · Playback: {playbackStartLabel(snapshot.householdDefaults.playbackStart)}
        </p>
        <div>
          <Button variant="outline" disabled={busy || conflict || snapshot.usesHouseholdDefaults} onclick={() => void reset()}>
            Reset to household defaults
          </Button>
          <Button type="submit" disabled={busy || conflict || !dirty || !selectedFilter}>
            {busy ? "Saving…" : "Save preferences"}
          </Button>
        </div>
      </footer>
    </form>
  </section>
{/if}

<style>
  .preferences-view { display: grid; min-width: 0; gap: var(--section-gap); }
  .preference-options { display: grid; gap: var(--space-3); border-top: 1px solid var(--color-edge); padding: var(--surface-gutter); }
  .preference-options h3 { margin: 0 0 .3rem; font-size: 1rem; font-weight: 600; }
  .preference-options p { margin: 0; color: var(--color-ink-muted); line-height: 1.55; }
  .lyrics-order-list { display: grid; gap: var(--space-2); margin: 0; padding: 0; list-style: none; }
  .lyrics-order-list li { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-2) var(--space-3); }
  .lyrics-order-list strong { min-width: 0; overflow-wrap: anywhere; font-size: var(--text-sm); }
  .lyrics-order-actions { display: flex; gap: var(--space-1); }
  :global(.preferences-loading) { min-height: 24rem; }
  .preferences-intro { padding-block: .2rem; }
  .preferences-intro h2 { margin: 0 0 .25rem; font-size: 1.375rem; line-height: 1.75rem; font-weight: 600; }
  .preferences-intro p:last-child, .preferences-heading p:last-child, .label-options p, .preferences-actions p {
    margin: 0; color: var(--color-ink-muted); line-height: 1.55;
  }
  .preferences-panel { min-width: 0; overflow: hidden; }
  .preferences-heading h3, .label-options h3 { margin: .15rem 0 .3rem; }
  .scope-status { border-radius: 999px; background: var(--color-panel-raised); padding: .45rem .75rem; color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 700; }
  .filter-options { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--space-3); margin: 0; border: 0; padding: var(--surface-gutter); }
  .filter-options legend { width: 100%; margin-bottom: var(--space-3); font-weight: 750; }
  .filter-options label { display: grid; min-width: 0; min-height: 5rem; grid-template-columns: auto minmax(0, 1fr); align-items: start; gap: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); padding: var(--space-3); cursor: pointer; }
  .filter-options label.active { border-color: var(--color-signal); background: color-mix(in srgb, var(--color-signal) 8%, transparent); }
  .preferences-panel { border-radius: var(--radius-lg); }
  .preferences-heading h3, .label-options h3 { font-size: 1rem; font-weight: 600; }
  .filter-options input { width: 1.15rem; height: 1.15rem; margin-top: .1rem; accent-color: var(--color-signal); }
  .filter-options strong, .filter-options small, .label-options strong, .label-options small { display: block; overflow-wrap: anywhere; }
  .filter-options small, .label-options small { margin-top: .2rem; color: var(--color-ink-muted); line-height: 1.4; }
  .unknown-value { grid-column: 1 / -1; margin: 0; border-radius: var(--radius-md); background: var(--color-panel-raised); padding: var(--space-3); overflow-wrap: anywhere; }
  .label-options { display: grid; grid-template-columns: minmax(0, 1fr) repeat(2, minmax(13rem, .8fr)); align-items: start; gap: var(--space-4); border-top: 1px solid var(--color-edge); padding: var(--surface-gutter); }
  .label-options label { display: grid; min-width: 0; min-height: var(--control-md); grid-template-columns: auto minmax(0, 1fr); align-items: start; gap: var(--space-3); padding-block: .35rem; cursor: pointer; }
  .label-options :global([data-slot="checkbox"]) { margin-top: .1rem; }
  .preferences-actions { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: var(--space-4); border-top: 1px solid var(--color-edge); padding: var(--surface-gutter); }
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
