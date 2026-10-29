<script lang="ts">
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
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
    scheduleCadence,
  } from "$lib/playlists";

  type TargetMode = "new" | "existing";

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

  let targetRequest = 0;
  let prepared = $state(false);
  let targetMode = $state<TargetMode>("existing");
  let trackRetention = $state<"onDemand" | "keepAll">("onDemand");
  let targetPlaylistId = $state("");
  let targetPlaylists = $state<TargetPlaylist[]>([]);
  let revision = $state(0);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");

  const targetSelectionValid = $derived(
    targetMode === "new" || targetPlaylists.some((item) => item.id === targetPlaylistId),
  );
  const targetPlaylistName = $derived(
    targetMode === "new"
      ? `a new ${targetName} playlist`
      : targetPlaylists.find((item) => item.id === targetPlaylistId)?.name ?? `the selected ${targetName} playlist`,
  );
  const sourcePlaylistName = $derived(playlist?.name ?? "this playlist");
  const importMode = $derived(playlist?.importMode ?? "linked");
  const updateCadence = $derived.by(() => {
    if (!details?.schedule) return undefined;
    const cadence = scheduleCadence(details.schedule.cronExpression);
    return cadence.charAt(0).toLocaleLowerCase() + cadence.slice(1);
  });
  const behaviorSummary = $derived(playlistBehaviorSummary(
    importMode,
    targetMode,
    sourcePlaylistName,
    targetName,
    targetPlaylistName,
    updateCadence,
    trackRetention,
  ));

  $effect(() => {
    if (!open) {
      targetRequest++;
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
    targetMode = playlist.targetPlaylistId ? "existing" : "new";
    trackRetention = playlist.trackRetention;
    targetPlaylistId = playlist.targetPlaylistId ?? "";
    targetPlaylists = [];
    error = "";
    if (targetMode === "existing") void loadTargets();
  });

  async function loadTargets() {
    if (!playlist || targetMode !== "existing") return;
    const requestedLink = playlist;
    const request = ++targetRequest;
    const requestedPlaylistId = targetPlaylistId;
    targetPlaylistId = "";
    targetPlaylists = [];
    loading = true;
    error = "";
    try {
      const targets = await playlistLinks.targets();
      const target = targets.targets.find((item) =>
        item.protocol === requestedLink.targetProtocol &&
        item.backendInstanceId === requestedLink.targetBackendInstanceId &&
        (item.credentialReferenceId ?? null) === (requestedLink.targetCredentialReferenceId ?? null));
      if (!target) throw new Error(`${targetName} is no longer connected to your account.`);
      const response = await playlistLinks.targetPlaylists(target.id);
      if (request !== targetRequest || !open || playlist?.id !== requestedLink.id || targetMode !== "existing") return;
      targetPlaylists = response.items.filter((item) => item.writable);
      if (requestedPlaylistId && !targetPlaylists.some((item) => item.id === requestedPlaylistId))
        throw new Error(`${targetPlaylistName} can no longer be updated in ${targetName}.`);
      targetPlaylistId = requestedPlaylistId;
    } catch (cause) {
      if (request !== targetRequest || !open) return;
      error = cause instanceof Error ? cause.message : `Playlists in ${targetName} could not be loaded.`;
    } finally {
      if (request === targetRequest) loading = false;
    }
  }

  async function chooseTargetMode(value: TargetMode) {
    targetRequest++;
    loading = false;
    targetMode = value;
    if (value === "new") {
      targetPlaylistId = "";
      targetPlaylists = [];
      error = "";
    } else if (!targetPlaylists.length) {
      await loadTargets();
    }
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
        mode: "hybrid",
        projectionMode: "resolved",
        materializationMode: "reconcile",
        importMode,
        trackRetention,
        scheduleId: playlist.scheduleId,
        targetPlaylistId: targetMode === "existing" ? targetPlaylistId : null,
        targetCredentialReferenceId: playlist.targetCredentialReferenceId,
        mirrorStaleEntries: true,
        preserveManualEntries: true,
        syncName: true,
        syncDescription: true,
        syncArtwork: true,
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
            <h3 id="playlist-settings-import">Import behavior</h3>
            <div class="pl-options pl-options-two" role="group" aria-labelledby="playlist-settings-import">
              <div class="pl-option pl-option-static" class:active={importMode === "oneTime"}>
                <span class="pl-option-text"><strong>Import once{#if importMode === "oneTime"}<span class="pl-chip">Current</span>{/if}</strong><small>Import the source playlist once and ignore later source changes.</small></span>
              </div>
              <div class="pl-option pl-option-static" class:active={importMode === "linked"}>
                <span class="pl-option-text"><strong>Keep in sync{#if importMode === "linked"}<span class="pl-chip">Current</span>{/if}</strong><small>Keep the Allstarr playlist updated on its existing schedule.</small></span>
              </div>
            </div>
            <p class="pl-help">Import behavior is fixed after creation. Remove the link and import again to choose different behavior.</p>
          </section>

          <section class="pl-section">
            <h3 id="playlist-settings-destination">Destination</h3>
            <div class="pl-options" role="radiogroup" aria-labelledby="playlist-settings-destination">
              <label class="pl-option" class:active={targetMode === "new"}>
                <input type="radio" name="settings-target-mode" value="new" checked={targetMode === "new"} onchange={() => void chooseTargetMode("new")} />
                <span class="pl-option-text"><strong>New playlist</strong><small>Create a new playlist in {targetName} and keep it owned by Allstarr.</small></span>
              </label>
              <label class="pl-option" class:active={targetMode === "existing"}>
                <input type="radio" name="settings-target-mode" value="existing" checked={targetMode === "existing"} onchange={() => void chooseTargetMode("existing")} />
                <span class="pl-option-text"><strong>Existing playlist</strong><small>Update a playlist you already selected in {targetName}.</small></span>
              </label>
            </div>
          </section>

          <div class="pl-rows">
            {#if targetMode === "existing"}
              <div class="pl-row">
                <div><strong>{targetName} playlist</strong><small>The playlist Allstarr updates with matched songs.</small></div>
                <SelectField
                  bind:value={targetPlaylistId}
                  label={`Playlist in ${targetName}`}
                  options={targetPlaylists.map((item) => ({ value: item.id, label: item.name }))}
                  disabled={loading}
                />
              </div>
            {/if}
            <div class="pl-row">
              <div><strong>Update schedule</strong><small>{importMode === "oneTime" ? "Imported once. Later changes are ignored." : details?.schedule ? scheduleCadence(details.schedule.cronExpression) : "Updates only when you ask."}</small></div>
              {#if importMode !== "oneTime"}<Button variant="secondary" onclick={() => { open = false; onEditSchedule(); }}>Edit schedule</Button>{/if}
            </div>
            <div class="pl-row pl-row-full">
              <label class="pl-retention-toggle">
                <Checkbox checked={trackRetention === "keepAll"} onCheckedChange={(checked) => { trackRetention = checked ? "keepAll" : "onDemand"; }} />
                <span><strong>Keep downloaded copies</strong><small>Save permanent copies of external songs Allstarr can download. Local songs are already permanent.</small></span>
              </label>
            </div>
          </div>
        </div>

        <footer class="dialog-actions">
          <Dialog.Close type="button" class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
          <Button type="submit" disabled={loading || saving || playlist?.revision !== revision || !targetSelectionValid}>
            {saving ? "Saving…" : "Save settings"}
          </Button>
        </footer>
      </form>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
