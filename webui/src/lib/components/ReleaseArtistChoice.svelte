<script lang="ts">
  import { onMount } from "svelte";
  import { X } from "@lucide/svelte";
  import { releaseInbox, type FollowedReleaseArtist, type ReleaseArtistCandidate } from "$lib/api";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import UiIcon from "$lib/components/UiIcon.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";

  let { open = $bindable(false), artist, reason = "", onsave }: {
    open: boolean;
    artist: FollowedReleaseArtist;
    reason?: string;
    onsave: (providerId: string | null, artistId: string | null) => Promise<void>;
  } = $props();
  let current = $state<FollowedReleaseArtist | null>(null), candidates = $state<ReleaseArtistCandidate[]>([]);
  let currentChoice = $state<ReleaseArtistCandidate | null>(null);
  let loading = $state(true), saving = $state(false), loadError = $state(""), saveError = $state("");
  let selection = $state("");
  let disposed = false;
  const candidateKey = (candidate: { providerId: string; artistId: string }) => JSON.stringify([candidate.providerId, candidate.artistId]);
  const currentKey = $derived(current?.providerId === "jellyfin" ? "none" : current ? candidateKey(current) : "");
  const currentCandidate = $derived(currentChoice ?? candidates.find(candidate => candidateKey(candidate) === currentKey));
  const extraCurrent = $derived(currentChoice && !candidates.some(candidate => candidateKey(candidate) === currentKey) ? currentChoice : null);
  const selected = $derived(candidates.find(candidate => candidateKey(candidate) === selection) ?? (currentChoice && candidateKey(currentChoice) === selection ? currentChoice : undefined));
  const currentName = $derived(currentKey === "none" ? "None of these" : `${currentCandidate?.displayName ?? current?.displayName ?? artist.displayName}${currentCandidate ? ` · ${currentCandidate.serviceName}` : ""}`);

  async function loadCandidates() {
    if (!artist.libraryArtistId) return;
    loading = true;
    loadError = "";
    try {
      const result = await releaseInbox.artistCandidates(artist.libraryArtistId);
      if (disposed) return;
      current = result.artist;
      candidates = result.candidates;
      currentChoice = result.currentChoice ?? null;
      selection = current.providerId === "jellyfin" ? "none" : candidateKey(current);
    } catch { if (!disposed) loadError = "Artists could not be loaded. Try again."; }
    finally { if (!disposed) loading = false; }
  }
  async function save() {
    if (saving || loading || (selection !== "none" && !selected)) return;
    saving = true;
    saveError = "";
    try {
      await onsave(selected?.providerId ?? null, selected?.artistId ?? null);
      if (!disposed) open = false;
    } catch { if (!disposed) saveError = "Your choice could not be saved. Try again."; }
    finally { if (!disposed) saving = false; }
  }
  onMount(() => { void loadCandidates(); return () => { disposed = true; }; });
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay match-dialog-overlay" />
    <Dialog.Content class="match-dialog release-artist-choice">
      <header>
        <div><Dialog.Title>Choose the right artist</Dialog.Title><Dialog.Description>Find {artist.displayName} on a music service to check their new releases.</Dialog.Description></div>
        <Dialog.Close class="icon-button" aria-label="Close artist choices" disabled={saving}><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>
      <div class="choice-body">
        {#if reason}<p class="choice-reason">{reason}</p>{/if}
        {#if loading}<RowsSkeleton count={3} label="Loading artist choices" />
        {:else if loadError}<ErrorState title="Couldn't load artists" detail={loadError} onretry={loadCandidates} />
        {:else}
          <p class="current-choice">Current choice: <strong>{currentName}</strong></p>
          {#if !candidates.length}<p class="choice-reason">No matching artists were found on your connected music services.</p>{/if}
          <fieldset class="artist-options" disabled={saving}>
            <legend class="sr-only">Artist choices</legend>
            {#if extraCurrent}
              <label class="artist-option current-option" class:chosen={selection === candidateKey(extraCurrent)}>
                <input type="radio" name="release-artist-choice" value={candidateKey(extraCurrent)} bind:group={selection} aria-label={`${extraCurrent.displayName} on ${extraCurrent.serviceName}`} onchange={() => saveError = ""} />
                <MediaArtwork url={extraCurrent.imageUrl} class="candidate-art" />
                <span class="candidate-copy"><strong>{extraCurrent.displayName}</strong><span class="candidate-service">{extraCurrent.serviceName}</span>
                  {#if extraCurrent.hints.length}<span class="candidate-hints">{extraCurrent.hints.join(" · ")}</span>{/if}
                  <span class="current-mark"><UiIcon name="check" size={16} />Current choice</span>
                </span>
              </label>
              {#if candidates.length}<p class="choice-reason">Other artists</p>{/if}
            {/if}
            {#each candidates as candidate (candidateKey(candidate))}
              <label class="artist-option" class:chosen={selection === candidateKey(candidate)}>
                <input type="radio" name="release-artist-choice" value={candidateKey(candidate)} bind:group={selection} aria-label={`${candidate.displayName} on ${candidate.serviceName}`} onchange={() => saveError = ""} />
                <MediaArtwork url={candidate.imageUrl} class="candidate-art" />
                <span class="candidate-copy"><strong>{candidate.displayName}</strong><span class="candidate-service">{candidate.serviceName}</span>
                  {#if candidate.hints.length}<span class="candidate-hints">{candidate.hints.join(" · ")}</span>{/if}
                  {#if currentKey === candidateKey(candidate)}<span class="current-mark"><UiIcon name="check" size={16} />Current choice</span>{/if}
                </span>
              </label>
            {/each}
            <label class="artist-option none-option" class:chosen={selection === "none"}>
              <input type="radio" name="release-artist-choice" value="none" bind:group={selection} onchange={() => saveError = ""} />
              <span class="candidate-copy"><strong>None of these</strong><span class="candidate-hints">Keep this artist library-only, without release checks.</span>
                {#if currentKey === "none"}<span class="current-mark"><UiIcon name="check" size={16} />Current choice</span>{/if}
              </span>
            </label>
          </fieldset>
          {#if saveError}<p class="notice-error" role="alert">{saveError}</p>{/if}
        {/if}
      </div>
      <footer class="dialog-actions">
        <Dialog.Close class={buttonVariants({ variant: "text" })} disabled={saving}>Cancel</Dialog.Close>
        <Button disabled={loading || Boolean(loadError) || saving || (selection !== "none" && !selected)} onclick={() => void save()}>{saving ? "Saving…" : "Save choice"}</Button>
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  :global(.release-artist-choice) { --dialog-width: 38rem; display: grid; grid-template-rows: auto minmax(0, 1fr) auto; overflow: hidden; }
  .choice-body { display: grid; gap: var(--space-4); min-width: 0; overflow-y: auto; padding: var(--surface-gutter); }
  .choice-reason, .current-choice { margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; overflow-wrap: anywhere; }
  .current-choice strong { color: var(--color-ink); font-weight: 600; }
  .artist-options { display: grid; gap: var(--space-2); min-width: 0; margin: 0; padding: 0; border: 0; }
  .artist-option { display: grid; grid-template-columns: 20px 52px minmax(0, 1fr); align-items: center; gap: var(--space-3); min-height: 44px; padding: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-lg); cursor: pointer; }
  .artist-option.chosen { border-color: var(--color-signal-text); background: var(--color-signal-muted); }
  .artist-option:focus-within { outline: 2px solid var(--focus-ring); outline-offset: 2px; }
  .artist-option input { width: 20px; height: 20px; margin: 0; accent-color: var(--color-signal-text); }
  .artist-option :global(.candidate-art) { width: 52px; height: 52px; border-radius: 50%; overflow: hidden; }
  .candidate-copy { display: grid; gap: var(--space-1); min-width: 0; overflow-wrap: anywhere; }
  .candidate-copy strong { font-size: var(--text-sm); font-weight: 600; }
  .candidate-service, .candidate-hints { color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; }
  .current-mark { display: flex; align-items: center; gap: var(--space-1); color: var(--color-signal-text); font-size: var(--text-xs); }
  .none-option { grid-template-columns: 20px minmax(0, 1fr); }
  .notice-error { margin: 0; }
</style>
