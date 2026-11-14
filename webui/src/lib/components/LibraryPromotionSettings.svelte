<script lang="ts">
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";
  import { onMount } from "svelte";
  import type { PromotionSetup } from "$lib/library-promotion-api";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import { ApiError } from "$lib/api-client";
  import { promotionProfiles, type ApprovalRoles, type HouseholdPromotionProfile, type NamingDetection, type NamingError, type NamingPreview, type PersonalPromotionProfile, type PromotionProfile, type PromotionRoot, type PromotionFolders, type PromotionFolder } from "$lib/promotion-profile-api";
  import { createNamingPreview } from "$lib/promotion-preview";
  import { createProfileSave, type ProfileScope } from "$lib/promotion-profile-save";
  import { loadPromotionSettings } from "$lib/promotion-settings";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import SelectField from "$lib/components/SelectField.svelte";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import PromotionNamingBuilder from "$lib/components/PromotionNamingBuilder.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import { formatDownloadSize } from "$lib/downloads";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";

  // inline: shown inside Settings › Library, where the page already explains what these are for.
  let { administrator, inline = false }: { administrator: boolean; inline?: boolean } = $props();
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
    previewError = ""; previewPending = true;
    previews.change(profile.namingScript);
  }
  function update(patch: Partial<PromotionProfile>) {
    if (!profile || !mine || !household) return;
    const current = profile;
    if (Object.entries(patch).every(([key, value]) => JSON.stringify(current[key as keyof PromotionProfile]) === JSON.stringify(value))) return;
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
  // The folder added songs go to: the profile's own choice, else the one setup picked for this listener.
  const chosenRootId = $derived(profile?.libraryRootId ?? (scope === "me" ? mine?.householdDefault.values.libraryRootId ?? setup?.rootId : null) ?? null);
  type FolderRow = { key: string; name: string; backendPath: string; path: string; mapped: boolean; freeBytes: number | null;
    rootId: string | null; folder: PromotionFolder | null; chosen: boolean; state: string; tone: "ok" | "warn" | "neutral"; action: "choose" | "link" | "map" | null };
  // One list of every folder Jellyfin reads, plus any folder set up by hand, so choosing and linking happen in the same place.
  const folderRows = $derived.by<FolderRow[]>(() => {
    const listed = (discovery?.folders ?? []).map(folder => {
      const chosen = folder.rootId != null && folder.rootId === chosenRootId;
      const blocked = !folder.writable ? ({ "not-writable": "Read-only", "outside-allowed-base": "Not allowed" } as Record<string, string>)[folder.status] ?? "Can't reach" : null;
      return { key: folder.backendPath, name: folder.name, backendPath: folder.backendPath, path: folder.path, mapped: folder.mapped,
        freeBytes: folder.freeBytes ?? null, rootId: folder.rootId, folder, chosen,
        state: blocked ?? (folder.status === "almost-full" ? "Almost full" : chosen ? "Adds go here" : "Available"),
        tone: blocked || folder.status === "almost-full" ? "warn" : chosen ? "ok" : "neutral",
        action: !folder.writable ? administrator ? "map" : null : chosen ? null : folder.rootId ? "choose" : administrator ? "link" : null } satisfies FolderRow;
    });
    const extra = roots.filter(root => !listed.some(row => row.rootId === root.id)).map(root => {
      const chosen = root.id === chosenRootId;
      return { key: root.id, name: root.name, backendPath: root.backendPath ?? root.path ?? "", path: root.path ?? "", mapped: false, freeBytes: null,
        rootId: root.id, folder: null, chosen, state: chosen ? "Adds go here" : "Available", tone: chosen ? "ok" : "neutral", action: chosen ? null : "choose" } satisfies FolderRow;
    });
    return [...listed, ...extra];
  });
  const usableFolder = $derived(folderRows.some(row => row.rootId || row.folder?.writable));
  function useFolder(row: FolderRow) {
    folderError = "";
    if (row.action === "choose" && row.rootId) update({ libraryRootId: row.rootId });
    else if (row.folder) { selectedFolder = row.folder; folderPath = row.folder.path; if (row.action === "link") void approveFolder(); }
  }

  // How the chosen folder already names its files, offered as a one-tap naming script.
  let detected = $state<NamingDetection | null>(null);
  $effect(() => {
    if (loading || !profile) return;
    const rootId = chosenRootId;
    const abort = new AbortController();
    promotionProfiles.detectNaming(rootId, abort.signal).then(result => { if (!abort.signal.aborted) detected = result; },
      () => { if (!abort.signal.aborted) detected = null; });
    return () => abort.abort();
  });
  const detectedExample = $derived(detected?.examples[0]?.path ?? null);

  async function approveFolder() {
    if (!folderPath.trim() || folderBusy || !selectedFolder || roots.some(root => !root.path)) return;
    folderBusy = true; folderError = "";
    try {
      const id = selectedFolder.rootId ?? crypto.randomUUID();
      await promotionProfiles.saveRoots([...roots.filter(root => root.id !== id).map(root => ({ ...root, path: root.path! })),
        { id, name: selectedFolder.name, path: folderPath.trim(), backendPath: selectedFolder.backendPath }]);
      // All three change together, so the list never shows the new folder twice.
      [roots, setup, discovery] = await Promise.all([promotionProfiles.roots(), promotionProfiles.setup(), promotionProfiles.folders()]);
      selectedFolder = null;
      update({ libraryRootId: id });
    } catch (cause) { folderError = message(cause); }
    finally { folderBusy = false; }
  }
  async function load() {
    loading = true; loadError = "";
    try {
      const [personal, folders, shared, state, locations] = await loadPromotionSettings(administrator);
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
{#snippet toggle(key: "fakeLosslessCheck" | "musicBrainzTags" | "replayGain" | "removeKeptCopy", title: string, description: string)}
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
    <section class="settings-card" aria-labelledby="promotion-where"><header><h3 id="promotion-where">Where added songs go</h3>{#if !inline}<p>One of the folders Jellyfin reads.</p>{/if}</header>
      {#if !usableFolder}<div class="folder-setup"><EmptyState title="No library folder yet" description="Allstarr needs write access to the music folder Jellyfin reads.">
        {#snippet action()}{#if !administrator}<p class="muted">Ask an administrator to set it up.</p>{/if}{/snippet}
      </EmptyState></div>{/if}
      {#if folderRows.length || administrator}<div class="folder-setup">
        {#if !chosenRootId && folderRows.some(row => row.action === "choose" || row.action === "link")}<p class="folder-hint" role="status"><strong>No destination selected.</strong> Choose Add songs here on the folder you want to use. Available folders are not selected destinations.</p>{/if}
        {#if folderRows.length}<ul class="folder-list" aria-label="Library folders">
        {#each folderRows as row (row.key)}
          <li class="folder-row" class:chosen={row.chosen}><div><strong>{row.name}</strong><code title={row.backendPath}>{row.backendPath}</code>
            {#if row.mapped}<small>Allstarr writes to <code title={row.path}>{row.path}</code></small>{/if}
            {#if row.freeBytes != null}<small>{formatDownloadSize(row.freeBytes)} free</small>{/if}</div>
            <StatePill compact tone={row.tone} text={row.state} />
            {#if row.action}<Button variant="outline" size="sm" aria-label={`${row.action === "map" ? "Set the path for" : "Add songs to"} ${row.name}`} disabled={folderBusy}
              onclick={() => useFolder(row)}>{row.action === "map" ? "Set path" : "Add songs here"}</Button>{:else}<span></span>{/if}
          </li>
        {/each}
        </ul>{/if}
        {#if profile.libraryRootId}<button type="button" class="folder-reset" onclick={() => update({ libraryRootId: null })}>{scope === "household" ? "Pick each listener's own library automatically" : "Use the household's folder"}</button>{/if}
        {#if administrator && discovery?.folders.length && !usableFolder}{@const example = discovery.folders[0]?.backendPath ?? "/path/to/music"}<p class="muted">Mount the music folder into the Allstarr container at the same path Jellyfin uses, for example <code>- {example}:{example}</code>, then choose Add songs here.</p>{/if}
        {#if administrator && !discovery?.folders.length && !usableFolder}<p class="muted">Jellyfin's music folders could not be read. Check the backend connection, then reload these settings.</p>{/if}
        {#if selectedFolder && !selectedFolder.writable}<form class="folder-form" onsubmit={event => { event.preventDefault(); void approveFolder(); }}>
          <label>Path inside Allstarr<input bind:value={folderPath} required placeholder="/music" /></label>
          <p class="muted">Where {selectedFolder.name} (<code>{selectedFolder.backendPath}</code>) is mounted in the Allstarr container. Must be an existing writable folder. {setup?.allowedBaseConfigured ? "The server also restricts it to the configured allowed base." : ""}</p>
          <Button type="submit" variant="outline" disabled={folderBusy || !folderPath.trim()}>Use this folder</Button>
        </form>{/if}
        {#if folderError}<p role="status" class="muted">{folderError}</p>{/if}
        {#if administrator}<a href="https://github.com/SoPat712/allstarr/blob/dev/docs/user-guide.md#adding-to-your-library" target="_blank" rel="noreferrer">Library setup guide</a>{/if}
      </div>{/if}
      <div class="settings-row"><div><strong>Default unit</strong><small>What “Add to library” adds from a song</small></div><ThreeDetentSwitch label="Default unit" value={profile.defaultUnit} options={[{ value: "album", label: "Whole album" }, { value: "song", label: "Just the song" }]} onchange={value => update({ defaultUnit: value })} /></div>
      {@render toggle("removeKeptCopy", "Remove kept copy", "Release the kept copy after Jellyfin confirms the library song")}
      {#if administrator && scope === "household"}<div class="settings-row"><div><strong>Who adds without approval</strong><small>Everyone else sends a request to an admin</small></div><SelectField label="Who adds without approval" value={household.addWithoutApproval} options={[{ value: "admins-and-trusted-listeners", label: "Admins and trusted listeners" }, { value: "admins", label: "Admins" }]} onchange={value => { if (!household || household.addWithoutApproval === value) return; household = { ...household, addWithoutApproval: value as ApprovalRoles }; dirty.add(scope); scheduleSave(); }} /></div>{/if}
    </section>
    <section class="settings-card" aria-labelledby="naming-heading">
      <header><h3 id="naming-heading">How added files are named</h3><p>Drag fields to build the name, or edit the naming script directly (Picard syntax).</p></header>
      <div class="naming-content">
        {#if detected?.script && detected.script !== profile.namingScript}
          <div class="naming-detected" role="status"><div><strong>Your library already names files like this</strong>
            {#if detectedExample}<code title={detectedExample}>{detectedExample}</code>{/if}
            <small>{detected.matched} of {detected.sampled} songs in this folder follow the same pattern</small></div>
            <Button variant="outline" size="sm" onclick={() => update({ namingScript: detected!.script! })}>Use this pattern</Button></div>
        {:else if detected?.script}<p class="naming-match">Matches how this folder already names its files.</p>{/if}
        <PromotionNamingBuilder script={profile.namingScript} onchange={namingScript => update({ namingScript })} />
        <div class="naming-examples" class:updating={previewPending && preview} aria-label="Live naming examples" aria-busy={previewPending}>
          {#if previewPending && !preview}<p class="muted">Loading examples…</p>
          {:else if previewError}<div role="alert"><p>{previewError}</p><Button variant="outline" onclick={requestPreview}>Retry examples</Button></div>
          {:else if preview && !preview.errors.length && !preview.examples.length}<p class="muted">No library examples are available yet. Examples will appear when songs are in your library or kept.</p>
          {:else if preview}{#each preview.examples as example}<div class="naming-example"><span>{exampleLabel(example.kind)}</span><div><code class="example-path" title={example.title}>{example.relativePath ?? "Could not render this name"}</code>{#if example.errors.length}{@render errorList(example.errors)}{/if}</div></div>{/each}{/if}
        </div>
      </div>
      <AdvancedFold label="Naming script" bind:open={scriptOpen}><div class="script-body">
          <label class="sr-only" for="promotion-script">Naming script</label><textarea bind:this={scriptInput} id="promotion-script" rows="4" value={profile.namingScript} aria-invalid={errors.length > 0} aria-describedby={errors.length ? "naming-validation" : "script-help"} spellcheck="false" oninput={event => update({ namingScript: event.currentTarget.value })}></textarea>
          <p id="script-help" class="muted">Use / between folders and include .%_extension% for the file extension. Escape literal punctuation with a backslash.</p>
          {#if errors.length}<div id="naming-validation" role="alert">{@render errorList(errors)}</div>{/if}
      </div></AdvancedFold>
    </section>
    <div class="promotion-columns">
      <section class="settings-card" aria-labelledby="promotion-quality"><header><h3 id="promotion-quality">Quality of added songs</h3></header>
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
  </div>
{/if}

<style>
  .folder-setup, .folder-form { display: grid; min-width: 0; gap: var(--space-3); padding: var(--space-4); }
  .folder-form { padding: 0; }
  .folder-list { display: grid; min-width: 0; margin: 0; padding: 0; list-style: none; border: 1px solid var(--color-edge); border-radius: var(--radius-md); }
  .folder-row { display: grid; grid-template-columns: minmax(0, 1fr) auto auto; align-items: center; gap: var(--space-3); min-width: 0; min-height: 3.25rem; padding: var(--space-2) var(--space-3); }
  .folder-row + .folder-row { border-top: 1px solid var(--color-edge); }
  .folder-row > div { display: grid; min-width: 0; gap: 0.125rem; }
  .folder-row strong { color: var(--color-ink); font-size: var(--text-sm); font-weight: 500; }
  .folder-row code { overflow: hidden; color: var(--color-ink-muted); font-size: var(--text-xs); text-overflow: ellipsis; white-space: nowrap; }
  .folder-row small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .folder-row small code { display: inline; }
  .folder-row.chosen { background: color-mix(in srgb, var(--color-signal) 8%, transparent); }
  .folder-hint { margin: 0; color: var(--color-ink); font-size: var(--text-sm); }
  .folder-reset { justify-self: start; padding: var(--space-1) 0; border: 0; background: transparent; color: var(--color-signal-text, var(--color-signal)); font: inherit; font-size: var(--text-sm); cursor: pointer; }
  .folder-reset:hover { text-decoration: underline; }
  .naming-detected { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: var(--space-3); padding: var(--space-3) var(--space-4); border-radius: var(--radius-md); background: var(--color-secondary); color: var(--color-secondary-foreground); }
  .naming-detected > div { display: grid; min-width: 0; gap: var(--space-1); }
  .naming-detected strong { font-size: var(--text-sm); font-weight: 500; }
  .naming-detected code { overflow: hidden; font: 0.76rem/1.5 ui-monospace, SFMono-Regular, Menlo, monospace; text-overflow: ellipsis; white-space: nowrap; }
  .naming-detected small { font-size: var(--text-xs); opacity: .85; }
  .naming-match { margin: 0; color: var(--color-ink-muted); font-size: var(--text-xs); }
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
  .naming-examples { display: grid; gap: var(--space-2); min-width: 0; transition: opacity 160ms var(--ease-standard); }
  .naming-examples.updating { opacity: .55; }
  .naming-example { display: grid; grid-template-columns: 6.5rem minmax(0, 1fr); align-items: baseline; gap: var(--space-3); font-size: var(--text-xs); }
  .naming-example > span { color: var(--color-ink-muted); }
  .example-path, textarea { display: block; width: 100%; min-width: 0; padding: var(--space-2) var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-panel-raised); color: var(--color-ink); font: 0.76rem/1.5 ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
  textarea { resize: vertical; }
  .script-body { padding: var(--space-3) var(--space-4) var(--space-4); font-size: var(--text-sm); }
  .script-body p { margin-top: var(--space-2); }
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
    .folder-row { grid-template-columns: minmax(0, 1fr) auto; }
    .folder-row > :global(button) { grid-column: 1 / -1; justify-self: start; min-height: 44px; }
    .folder-row > span:empty { display: none; }
    .naming-detected { grid-template-columns: minmax(0, 1fr); }
    .naming-detected > :global(button) { justify-self: start; min-height: 44px; }
  }
</style>
