<script lang="ts">
  import { Lock } from "@lucide/svelte";
  import { settings, type ConfigField, type ConfigSection, type PlaybackStart } from "$lib/api";
  import { settingValue, splitLibraryIds, joinLibraryIds } from "$lib/settings-fields";
  import { fieldValue, playbackStartOptions } from "$lib/settings";
  import { rememberRestartUndo } from "$lib/settings-restart";
  import { toast } from "$lib/toasts";
  import { waitForRestart, savedBeforeRestart } from "$lib/restart";
  import { onDestroy, onMount, tick } from "svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Switch } from "$lib/components/ui/switch";
  import { Button } from "$lib/components/ui/button";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import FilterSegments from "$lib/components/patterns/FilterSegments.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import AudioQualityField from "$lib/components/AudioQualityField.svelte";

  let { section, config, onChanged = () => {} }: { section: ConfigSection; config: Record<string, unknown>; onChanged?: () => void | Promise<void> } = $props();
  let edits = $state<Record<string, { value: string; base: string }>>({});
  let solverTesting = $state(false);
  let libraries = $state<{ id: string; name: string }[] | null>(null);
  let libraryError = $state("");
  let librariesLoading = $state(false);
  let drafts = $state<Record<string, string>>({}), saved = $state<Record<string, boolean>>({});
  let busy = $state(false), error = $state(""), restartOpen = $state(false);
  let keyOpen = $state(false);
  let requestedQualities = $state<readonly string[]>([]);
  let pending = $state<{ field: ConfigField; value: string; previous: string; removing?: boolean } | null>(null);
  const abort = new AbortController();
  const id = $props.id();
  let qualityTimer: ReturnType<typeof setTimeout>;
  async function qualityChanged(field: ConfigField) {
    await tick();
    clearTimeout(qualityTimer);
    qualityTimer = setTimeout(() => {
      const next = document.getElementById(`${id}-${field.key}-quality`)?.querySelector<HTMLInputElement>('input[type="hidden"]')?.value;
      if (next) change(field, next);
    }, 350);
  }
  const names: Record<string, string> = {
    STORAGE_MODE: "Cache played songs", CACHE_DURATION_HOURS: "Keep cached songs for (hours)",
    BACKEND_MUSIC_LIBRARY_IDS: "Libraries to use", ODESLI_ENABLED: "Find the same song on other services",
    ODESLI_API_KEY: "song.link API key", ODESLI_RATE_LIMIT_MS: "Wait between song.link lookups (milliseconds)",
    EXTENSIONS_ALLOW_REMOTE_INSTALL: "Install extensions from outside your home network",
    PLAYBACK_LASTFM_SIMILAR_ENABLED: "Similar songs from Last.fm when a service has none",
    PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED: "Similar songs from ListenBrainz as a last resort",
    ADMIN_BIND_ANY_IP: "Admin page on all network interfaces", ADMIN_TRUSTED_SUBNETS: "Trusted admin networks",
    DEBUG_LOG_ALL_REQUESTS: "Log request usage", LIBRARY_DOWNLOAD_PATH: "Downloads", LIBRARY_KEPT_PATH: "Kept songs",
  };
  const label = (field: ConfigField) => names[field.key] ?? field.label;
  const locked = (field: ConfigField) => field.readOnly || field.ownership === "deployment";
  const toggle = (field: ConfigField) => ["toggle", "boolean"].includes(field.type);
  const original = (field: ConfigField) => settingValue(config, field);
  const value = (field: ConfigField) => drafts[field.key] ?? original(field);
  const help: Record<string, string> = {
    CLIENT_API_ENABLED: "Let signed-in music apps use song details, lyrics and mixes.",
    LISTENERS_CAN_CONNECT_OWN_ACCOUNTS: "Let listeners connect personal accounts. Existing connections stay usable.",
    DOWNLOAD_MODE: "Save just the song, or its whole album.",
    PLAYBACK_START_PREFERENCE: "Balanced favors quality when starts stay under 3 seconds.",
    PLAYBACK_PREPARE_UPCOMING_SONGS: "Prepare the next Apple song after 30 seconds of playback. Uses cache space.",
    PLAYBACK_LASTFM_SIMILAR_ENABLED: "Use a connected Last.fm account when a service has no suggestions.",
    PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED: "Use ListenBrainz when the service and Last.fm have no suggestions.",
    BACKEND_MUSIC_LIBRARY_IDS: "Choose libraries, or use every library your server allows.",
    ODESLI_ENABLED: "Find links to the same song across your enabled services.",
    ODESLI_API_KEY: "Optional. With a key, the official API is tried first. Leave unchanged to keep it.",
    STORAGE_MODE: "Songs saved after streaming start faster next time. Hearts, Keep and retained playlists protect their copies in either mode.",
    CACHE_DURATION_HOURS: "After the last play. The size limit can remove cached songs sooner.",
    MATCHING_LOCAL_PREFERENCE_PERCENT: "Only for matches below the automatic threshold. Never raises confidence.",
  };
  const displayValue = (field: ConfigField) => edits[field.key]?.value ?? value(field);
  const externallyChanged = $derived(section.fields.some(field => edits[field.key] && edits[field.key].base !== original(field)));
  function edit(field: ConfigField, next: string) {
    edits[field.key] = { value: next, base: edits[field.key]?.base ?? original(field) };
    saved[field.key] = false;
  }
  const options: Record<string, Record<string, string>> = {
    DOWNLOAD_MODE: { Track: "Just the song", Album: "Whole album" },
    EXPLICIT_FILTER: { All: "Show everything", ExplicitOnly: "Hide clean/edited versions", CleanOnly: "Clean only" },
    STORAGE_MODE: { Cache: "Cache played songs", Permanent: "Do not cache playback" },
  };
  const optionLabel = (field: ConfigField, next: string) => options[field.key]?.[next] ?? next;

  $effect(() => {
    if (!restartOpen && pending && !busy) {
      delete drafts[pending.field.key];
      pending = null;
    }
  });

  function change(field: ConfigField, next: string, force = false) {
    if (busy || locked(field)) return;
    if (externallyChanged) { toast.info("This setting changed elsewhere. Reload the saved values before changing it."); return; }
    delete edits[field.key];
    if (!force && next === value(field)) return;
    const previous = value(field);
    drafts[field.key] = next;
    saved[field.key] = false;
    if (field.requiresRestart || field.sensitive && force) { pending = { field, value: next, previous, removing: field.sensitive && force }; restartOpen = true; }
    else void persist(field, next, previous);
  }

  async function persist(field: ConfigField, next: string, previous: string) {
    if (busy) return;
    busy = true; error = "";
    try {
      let restarting = false;
      try { restarting = (await settings.save({ [field.key]: next })).requiresRestart === true; }
      catch (cause) { if (field.requiresRestart && savedBeforeRestart(cause)) restarting = true; else throw cause; }
      pending = null; restartOpen = false;
      saved[field.key] = true;
      if (restarting) {
        if (!field.sensitive) rememberRestartUndo({ kind: "config", key: field.key, value: previous, label: label(field) });
        toast.info("Saved. Allstarr is restarting…");
        await waitForRestart(abort.signal); window.location.reload(); return;
      }
      toast.success(`Saved · ${label(field)}`, field.sensitive ? undefined : { label: "Undo", run: () => change(field, previous) });
      await onChanged();
      delete drafts[field.key];
    } catch (cause) {
      if (!saved[field.key]) drafts[field.key] = previous;
      error = cause instanceof Error ? cause.message : "This setting could not be saved. Try again.";
      toast.error(error);
    } finally { busy = false; }
  }

  async function testSolver() {
    const field = section.fields.find(item => item.key === "CHALLENGE_SOLVER_URL");
    if (!field || solverTesting) return;
    solverTesting = true;
    try { const result = await settings.testChallengeSolver(displayValue(field)); (result.ok ? toast.success : toast.error)(result.message); }
    catch (cause) { toast.error(cause instanceof Error ? cause.message : "The solver could not be reached."); }
    finally { solverTesting = false; }
  }
  onMount(() => {
    if (!section.fields.some(field => field.key === "BACKEND_MUSIC_LIBRARY_IDS" && !locked(field))) return;
    librariesLoading = true;
    void settings.musicLibraries().then(items => { libraries = items; })
      .catch(cause => { libraryError = cause instanceof Error ? cause.message : "Music libraries could not be loaded."; })
      .finally(() => { librariesLoading = false; });
  });
  function selectLibrary(field: ConfigField, libraryId: string, checked: boolean) {
    const selected = splitLibraryIds(value(field));
    change(field, joinLibraryIds(checked ? [...selected, libraryId] : selected.filter(item => item !== libraryId)));
  }
  onDestroy(() => { abort.abort(); clearTimeout(qualityTimer); });
</script>

<form class="settings-card settings-disclosure" aria-label={`${section.label} settings`} onsubmit={event => event.preventDefault()}>
  <header><h3>{section.label}</h3></header>
  {#if externallyChanged}<div class="degraded-banner" role="status"><p><strong>Someone else changed these settings.</strong> Your unsaved edits are kept.</p><Button variant="outline" type="button" onclick={() => { edits = {}; drafts = {}; error = ""; }}>Reload server values</Button></div>{/if}
  <div class="settings-fields">
    {#each section.fields as field (field.key)}
      <div class="setting-field settings-row" class:wide-control={field.type === "audio-quality" || field.key === "PLAYBACK_START_PREFERENCE"} class:quality-row={field.type === "audio-quality"}>
        <div class:sr-only={field.type === "audio-quality"}>
          <label for={`${id}-${field.key}`}><strong>{label(field)}</strong></label>
          {#if !locked(field) && (help[field.key] || field.helpText && !field.helpText.startsWith("Saved in the data folder"))}<small>{help[field.key] || field.helpText}</small>{/if}
          {#if !locked(field) && field.requiresRestart}<small class="restart-note">· Restarts Allstarr</small>{/if}
          {#if saved[field.key]}<small class="setting-saved" role="status">✓ Saved</small>{/if}
        </div>
        {#if locked(field)}
          <div class="locked-setting"><output>{toggle(field) ? value(field) === "true" ? "On" : "Off" : field.sensitive ? fieldValue(config, field) ? "Key saved" : "Not set" : optionLabel(field, value(field)) || "Not set"}</output><small><Lock size={12} aria-hidden="true" />Set in your deployment</small></div>
        {:else if field.key === "ODESLI_API_KEY" && !keyOpen}
          <Button variant="outline" disabled={busy} onclick={() => keyOpen = true}>{fieldValue(config, field) ? "Change key…" : "Add key…"}</Button>
        {:else if field.key === "BACKEND_MUSIC_LIBRARY_IDS" && libraries !== null}
          <fieldset id={`${id}-${field.key}`} class="library-choices quality-control" aria-label={label(field)} disabled={busy}>
            <label><Checkbox aria-label="All libraries" checked={!splitLibraryIds(value(field)).length} onCheckedChange={checked => change(field, checked ? "" : joinLibraryIds(libraries?.map(item => item.id) ?? []))} /><span>All libraries</span></label>
            {#each libraries as library (library.id)}
              <label><Checkbox aria-label={library.name} checked={splitLibraryIds(value(field)).includes(library.id)} onCheckedChange={checked => selectLibrary(field, library.id, checked)} /><span>{library.name}</span></label>
            {/each}
            {#each splitLibraryIds(value(field)).filter(selected => !libraries?.some(item => item.id === selected)) as missingId (missingId)}
              <label><Checkbox aria-label={`Unavailable library (${missingId})`} checked onCheckedChange={checked => selectLibrary(field, missingId, checked)} /><span>Unavailable library ({missingId})</span></label>
            {/each}
            {#if !libraries.length}<small>No music libraries are available to this account.</small>{/if}
          </fieldset>
        {:else if field.key === "BACKEND_MUSIC_LIBRARY_IDS" && librariesLoading}
          <p role="status">Loading music libraries…</p>
        {:else if field.type === "audio-quality"}
          <fieldset disabled={busy} id={`${id}-${field.key}-quality`} class="quality-control">
            <AudioQualityField name={field.key} value={value(field)} showDetails={false} bind:requestedQualities onchange={() => void qualityChanged(field)} />
          </fieldset>
        {:else if field.key === "MATCHING_LOCAL_PREFERENCE_PERCENT"}
          <div class="matching-preference"><span class="settings-toggle"><Switch aria-label={label(field)} checked={Number(value(field)) > 0} disabled={busy} onchange={event => change(field, event.currentTarget.checked ? "7" : "0")} /></span><details><summary>Preference window</summary><input type="number" aria-label="Preference window (points)" value={value(field)} min={field.min ?? 0} max={field.max ?? 20} disabled={busy} onchange={event => { if (event.currentTarget.reportValidity()) change(field, event.currentTarget.value); }} /></details></div>
        {:else if field.key === "PLAYBACK_START_PREFERENCE"}
          <ThreeDetentSwitch name={field.key} label={label(field)} value={(value(field) || "Balanced") as PlaybackStart} options={playbackStartOptions} disabled={busy} onchange={next => change(field, next)} />
        {:else if field.key === "STORAGE_MODE"}
          <span class="settings-toggle"><Switch aria-label={label(field)} checked={value(field) === "Cache"} disabled={busy} onchange={event => change(field, event.currentTarget.checked ? "Cache" : "Permanent")} /></span>
        {:else if toggle(field)}
          <span class="settings-toggle"><Switch id={`${id}-${field.key}`} aria-label={label(field)} checked={value(field) === "true"} disabled={busy} onchange={event => change(field, String(event.currentTarget.checked))} /></span>
        {:else if field.key === "DOWNLOAD_MODE"}
          <fieldset class="quality-control" disabled={busy}><FilterSegments label={label(field)} value={value(field)} options={(field.options ?? []).map(next => ({ id: next, label: optionLabel(field, next) }))} onchange={next => change(field, next)} /></fieldset>
        {:else if field.type === "select"}
          <SelectField name={field.key} label={label(field)} value={value(field)} options={(field.options ?? []).map(next => ({ value: next, label: optionLabel(field, next) }))} disabled={busy} onchange={next => change(field, next)} />
        {:else}
          <div class:library-fallback={field.key === "BACKEND_MUSIC_LIBRARY_IDS"}>
            {#if field.key === "BACKEND_MUSIC_LIBRARY_IDS" && libraryError}<small role="status">{libraryError}</small><small>Enter library IDs separated by commas. Leave empty for all libraries.</small>{/if}
            <div class="setting-input">
              <input id={`${id}-${field.key}`} name={field.key} aria-label={label(field)} type={field.sensitive ? "password" : field.type === "number" ? "number" : "text"} value={displayValue(field)} min={field.min ?? undefined} max={field.max ?? undefined} required={field.required} placeholder={field.sensitive ? "Leave unchanged to keep the saved key" : field.placeholder ?? undefined} autocomplete={field.sensitive ? "new-password" : undefined} disabled={busy} oninput={event => edit(field, event.currentTarget.value)} onchange={event => { if (event.currentTarget.reportValidity()) change(field, event.currentTarget.value); }} />
              {#if field.sensitive && fieldValue(config, field)}<Button variant="text" class="settings-danger" type="button" disabled={busy} onclick={() => change(field, "", true)}>Remove key…</Button>{/if}
              {#if field.key === "CHALLENGE_SOLVER_URL"}<Button variant="outline" type="button" disabled={solverTesting} onclick={testSolver}>{solverTesting ? "Testing…" : "Test"}</Button>{/if}
            </div>
          </div>
        {/if}
      </div>
    {/each}
  </div>
  {#if section.fields.some(field => field.type === "audio-quality")}<details class="settings-fold"><summary>Exact quality asked from each service</summary><ul class="requested-qualities">{#each requestedQualities as quality}<li>{quality}</li>{/each}</ul></details>{/if}
  {#if error}<p role="alert" class="settings-impact notice-error">{error}</p>{/if}
</form>

<ConfirmDialog bind:open={restartOpen} title={pending?.removing ? pending.field.requiresRestart ? "Remove the key and restart Allstarr?" : "Remove the saved key?" : "Restart Allstarr to apply?"} description={pending?.removing && !pending.field.requiresRestart ? "The saved key is removed. You will need to enter it again to use it." : `Changing ${pending ? label(pending.field).toLowerCase() : "this setting"} needs a restart. Music that is playing will pause briefly. This page reconnects when Allstarr is ready.`} confirmLabel={busy ? "Applying…" : pending?.removing ? pending.field.requiresRestart ? "Remove key and restart" : "Remove key" : "Restart and apply"} confirmVariant={pending?.removing ? "destructive" : "default"} disabled={busy} closeOnConfirm={false} {error} onConfirm={() => pending ? persist(pending.field, pending.value, pending.previous) : undefined} />

<style>
  .wide-control.quality-row { grid-template-columns: minmax(0, 1fr) !important; }
  .requested-qualities { margin: 0; padding: var(--space-3) var(--space-6); color: var(--color-ink-muted); font-size: var(--text-sm); }

  .restart-note { color: var(--on-warning-container); }
  .library-choices, .library-fallback { display: grid; gap: var(--space-2); min-width: 0; }
  .library-choices label { display: flex; align-items: center; gap: var(--space-2); min-height: 44px; }
  .library-choices span { overflow-wrap: anywhere; }
  .matching-preference { min-width: 0; }
  .matching-preference summary { min-height: 44px; padding-block: var(--space-3); cursor: pointer; font-size: var(--text-xs); color: var(--color-ink-muted); }
  .quality-control { min-width: 0; margin: 0; padding: 0; border: 0; }
  .setting-input { display: flex; min-width: 0; gap: var(--space-2); align-items: center; }
  .setting-input input { min-width: 0; flex: 1; }
  .locked-setting { display: grid; justify-items: end; gap: var(--space-1); text-align: right; }
  .locked-setting small { display: flex; align-items: center; gap: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .setting-saved { color: var(--success) !important; }
  .wide-control { grid-template-columns: minmax(0, 1fr) minmax(0, 25rem) !important; }
  @media (max-width: 650px) { .wide-control { grid-template-columns: minmax(0, 1fr) !important; } .locked-setting { justify-items: start; text-align: left; } }
</style>
