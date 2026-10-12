<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Info, X } from "@lucide/svelte";
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
  let trackRetention = $state<"onDemand" | "keepAll">("onDemand");
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
    playlist?.importMode ?? "linked",
    trackRetention,
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
    trackRetention = playlist.trackRetention;
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
        (item.credentialReferenceId ?? null) === (playlist.targetCredentialReferenceId ?? null));
      if (!target) throw new Error(`${targetName} is no longer connected to your account.`);
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
      const updated = await playlistLinks.update(playlist.id, {
        expectedRevision: revision,
        mode,
        projectionMode,
        materializationMode,
        importMode: playlist.importMode,
        trackRetention,
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
      const retentionStatus = updated.retentionQueued == null
        ? ""
        : updated.retentionQueued > 0
          ? ` ${updated.retentionQueued} existing ${updated.retentionQueued === 1 ? "song is" : "songs are"} queued for permanent storage.`
          : " Permanent storage is enabled for this playlist.";
      await onSaved(`Playlist settings saved.${retentionStatus} ${behaviorSummary}`);
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
    <Dialog.Content class="source-dialog match-dialog playlist-settings-dialog pl-dialog">
      <header>
        <div>
          <Dialog.Title>Edit playlist settings</Dialog.Title>
          <Dialog.Description>Changes apply to {sourcePlaylistName} in Allstarr. The original playlist in {sourceName} is not changed.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close playlist settings"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>
      <form class="playlist-settings-form" onsubmit={save}>
        <div class="playlist-settings-body">
          {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

          <div class="pl-summary" role="status">
            <Info size={20} aria-hidden="true" />
            <p><strong>What will happen</strong>{behaviorSummary}</p>
          </div>

          <section class="pl-section">
            <h3 id="playlist-settings-view">What listeners see</h3>
            <div class="pl-options" role="radiogroup" aria-labelledby="playlist-settings-view">
              {#each projectionOptions as option}
                <label class="pl-option" class:active={projectionMode === option.id}>
                  <input type="radio" name="settings-projection" value={option.id} checked={projectionMode === option.id} onchange={() => void chooseProjection(option.id)} />
                  <span class="pl-option-text"><strong>{option.label}{#if option.id === "resolved"}<span class="pl-chip">Recommended</span>{/if}</strong><small>{option.description}</small></span>
                </label>
              {/each}
            </div>
          </section>

          <section class="pl-section">
            <h3 id="playlist-settings-destination">Where it appears</h3>
            <div class="pl-options" role="radiogroup" aria-labelledby="playlist-settings-destination">
              {#each destinationOptions as option}
                <label class="pl-option" class:active={mode === option.id}>
                  <input type="radio" name="settings-destination" value={option.id} checked={mode === option.id} onchange={() => void chooseDestination(option.id)} />
                  <span class="pl-option-text"><strong>{option.label}</strong><small>{option.description}</small></span>
                </label>
              {/each}
            </div>
          </section>

          <div class="pl-rows">
            {#if needsTarget}
              <div class="pl-row">
                <div><strong>{targetName} playlist</strong><small>The playlist Allstarr reads from or adds songs to.</small></div>
                <SelectField
                  bind:value={targetPlaylistId}
                  label={`Playlist in ${targetName}`}
                  options={targetPlaylists.map((item) => ({ value: item.id, label: item.name }))}
                  disabled={loading}
                />
              </div>
            {/if}
            {#if mode !== "virtual"}
              <div class="pl-row">
                <div><strong>Playlist changes</strong><small>Add songs to {targetPlaylistName}, or create a new playlist each time.</small></div>
                <SelectField bind:value={materializationMode} label="Playlist changes" options={[
                  { value: "reconcile", label: `Add to ${targetPlaylistName}` },
                  { value: "recreate", label: "Create a new playlist" },
                ]} />
              </div>
              {#if materializationMode === "reconcile"}
                <div class="pl-row">
                  <div><strong>Songs removed from {sourcePlaylistName}</strong><small>What happens to them in {targetPlaylistName}.</small></div>
                  <SelectField bind:value={syncBehavior} label={`Songs removed from ${sourcePlaylistName}`} options={[
                    { value: "preserve", label: "Keep them" },
                    { value: "mirror", label: "Remove songs Allstarr added" },
                  ]} />
                </div>
              {/if}
            {/if}
            <div class="pl-row">
              <div><strong>Update schedule</strong><small>{playlist?.importMode === "oneTime" ? "Imported once. Later changes are ignored." : details?.schedule ? scheduleCadence(details.schedule.cronExpression) : "Updates only when you ask."}</small></div>
              {#if playlist?.importMode !== "oneTime"}<Button variant="secondary" onclick={() => { open = false; onEditSchedule(); }}>Edit schedule</Button>{/if}
            </div>
            <div class="pl-row pl-row-full">
              <div><strong id="playlist-settings-storage">Song storage</strong><small>Choose whether Allstarr saves permanent copies of these songs.</small></div>
              <div class="pl-options pl-options-two" role="radiogroup" aria-labelledby="playlist-settings-storage">
                <label class="pl-option" class:active={trackRetention === "onDemand"}>
                  <input bind:group={trackRetention} type="radio" name="settings-storage" value="onDemand" />
                  <span class="pl-option-text"><strong>Stream when played</strong><small>Songs play from their source and are cached briefly. Nothing is saved permanently.</small></span>
                </label>
                <label class="pl-option" class:active={trackRetention === "keepAll"}>
                  <input bind:group={trackRetention} type="radio" name="settings-storage" value="keepAll" />
                  <span class="pl-option-text"><strong>Keep a copy of every song</strong><small>After each import, Allstarr downloads every song it can. Uses disk space.</small></span>
                </label>
              </div>
            </div>
            {#if mode !== "virtual"}
              <div class="pl-row pl-row-full" role="group" aria-labelledby="playlist-settings-details">
                <div><strong id="playlist-settings-details">{materializationMode === "recreate" ? "Copy to the new playlist" : "Keep in sync"}</strong><small>Copy these details from {sourceName} to the {targetName} playlist.</small></div>
                <div class="pl-checks">
                  <label><Checkbox bind:checked={syncName} /> Name</label>
                  <label><Checkbox bind:checked={syncDescription} /> Description</label>
                  <label><Checkbox bind:checked={syncArtwork} /> Artwork</label>
                </div>
              </div>
            {/if}
          </div>
        </div>

        <footer class="dialog-actions">
          <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
          <Button type="submit" disabled={loading || saving || playlist?.revision !== revision || !targetSelectionValid}>
            {saving ? "Saving…" : "Save settings"}
          </Button>
        </footer>
      </form>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
