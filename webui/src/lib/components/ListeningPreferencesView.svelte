<script lang="ts">
  import { toast } from "$lib/toasts";
  import { onMount } from "svelte";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import {
    ApiError,
    listeningPreferences,
    type ExplicitFilter,
    type PlaybackStart,
    type PersonalListeningPreferences,
  } from "$lib/api";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import { audioQualityOptions, playbackStartLabel, playbackStartOptions } from "$lib/settings";
  import SelectField from "$lib/components/SelectField.svelte";

  // Settings passes the household's status so the page has one status line.
  let { administrator = false, householdStatus = [] }: { administrator?: boolean; householdStatus?: { label: string }[] } = $props();

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
  let loading = $state(true);
  let reloading = $state(false);
  let saving = $state(false);
  let writes = 0;
  let error = $state("");
  let conflict = $state(false);
  const controlsDisabled = $derived(reloading || saving || conflict);

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

  function apply(next: PersonalListeningPreferences) {
    snapshot = next;
    explicitFilter = next.cleanOnlyLocked ? "CleanOnly" : next.values.explicitFilter;
    showExternalLabel = next.values.showExternalLabel;
    showExplicitLabel = next.values.showExplicitLabel;
    playbackStart = next.values.playbackStart ?? "Balanced";
    qualityLimit = next.values.qualityLimit ?? "";
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

  const editing = () => saving || conflict || dirty;

  // Background refreshes stay silent: they never show "Saving…", lock the controls or replace a choice made meanwhile.
  async function load(passive = false) {
    if (passive && editing()) return;
    const startedAt = writes;
    loading = !snapshot;
    reloading = !passive && Boolean(snapshot);
    if (!passive) { error = ""; conflict = false; }
    try {
      const preferences = await listeningPreferences.get();
      if (passive && (writes !== startedAt || editing())) return;
      if (passive && snapshot && JSON.stringify(preferences) === JSON.stringify(snapshot)) return;
      apply(preferences);
    } catch (cause) {
      if (!passive) error = messageFor(cause, "Listening preferences could not be loaded.");
    } finally {
      loading = false;
      reloading = false;
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
    writes++;
    error = "";
    try {
      apply(await listeningPreferences.save({
        ...values,
        explicitFilter: values.explicitFilter as ExplicitFilter,
        ...(values.qualityLimit !== (snapshot.values.qualityLimit ?? "") ? { qualityLimit: values.qualityLimit } : { qualityLimit: undefined }),
        expectedRevision: snapshot.revision,
      }));
      toast.success("Listening preferences saved.", { label: "Undo", run: async () => {
        if (!snapshot || controlsDisabled) return;
        const restored = { ...valuesFor(snapshot), [field]: valuesFor(previous)[field] };
        if (previous.usesHouseholdDefaults && JSON.stringify(restored) === JSON.stringify(valuesFor(previous))) await reset();
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
    writes++;
    error = "";
    try {
      apply(await listeningPreferences.save({
        ...values, explicitFilter: values.explicitFilter as ExplicitFilter, expectedRevision: snapshot.revision,
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
    writes++;
    error = "";
    try {
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
    return () => { unsubscribe(); scheduler.cancel(); };
  });
</script>

{#if loading}
  <RowsSkeleton count={6} />
{:else if !snapshot}
  <ErrorState title="Listening preferences could not be loaded." detail={error} onretry={load} />
{:else}
  <section class="preferences-view" aria-busy={reloading || saving}>
    <PageStatus label="Listening preferences status" items={[
      ...householdStatus,
      { label: snapshot.usesHouseholdDefaults ? "Using household defaults" : "Personal choices" },
      { label: saving ? "Saving…" : reloading ? "Loading…" : conflict ? "Reload needed" : "Changes save automatically", tone: conflict ? "warn" : "neutral" },
    ]} />
    {#if error}
      <div class="degraded-banner" role="alert">
        <span aria-hidden="true">!</span>
        <p><strong>Preferences were not changed.</strong> {error}</p>
        {#if conflict}
          <Button variant="secondary" disabled={reloading || saving} onclick={() => void load()}>Reload latest</Button>
        {/if}
      </div>
    {/if}


    <section class="settings-card preferences-panel" aria-label="What you hear">
      <header>{#if administrator}<div class="settings-card-title"><h3>What you hear</h3><span class="scope-tag" data-scope="you">Just you</span></div>{:else}<h3>What you hear</h3>{/if}</header>
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
  .preferences-actions { display: flex; align-items: start; justify-content: space-between; gap: var(--space-3); }
  .preferences-actions p { margin: 0; font-size: var(--text-xs); }
  .unknown-value { padding: var(--space-3) var(--space-4); font-size: var(--text-sm); }
  .preference-link-row { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  .preference-link-row p { margin: var(--space-1) 0 0; color: var(--color-ink-muted); font-size: var(--text-sm); }
  @media (max-width: 900px) { .playback-row { grid-template-columns: minmax(0, 1fr) !important; } }
  @media (max-width: 599px) { .preferences-actions, .preference-link-row { flex-wrap: wrap; } }
</style>
