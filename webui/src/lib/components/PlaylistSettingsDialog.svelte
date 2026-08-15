<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { X } from "lucide-svelte";
  import {
    playlistLinks,
    type PlaylistDetails,
    type PlaylistLink,
    type TargetPlaylist,
  } from "$lib/api";
  import SelectField from "$lib/components/SelectField.svelte";
  import {
    playlistBehaviorSummary,
    playlistDestinationOptions,
    playlistProjectionOptions,
    scheduleCadence,
  } from "$lib/playlists";

  let {
    open = $bindable(false),
    playlist,
    details,
    sourceName,
    targetName,
    onSaved,
    onEditSchedule,
  }: {
    open: boolean;
    playlist: PlaylistLink | null;
    details: PlaylistDetails | null;
    sourceName: string;
    targetName: string;
    onSaved: (message: string) => void | Promise<void>;
    onEditSchedule: () => void;
  } = $props();

  let prepared = $state(false);
  let mode = $state<"virtual" | "materialized" | "hybrid">("materialized");
  let projectionMode = $state<"resolved" | "source" | "target">("resolved");
  let materializationMode = $state<"reconcile" | "recreate">("reconcile");
  let syncBehavior = $state<"preserve" | "mirror">("preserve");
  let syncName = $state(true);
  let syncDescription = $state(true);
  let syncArtwork = $state(true);
  let targetPlaylistId = $state("");
  let targetPlaylists = $state<TargetPlaylist[]>([]);
  let revision = $state(0);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");

  const needsTarget = $derived(mode !== "virtual" || projectionMode === "target");
  const targetSelectionValid = $derived(
    !needsTarget || targetPlaylists.some((item) => item.id === targetPlaylistId),
  );
  const targetPlaylistName = $derived(
    targetPlaylists.find((item) => item.id === targetPlaylistId)?.name ?? `the selected ${targetName} playlist`,
  );
  const sourcePlaylistName = $derived(playlist?.name ?? "this playlist");
  const destinationOptions = $derived(playlistDestinationOptions(
    targetName,
    targetPlaylistName,
    sourcePlaylistName,
  ));
  const projectionOptions = $derived(playlistProjectionOptions(sourceName, targetName, targetPlaylistName));
  const updateCadence = $derived.by(() => {
    if (!details?.schedule) return undefined;
    const cadence = scheduleCadence(details.schedule.cronExpression);
    return cadence.charAt(0).toLocaleLowerCase() + cadence.slice(1);
  });
  const behaviorSummary = $derived(playlistBehaviorSummary(
    mode,
    materializationMode,
    sourcePlaylistName,
    targetName,
    targetPlaylistName,
    updateCadence,
  ));

  $effect(() => {
    if (!open) {
      prepared = false;
      return;
    }
    if (prepared) {
      if (playlist && playlist.revision !== revision)
        error = "This playlist changed while you were editing. Close and reopen settings to load the current revision.";
      return;
    }
    if (!playlist) return;
    prepared = true;
    revision = playlist.revision;
    mode = playlist.mode;
    projectionMode = playlist.projectionMode;
    materializationMode = playlist.materializationMode;
    syncBehavior = playlist.mirrorStaleEntries ? "mirror" : "preserve";
    syncName = playlist.syncName;
    syncDescription = playlist.syncDescription;
    syncArtwork = playlist.syncArtwork;
    targetPlaylistId = playlist.targetPlaylistId ?? "";
    targetPlaylists = [];
    error = "";
    void loadTargets();
  });

  async function loadTargets() {
    if (!playlist || !needsTarget) return;
    const requestedPlaylistId = targetPlaylistId;
    targetPlaylistId = "";
    targetPlaylists = [];
    loading = true;
    error = "";
    try {
      const targets = await playlistLinks.targets();
      const target = targets.targets.find((item) =>
        item.protocol === playlist.targetProtocol &&
        item.backendInstanceId === playlist.targetBackendInstanceId &&
        (item.libraryScopeId ?? null) === (playlist.libraryScopeId ?? null) &&
        (item.credentialReferenceId ?? null) === (playlist.targetCredentialReferenceId ?? null));
      if (!target) throw new Error(`${targetName} is no longer connected to this library.`);
      const response = await playlistLinks.targetPlaylists(target.id);
      targetPlaylists = response.items.filter((item) => item.writable);
      if (requestedPlaylistId && !targetPlaylists.some((item) => item.id === requestedPlaylistId))
        throw new Error(`${targetPlaylistName} can no longer be updated in ${targetName}.`);
      targetPlaylistId = requestedPlaylistId;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : `Playlists in ${targetName} could not be loaded.`;
    } finally {
      loading = false;
    }
  }

  async function chooseDestination(value: typeof mode) {
    mode = value;
    if (needsTarget && !targetPlaylists.length) await loadTargets();
  }

  async function chooseProjection(value: typeof projectionMode) {
    projectionMode = value;
    if (needsTarget && !targetPlaylists.length) await loadTargets();
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!playlist || saving) return;
    if (playlist.revision !== revision) {
      error = "This playlist changed while you were editing. Close and reopen settings to load the current revision.";
      return;
    }
    if (!targetSelectionValid) {
      error = `Choose the playlist in ${targetName} that Allstarr should update.`;
      return;
    }
    saving = true;
    error = "";
    try {
      await playlistLinks.update(playlist.id, {
        expectedRevision: revision,
        mode,
        projectionMode,
        materializationMode,
        scheduleId: playlist.scheduleId,
        targetPlaylistId: needsTarget ? targetPlaylistId : null,
        targetCredentialReferenceId: playlist.targetCredentialReferenceId,
        mirrorStaleEntries: syncBehavior === "mirror",
        preserveManualEntries: syncBehavior === "preserve",
        syncName,
        syncDescription,
        syncArtwork,
        ruleVersion: playlist.ruleVersion,
        policyVersion: playlist.policyVersion,
      });
      open = false;
      await onSaved(`Playlist settings saved. ${behaviorSummary}`);
    } catch (cause) {
      error = cause instanceof Error
        ? cause.message
        : "The playlist changed before these settings could be saved.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay match-dialog-overlay" />
    <Dialog.Content class="source-dialog match-dialog playlist-settings-dialog">
      <header>
        <div>
          <p class="eyebrow">Playlist behavior</p>
          <Dialog.Title>Edit playlist settings</Dialog.Title>
          <Dialog.Description>Choose what listeners see and whether Allstarr updates a playlist in {targetName}. The original playlist in {sourceName} is not changed.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close playlist settings"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>
      <form class="playlist-settings-form" onsubmit={save}>
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

        <fieldset class="audience-options playlist-mode-options">
          <legend>What listeners see</legend>
          {#each projectionOptions as option}
            <label class:active={projectionMode === option.id}>
              <input type="radio" name="settings-projection" value={option.id} checked={projectionMode === option.id} onchange={() => void chooseProjection(option.id)} />
              <span><strong>{option.label}</strong><small>{option.description}</small></span>
            </label>
          {/each}
        </fieldset>

        <fieldset class="audience-options playlist-mode-options">
          <legend>Where it appears</legend>
          {#each destinationOptions as option}
            <label class:active={mode === option.id}>
              <input type="radio" name="settings-destination" value={option.id} checked={mode === option.id} onchange={() => void chooseDestination(option.id)} />
              <span><strong>{option.label}</strong><small>{option.description}</small></span>
            </label>
          {/each}
        </fieldset>

        {#if needsTarget}
          <SelectField
            bind:value={targetPlaylistId}
            label={`Playlist in ${targetName}`}
            options={targetPlaylists.map((item) => ({ value: item.id, label: item.name }))}
            disabled={loading}
          />
        {/if}

        {#if mode !== "virtual"}
          <div class="setting-field">
            <span><strong>Which playlist Allstarr changes</strong><small>Change {targetPlaylistName}, or create a new playlist in {targetName} instead.</small></span>
            <SelectField bind:value={materializationMode} label={`How ${targetName} is updated`} options={[
              { value: "reconcile", label: `Change ${targetPlaylistName}` },
              { value: "recreate", label: `Create a new playlist in ${targetName}` },
            ]} />
          </div>
          {#if materializationMode === "reconcile"}
            <div class="setting-field">
              <span><strong>When songs leave {sourcePlaylistName}</strong><small>Choose whether {targetPlaylistName} keeps songs that are no longer in {sourcePlaylistName}.</small></span>
              <SelectField bind:value={syncBehavior} label={`Songs no longer in ${sourcePlaylistName}`} options={[
                { value: "preserve", label: `Keep them in ${targetPlaylistName}` },
                { value: "mirror", label: "Remove songs Allstarr previously added" },
              ]} />
            </div>
          {/if}
          <fieldset class="playlist-sync-fields">
            <legend>{materializationMode === "recreate" ? "Copy these details to the new playlist" : "Keep these details updated"}</legend>
            <label><Checkbox bind:checked={syncName} /> Playlist name</label>
            <label><Checkbox bind:checked={syncDescription} /> Description</label>
            <label><Checkbox bind:checked={syncArtwork} /> Artwork</label>
          </fieldset>
        {/if}

        <div class="setting-field playlist-schedule-setting">
          <span><strong>Automatic updates</strong><small>{details?.schedule ? scheduleCadence(details.schedule.cronExpression) : "Manual only"}</small></span>
          <Button variant="secondary" onclick={() => { open = false; onEditSchedule(); }}>Edit schedule</Button>
        </div>

        <p class="credential-safety">{behaviorSummary}</p>

        <footer>
          <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
          <Button type="submit" disabled={loading || saving || playlist?.revision !== revision || !targetSelectionValid}>
            {saving ? "Saving…" : "Save settings"}
          </Button>
        </footer>
      </form>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
