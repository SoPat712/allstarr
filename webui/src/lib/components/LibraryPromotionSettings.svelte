<script lang="ts">
  import { onMount } from "svelte";
  import type { PromotionSetup } from "$lib/library-promotion-api";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import { ApiError } from "$lib/api-client";
  import { promotionProfiles, type ApprovalRoles, type HouseholdPromotionProfile, type NamingError, type NamingPreview, type PersonalPromotionProfile, type PromotionProfile, type PromotionRoot, type PromotionFolders, type PromotionFolder } from "$lib/promotion-profile-api";
  import { createNamingPreview } from "$lib/promotion-preview";
  import { createProfileSave, type ProfileScope } from "$lib/promotion-profile-save";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import SelectField from "$lib/components/SelectField.svelte";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import PromotionNamingBuilder from "$lib/components/PromotionNamingBuilder.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";

  let { administrator }: { administrator: boolean } = $props();
  let setup = $state<PromotionSetup | null>(null), folderPath = $state(""), selectedFolder = $state<PromotionFolder | null>(null), discovery = $state<PromotionFolders | null>(null), folderBusy = $state(false), folderError = $state("");
  let mine = $state<PersonalPromotionProfile | null>(null), household = $state<HouseholdPromotionProfile | null>(null);
  let roots = $state<PromotionRoot[]>([]), scope = $state<ProfileScope>("me");
  let loading = $state(true), loadError = $state(""), previewError = $state(""), previewPending = $state(false);
  let preview = $state<NamingPreview | null>(null), errors = $state<NamingError[]>([]), scriptOpen = $state(false);
  let statuses = $state<Record<ProfileScope, string>>({ me: "Saved", household: "Saved" });
  let saveErrors = $state<Record<ProfileScope, string>>({ me: "", household: "" });
  let scriptInput = $state<HTMLTextAreaElement>();
  const profile = $derived(scope === "me" ? mine?.values : household?.values);
  const dirty = new Set<ProfileScope>(), validScripts = new Set<string>();
  const message = (cause: unknown) => cause instanceof Error ? cause.message : "Please try again.";
  const exampleLabel = (kind: string) => ({ album: "Album", "multi-disc": "Two discs", compilation: "Compilation", single: "Single" })[kind] ?? kind;
  const floorValue = $derived(profile ? `${profile.qualityFloor.codecFamily}:${profile.qualityFloor.minBitDepth}:${profile.qualityFloor.minSampleRate}` : "");
  const ceilingValue = $derived(profile ? `${profile.hiResCeiling.maxBitDepth}:${profile.hiResCeiling.maxSampleRate}` : "");
  const floorChoices = [
    { value: "lossless:16:44100", label: "Lossless 16/44" }, { value: "lossless:24:48000", label: "Lossless 24/48" },
    { value: "lossless:24:96000", label: "Lossless 24/96" }, { value: "any:0:44100", label: "Lossy or lossless" },
  ];
  const ceilingChoices = [{ value: "16:44100", label: "16-bit / 44.1 kHz" }, { value: "24:48000", label: "24-bit / 48 kHz" }, { value: "24:96000", label: "24-bit / 96 kHz" }, { value: "24:192000", label: "24-bit / 192 kHz" }];
  const floorOptions = $derived(floorChoices.some(item => item.value === floorValue) ? floorChoices : [...floorChoices, { value: floorValue, label: `Custom · ${floorValue.replaceAll(":", " / ")}` }]);
  const ceilingOptions = $derived((ceilingChoices.some(item => item.value === ceilingValue) ? ceilingChoices : [...ceilingChoices, { value: ceilingValue, label: `Custom · ${ceilingValue.replace(":", " / ")}` }]).map(item => {
    const [depth, rate] = item.value.split(":").map(Number);
    return { ...item, disabled: Boolean(profile && (depth < profile.qualityFloor.minBitDepth || rate < profile.qualityFloor.minSampleRate)) };
  }));
  const saves = createProfileSave<HouseholdPromotionProfile>({
    save: (target, value) => target === "me" ? promotionProfiles.saveMine(value.values) : promotionProfiles.saveHousehold(value),
    saved(target, value) {
      if (target === "me" && mine) mine = { ...mine, usesHouseholdDefault: false };
      if (target === "household" && mine) mine = { ...mine, householdDefault: value, ...(mine.usesHouseholdDefault && !dirty.has("me") ? { values: structuredClone(value.values) } : {}) };
      const current = target === "me" ? mine?.values : household?.values;
      if (JSON.stringify(current) === JSON.stringify(value.values) && (target === "me" || household?.addWithoutApproval === value.addWithoutApproval)) {
        dirty.delete(target); statuses[target] = "Saved"; saveErrors[target] = "";
      }
    },
    failed(target, cause) {
      statuses[target] = "Not saved"; saveErrors[target] = message(cause);
      if (cause instanceof ApiError && target === scope) {
        const details = cause.details as { errors?: NamingError[] } | null;
        if (details?.errors?.length) { errors = details.errors; scriptOpen = true; }
      }
    },
  });
  const previews = createNamingPreview({
    preview: promotionProfiles.preview,
    received(result) {
      previewPending = false; preview = result; errors = result.errors;
      if (errors.length) { scriptOpen = true; statuses[scope] = dirty.has(scope) ? "Waiting for a valid naming script" : statuses[scope]; }
      else { validScripts.add(result.script); if (dirty.has(scope)) scheduleSave(); }
    },
    failed(cause) { previewPending = false; previewError = message(cause); },
  });
  function scheduleSave() {
    if (!profile || !household || !validScripts.has(profile.namingScript)) return;
    statuses[scope] = "Saving…"; saveErrors[scope] = "";
    saves.change(scope, { values: structuredClone($state.snapshot(profile)), addWithoutApproval: household.addWithoutApproval });
  }
  function requestPreview() {
    if (!profile) return;
    preview = null; errors = []; previewError = ""; previewPending = true;
    previews.change(profile.namingScript);
  }
  function update(patch: Partial<PromotionProfile>) {
    if (!profile || !mine || !household) return;
    const next = { ...profile, ...patch };
    if (scope === "me") mine = { ...mine, values: next };
    else household = { ...household, values: next };
    dirty.add(scope);
    if (patch.namingScript !== undefined) {
      saves.discard(scope); statuses[scope] = "Checking naming script…"; requestPreview();
    } else if (validScripts.has(next.namingScript)) scheduleSave();
  }
  function chooseScope(next: string) {
    if (!administrator || next === scope) return;
    scope = next as ProfileScope; requestPreview();
  }
  function chooseFloor(value: string) {
    if (!profile) return;
    const [family, depth, rate] = value.split(":");
    const qualityFloor = { codecFamily: family as PromotionProfile["qualityFloor"]["codecFamily"], minBitDepth: Number(depth), minSampleRate: Number(rate) };
    update({ qualityFloor, allowLossy: family === "lossless" ? profile.allowLossy : true, hiResCeiling: { maxBitDepth: Math.max(profile.hiResCeiling.maxBitDepth, Number(depth)), maxSampleRate: Math.max(profile.hiResCeiling.maxSampleRate, Number(rate)) } });
  }
  function chooseCeiling(value: string) {
    const [maxBitDepth, maxSampleRate] = value.split(":").map(Number); update({ hiResCeiling: { maxBitDepth, maxSampleRate } });
  }
  function showError(error: NamingError) {
    scriptOpen = true;
    requestAnimationFrame(() => { scriptInput?.focus(); scriptInput?.setSelectionRange(error.position, error.position + Math.max(1, error.length)); });
  }
  async function approveFolder() {
    if (!folderPath.trim() || folderBusy || !selectedFolder || roots.some(root => !root.path)) return;
    folderBusy = true; folderError = "";
    try {
      const id = selectedFolder.rootId ?? crypto.randomUUID();
      await promotionProfiles.saveRoots([...roots.filter(root => root.id !== id).map(root => ({ ...root, path: root.path! })),
        { id, name: selectedFolder.name, path: folderPath.trim(), backendPath: selectedFolder.backendPath }]);
      roots = await promotionProfiles.roots(); setup = await promotionProfiles.setup(); discovery = await promotionProfiles.folders();
      selectedFolder = null;
      update({ libraryRootId: id });
    } catch (cause) { folderError = message(cause); }
    finally { folderBusy = false; }
  }
  async function load() {
    loading = true; loadError = "";
    try {
      const [personal, folders, shared, state, locations] = await Promise.all([promotionProfiles.mine(), promotionProfiles.roots(), administrator ? promotionProfiles.household() : Promise.resolve(null), promotionProfiles.setup(), administrator ? promotionProfiles.folders().catch(() => null) : Promise.resolve(null)]);
      setup = state; discovery = locations && Array.isArray(locations.folders) ? locations : null;
      mine = personal; roots = folders; household = shared ?? personal.householdDefault;
      validScripts.add(personal.values.namingScript); validScripts.add(household.values.namingScript);
      requestPreview();
    } catch (cause) { loadError = message(cause); }
    finally { loading = false; }
  }
  onMount(() => { void load(); return () => { previews.cancel(); void saves.flush(); }; });
</script>

{#snippet errorList(items: NamingError[])}
  <ul class="naming-errors" aria-label="Naming errors">{#each items as error}<li><button type="button" onclick={() => showError(error)}>Character {error.position + 1}: {error.message}</button>{#if profile}<code class="error-location">{profile.namingScript.slice(0, error.position)}<mark>{profile.namingScript.slice(error.position, error.position + error.length) || " "}</mark>{profile.namingScript.slice(error.position + error.length)}</code>{/if}</li>{/each}</ul>
{/snippet}
{#snippet toggle(key: "fakeLosslessCheck" | "musicBrainzTags" | "replayGain" | "afterAdding", title: string, description: string)}
  {#if profile}<div class="settings-row"><div><strong>{title}</strong><small>{description}</small></div><span class="settings-toggle"><Switch aria-label={title} checked={profile[key]} onchange={event => update({ [key]: event.currentTarget.checked })} /></span></div>{/if}
{/snippet}

{#if loading}<RowsSkeleton count={4} />
{:else if loadError}<ErrorState title="Library settings could not be loaded" detail={loadError} onretry={load} />
{:else if profile && mine && household}
  <div class="promotion-settings">
    <div class="settings-toolbar profile-toolbar">
      <p class="profile-context">{scope === "household" ? "Applies to listeners without their own profile." : mine.usesHouseholdDefault ? "Using the household default. Changes create your own profile." : "Your own process. Saved as you go."}</p>
      {#if administrator}<div class="profile-choice"><ThreeDetentSwitch label="Library profile" value={scope} options={[{ value: "me", label: "Your profile" }, { value: "household", label: "Household default" }]} onchange={chooseScope} /></div>{/if}
    </div>
    <div class="save-status" role="status" aria-live="polite">{statuses[scope]}</div>
    {#if saveErrors[scope]}<div class="save-error" role="alert"><p>{saveErrors[scope]}</p><Button variant="outline" onclick={scheduleSave}>Retry saving</Button></div>{/if}
    <section class="settings-card" aria-labelledby="naming-heading">
      <header><h3 id="naming-heading">File names and folders</h3><p>Drag fields to build the name, or edit the naming script directly (Picard syntax).</p></header>
      <div class="naming-content">
        <PromotionNamingBuilder script={profile.namingScript} onchange={namingScript => update({ namingScript })} />
        <div class="naming-examples" aria-label="Live naming examples" aria-busy={previewPending}>
          {#if previewPending}<p class="muted">Updating examples…</p>
          {:else if previewError}<div role="alert"><p>{previewError}</p><Button variant="outline" onclick={requestPreview}>Retry examples</Button></div>
          {:else if preview && !preview.errors.length && !preview.examples.length}<p class="muted">No library examples are available yet. Examples will appear when songs are in your library or kept.</p>
          {:else if preview}{#each preview.examples as example}<div class="naming-example"><span>{exampleLabel(example.kind)}</span><div><code class="example-path" title={example.title}>{example.relativePath ?? "Could not render this name"}</code>{#if example.errors.length}{@render errorList(example.errors)}{/if}</div></div>{/each}{/if}
        </div>
        <details bind:open={scriptOpen} class="script-disclosure"><summary>Naming script</summary>
          <label class="sr-only" for="promotion-script">Naming script</label><textarea bind:this={scriptInput} id="promotion-script" rows="4" value={profile.namingScript} aria-invalid={errors.length > 0} aria-describedby={errors.length ? "naming-validation" : "script-help"} spellcheck="false" oninput={event => update({ namingScript: event.currentTarget.value })}></textarea>
          <p id="script-help" class="muted">Use / between folders and include .%_extension% for the file extension. Escape literal punctuation with a backslash.</p>
          {#if errors.length}<div id="naming-validation" role="alert">{@render errorList(errors)}</div>{/if}
        </details>
      </div>
    </section>
    <div class="promotion-columns">
      <section class="settings-card" aria-labelledby="promotion-quality"><header><h3 id="promotion-quality">Quality</h3></header>
        <div class="settings-row"><div><strong>Lowest quality to add</strong><small>Copies below this are never added</small></div><SelectField label="Lowest quality to add" value={floorValue} options={floorOptions} onchange={chooseFloor} /></div>
        <div class="settings-row"><div><strong>Keep hi-res up to</strong><small>Higher copies are converted down</small></div><SelectField label="Keep hi-res up to" value={ceilingValue} options={ceilingOptions} onchange={chooseCeiling} /></div>
        {@render toggle("fakeLosslessCheck", "Check lossless copies are real", "Spectrum check for upscaled files")}
        <div class="settings-row"><div><strong>Allow lossy when nothing else exists</strong><small>Marked as lossy in tags and in Allstarr</small></div><span class="settings-toggle"><Switch aria-label="Allow lossy when nothing else exists" checked={profile.allowLossy} onchange={event => update({ allowLossy: event.currentTarget.checked, ...(!event.currentTarget.checked && profile.qualityFloor.codecFamily !== "lossless" ? { qualityFloor: { codecFamily: "lossless", minBitDepth: 16, minSampleRate: 44100 } } : {}) })} /></span></div>
      </section>
      <section class="settings-card" aria-labelledby="promotion-extras"><header><h3 id="promotion-extras">Tags and extras</h3></header>
        {@render toggle("musicBrainzTags", "MusicBrainz tags", "Release, recording and artist IDs, like Picard")}
        <div class="settings-row"><div><strong>Cover art</strong></div><SelectField label="Cover art" value={profile.coverMode} options={[{ value: "both", label: "cover.jpg and embedded" }, { value: "cover.jpg", label: "cover.jpg" }, { value: "embedded", label: "Embedded" }, { value: "none", label: "Off" }]} onchange={value => update({ coverMode: value as PromotionProfile["coverMode"] })} /></div>
        <div class="settings-row"><div><strong>Synced lyrics</strong><small>Only when the timing fits the file</small></div><SelectField label="Synced lyrics" value={profile.lyricsMode} options={[{ value: ".lrc", label: ".lrc next to the song" }, { value: "both", label: ".lrc and embedded" }, { value: "embedded", label: "Embedded" }, { value: "off", label: "Off" }]} onchange={value => update({ lyricsMode: value as PromotionProfile["lyricsMode"] })} /></div>
        {@render toggle("replayGain", "ReplayGain", "Track and album gain (rsgain)")}
      </section>
    </div>
    <section class="settings-card" aria-labelledby="promotion-where"><header><h3 id="promotion-where">Where and who</h3></header>
      {#if !roots.length}<div class="folder-setup"><EmptyState title="No library folder yet" description="Allstarr needs write access to the music folder Jellyfin reads.">
        {#snippet action()}{#if !administrator}<p class="muted">Ask an administrator to set it up.</p>{/if}{/snippet}
      </EmptyState></div>{/if}
      {#if administrator}<div class="folder-setup">
        {#if discovery?.folders.length}
          {#each discovery.folders as folder (folder.backendPath)}
            <div class="folder-option"><strong>{folder.name}</strong><code>{folder.backendPath}</code>
              <p class="muted">{folder.writable ? "Allstarr can add songs here" : "Allstarr can't reach this folder"}</p>
              {#if folder.mapped}<p class="muted">Path inside Allstarr: <code>{folder.path}</code></p>{/if}
              <Button variant="outline" onclick={() => { selectedFolder = folder; folderPath = folder.path; folderError = ""; }}>{folder.rootId ? "Edit folder mapping" : "Choose or map folder"}</Button>
            </div>
          {/each}
        {:else}<p class="muted">Jellyfin's music folders could not be read. Check the backend connection, then reload these settings.</p>{/if}
        {#if !discovery?.folders.some(folder => folder.writable)}<p class="muted">Mount the music folder in the Allstarr container once, for example <code>- /path/to/music:/music</code>, then map it here.</p>{/if}
        <a href="https://github.com/SoPat712/allstarr/blob/dev/docs/user-guide.md#adding-to-your-library" target="_blank" rel="noreferrer">Library setup guide</a>
        {#if selectedFolder}<form class="folder-form" onsubmit={event => { event.preventDefault(); void approveFolder(); }}>
          <label>Path inside Allstarr<input bind:value={folderPath} required placeholder="/music" /></label>
          <p class="muted">Must be an existing writable folder. {setup?.allowedBaseConfigured ? "The server also restricts it to the configured allowed base." : ""}</p>
          <Button type="submit" variant="outline" disabled={folderBusy || !folderPath.trim()}>Use this library folder</Button>
          {#if folderError}<p role="status" class="muted">{folderError}</p>{/if}
        </form>{/if}
      </div>{/if}
      {#if roots.length}
      <div class="settings-row"><div><strong>Library folder</strong><small>One of the folders Jellyfin reads</small></div><SelectField label="Library folder" value={profile.libraryRootId ?? ""} options={[{ value: "", label: "Default library folder" }, ...roots.map(root => ({ value: root.id, label: root.name })), ...(profile.libraryRootId && !roots.some(root => root.id === profile.libraryRootId) ? [{ value: profile.libraryRootId, label: "Previously selected folder" }] : [])]} onchange={value => update({ libraryRootId: value || null })} /></div>
      {/if}
      <div class="settings-row"><div><strong>Default unit</strong><small>What “Add to library” adds from a song</small></div><ThreeDetentSwitch label="Default unit" value={profile.defaultUnit} options={[{ value: "album", label: "Whole album" }, { value: "song", label: "Just the song" }]} onchange={value => update({ defaultUnit: value })} /></div>
      {@render toggle("afterAdding", "After adding", "Move playlists, mixes, hearts and plays to the library copy")}
      {#if administrator && scope === "household"}<div class="settings-row"><div><strong>Who adds without approval</strong><small>Everyone else sends a request to an admin</small></div><SelectField label="Who adds without approval" value={household.addWithoutApproval} options={[{ value: "admins-and-trusted-listeners", label: "Admins and trusted listeners" }, { value: "admins", label: "Admins" }]} onchange={value => { if (household) household = { ...household, addWithoutApproval: value as ApprovalRoles }; dirty.add(scope); scheduleSave(); }} /></div>{/if}
    </section>
  </div>
{/if}

<style>
  .folder-setup, .folder-form { display: grid; min-width: 0; gap: var(--space-3); padding: var(--space-4); }
  .folder-form { padding: 0; }
  .folder-option { display: grid; min-width: 0; gap: var(--space-2); }
  .folder-option code { overflow-wrap: anywhere; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .folder-form label { display: grid; gap: var(--space-2); color: var(--color-ink-muted); font-size: var(--text-sm); }
  .folder-form input { width: 100%; min-width: 0; padding: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-panel-raised); color: var(--color-ink); }
  .promotion-settings { display: grid; min-width: 0; gap: var(--space-4); }
  .profile-context, .save-status, .muted { margin: 0; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .profile-toolbar { align-items: start; }
  .profile-choice { min-width: 0; max-width: 100%; }
  .save-status { min-height: 1.2em; }
  .promotion-columns { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-4); }
  .promotion-columns :global(.settings-row) { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) !important; }
  .naming-content { display: grid; gap: var(--space-3); padding: var(--space-1) var(--space-4) var(--space-4); }
  header p { margin: var(--space-2) 0 0; font-size: var(--text-xs); color: var(--color-ink-muted); }
  header:has(p) { display: block; }
  .naming-examples { display: grid; gap: var(--space-2); min-width: 0; }
  .naming-example { display: grid; grid-template-columns: 6.5rem minmax(0, 1fr); align-items: baseline; gap: var(--space-3); font-size: var(--text-xs); }
  .naming-example > span { color: var(--color-ink-muted); }
  .example-path, textarea { display: block; width: 100%; min-width: 0; padding: var(--space-2) var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-panel-raised); color: var(--color-ink); font: 0.76rem/1.5 ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
  textarea { resize: vertical; }
  .script-disclosure { min-width: 0; font-size: var(--text-sm); }
  .script-disclosure summary { min-height: 44px; align-content: center; color: var(--color-ink-muted); cursor: pointer; }
  .script-disclosure p { margin-top: var(--space-2); }
  .naming-errors { margin: var(--space-2) 0; padding-left: var(--space-4); color: var(--danger); font-size: var(--text-xs); }
  .naming-errors button { border: 0; background: transparent; color: inherit; padding: var(--space-1) 0; text-align: left; text-decoration: underline; cursor: pointer; font: inherit; }
  .error-location { display: block; color: var(--color-ink-muted); white-space: pre-wrap; overflow-wrap: anywhere; }
  .error-location mark { background: var(--md-sys-color-error-container); color: var(--md-sys-color-on-error-container); text-decoration: underline wavy; }
  .save-error { color: var(--danger); font-size: var(--text-sm); }
  @media (max-width: 1100px) { .promotion-columns { grid-template-columns: minmax(0, 1fr); } }
  @media (max-width: 650px) {
    .promotion-columns :global(.settings-row) { grid-template-columns: minmax(0, 1fr) !important; }
    .naming-example { grid-template-columns: minmax(0, 1fr); gap: var(--space-1); }
    .profile-toolbar { display: grid; }
  }
</style>
