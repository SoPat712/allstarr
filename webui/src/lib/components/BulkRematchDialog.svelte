<script lang="ts">
  import { type PlaylistRematchPreview, type TrackRematchAllPreview, playlistLinks } from "$lib/api";
  import { bulkTrackRematch } from "$lib/matching-api";
  import { Badge } from "$lib/components/ui/badge";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import { X } from "@lucide/svelte";

  type RematchKind = "playlist" | "automatic";
  type Preview = PlaylistRematchPreview | TrackRematchAllPreview;
  type Count = { label: string; value: number };

  let {
    kind,
    open = $bindable(false),
    onQueued,
  }: {
    kind: RematchKind;
    open: boolean;
    onQueued: (jobId: string, message: string) => void | Promise<void>;
  } = $props();

  let prepared = $state(false);
  let loading = $state(false);
  let applying = $state(false);
  let confirmed = $state(false);
  let error = $state("");
  let preview = $state<Preview | null>(null);
  let copy = $derived(presentation(preview));

  $effect(() => {
    if (!open) {
      prepared = false;
      loading = false;
      applying = false;
      confirmed = false;
      error = "";
      preview = null;
      return;
    }
    if (prepared) return;
    prepared = true;
    void loadPreview();
  });

  function tracks(count: number) {
    return `${count.toLocaleString()} ${count === 1 ? "track" : "tracks"}`;
  }

  function presentation(value: Preview | null) {
    if (kind === "playlist") {
      const item = value as PlaylistRematchPreview | null;
      const canApply = item?.canApply ?? false;
      const count = item?.uniqueTracksToRematch ?? 0;
      return {
        title: "Review full rematch",
        description: "Inspect the current account before Allstarr writes any new match decisions.",
        closeLabel: "Close rematch preview",
        loading: "Checking linked playlists, libraries, and protected choices…",
        errorTitle: "The rematch preview could not be prepared",
        consequenceTitle: canApply
          ? `${tracks(count)} ${count === 1 ? "needs" : "need"} review`
          : "No rematch needed",
        badge: canApply ? "Ready to queue" : "Current",
        consequence: item
          ? `Allstarr checked ${item.playlistCount.toLocaleString()} linked playlists across ${item.libraryCount.toLocaleString()} ${item.libraryCount === 1 ? "library" : "libraries"}. Only missing or stale decisions are eligible.`
          : "",
        counts: item ? [
          { label: "Local", value: item.localRows },
          { label: "Exact provider", value: item.exactProviderRows },
          { label: "Generic external", value: item.genericExternalRows },
          { label: "Unresolved", value: item.unresolvedRows },
          { label: "Protected manual", value: item.confirmedManualRows },
          { label: "Stale revision", value: item.staleRevisionRows },
          { label: "Conflicting", value: item.conflictingRows },
          { label: "Rows to rematch", value: item.rowsToRematch },
        ] satisfies Count[] : [],
        countsLabel: "Rematch preview counts",
        safety: "Playlist order, repeated songs, manual choices, and saved provider routes stay intact. This job never downloads or plays media. Rows that change after this preview are skipped.",
        confirmation: "I reviewed these counts and want to queue this exact rematch.",
        applyingLabel: "Queueing rematch…",
        actionLabel: `Rematch ${tracks(count)}`,
        canApply,
      };
    }

    const item = value as TrackRematchAllPreview | null;
    const canApply = item?.canApply ?? false;
    const count = item?.tracksToRematch ?? 0;
    return {
      title: "Rematch every automatic decision",
      description: "Replace resolved and unresolved automatic choices with the current algorithm.",
      closeLabel: "Close full rematch preview",
      loading: "Checking automatic decisions and protected manual choices…",
      errorTitle: "The full rematch preview could not be prepared",
      consequenceTitle: canApply ? `${tracks(count)} will be reprocessed` : "No automatic decisions to rematch",
      badge: canApply ? item?.algorithmVersion ?? "Current algorithm" : "Manual choices only",
      consequence: "The durable worker handles 25 tracks at a time and yields between batches, so playback and other jobs remain responsive.",
      counts: item ? [
        { label: "All tracks", value: item.totalTracks },
        { label: "Resolved", value: item.resolvedTracks },
        { label: "Needs review", value: item.reviewTracks },
        { label: "Unresolved", value: item.unresolvedTracks },
        { label: "Auto decisions replaced", value: item.automaticDecisionsToReplace },
        { label: "Protected manual", value: item.protectedManualTracks },
      ] satisfies Count[] : [],
      countsLabel: "Full rematch preview counts",
      safety: "Manual pins, rejections, and manually selected provider routes stay authoritative. Earlier automatic decisions remain in audit history, but each successful replacement becomes the current decision.",
      confirmation: "I reviewed these counts and want to rematch every automatic decision.",
      applyingLabel: "Queueing full rematch…",
      actionLabel: `Rematch ${tracks(count)}`,
      canApply,
    };
  }

  async function loadPreview() {
    loading = true;
    confirmed = false;
    error = "";
    preview = null;
    try {
      preview = kind === "playlist"
        ? await playlistLinks.previewRematch()
        : await bulkTrackRematch.preview();
    } catch (cause) {
      error = cause instanceof Error
        ? cause.message
        : kind === "playlist"
          ? "Allstarr could not inspect the current match state."
          : "Allstarr could not inspect the current match decisions.";
    } finally {
      loading = false;
    }
  }

  async function applyRematch() {
    if (!preview?.canApply || !confirmed || applying) return;
    applying = true;
    error = "";
    try {
      if (kind === "playlist") {
        const result = await playlistLinks.applyRematch(preview.confirmationId);
        open = false;
        await onQueued(result.jobId, result.created ? "Controlled rematch queued." : "This rematch is already queued.");
      } else {
        const result = await bulkTrackRematch.apply(preview.confirmationId);
        open = false;
        await onQueued(
          result.jobId,
          result.created
            ? `Rematching ${tracks(result.trackCount)} in durable batches.`
            : "This full rematch is already queued.",
        );
      }
    } catch (cause) {
      error = cause instanceof Error
        ? cause.message
        : kind === "playlist"
          ? "The match state changed. Review the preview again."
          : "The match state changed. Review the current counts and try again.";
      confirmed = false;
    } finally {
      applying = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay match-dialog-overlay" />
    <Dialog.Content class="source-dialog match-dialog bulk-rematch-dialog">
      <header>
        <div>
          <Dialog.Title>{copy.title}</Dialog.Title>
          <Dialog.Description>{copy.description}</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label={copy.closeLabel}>
          <X size={18} aria-hidden="true" />
        </Dialog.Close>
      </header>

      <div class="bulk-rematch-body" aria-busy={loading}>
        {#if loading}
          <div class="detail-loading">{copy.loading}</div>
        {:else if error}
          <div class="compact-empty" role="alert">
            <strong>{copy.errorTitle}</strong>
            <p>{error}</p>
            <Button variant="secondary" onclick={() => void loadPreview()}>Try again</Button>
          </div>
        {:else if preview}
          <section class="bulk-rematch-consequence">
            <div>
              <strong>{copy.consequenceTitle}</strong>
              <Badge state={copy.canApply ? "suggested" : "healthy"}>{copy.badge}</Badge>
            </div>
            <p>{copy.consequence}</p>
          </section>

          <dl class:playlist={kind === "playlist"} class="bulk-rematch-counts" aria-label={copy.countsLabel}>
            {#each copy.counts as count}
              <div><dt>{count.label}</dt><dd>{count.value.toLocaleString()}</dd></div>
            {/each}
          </dl>

          <p class="bulk-rematch-safety">{copy.safety}</p>

          {#if copy.canApply}
            <label class="bulk-rematch-confirm">
              <Checkbox class="mt-0.5" bind:checked={confirmed} />
              <span>{copy.confirmation}</span>
            </label>
          {/if}
        {/if}
      </div>

      <footer class="dialog-actions dialog-actions--stacked">
        <Dialog.Close class={buttonVariants({ variant: "secondary" })}>{copy.canApply ? "Cancel" : "Close"}</Dialog.Close>
        {#if copy.canApply}
          <Button disabled={!confirmed || applying} onclick={() => void applyRematch()}>
            {applying ? copy.applyingLabel : copy.actionLabel}
          </Button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  :global(.bulk-rematch-dialog) {
    --dialog-width: 45rem;
    --dialog-max-height: 53.75rem;
  }

  .bulk-rematch-body {
    display: grid;
    min-height: 10rem;
    gap: var(--space-5);
    overflow: auto;
    padding: var(--surface-gutter);
  }

  .bulk-rematch-consequence {
    display: grid;
    gap: var(--space-2);
  }

  .bulk-rematch-consequence > div {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--space-3);
  }

  .bulk-rematch-consequence strong {
    font-size: 1.1rem;
  }

  .bulk-rematch-consequence p,
  .bulk-rematch-safety {
    margin: 0;
    color: var(--color-ink-muted);
    line-height: 1.55;
  }

  .bulk-rematch-counts {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    margin: 0;
  }

  .bulk-rematch-counts.playlist {
    grid-template-columns: repeat(4, minmax(0, 1fr));
  }

  .bulk-rematch-counts div {
    display: grid;
    min-width: 0;
    gap: var(--space-1);
    border-block-start: 1px solid var(--color-edge);
    padding: 0.875rem var(--space-3);
  }

  .bulk-rematch-counts dt {
    color: var(--color-ink-muted);
    font-size: var(--text-xs);
    line-height: 1.3;
  }

  .bulk-rematch-counts dd {
    margin: 0;
    font-size: 1.15rem;
    font-weight: 700;
  }

  .bulk-rematch-confirm {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: start;
    gap: 0.625rem;
    border: 1px solid var(--color-edge);
    border-radius: var(--radius-md);
    background: var(--color-surface-raised);
    cursor: pointer;
    padding: 0.875rem;
  }

  @media (max-width: 620px) {
    .bulk-rematch-counts,
    .bulk-rematch-counts.playlist {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }

  }
</style>
