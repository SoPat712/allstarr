<script lang="ts">
  import { Dialog } from "bits-ui";
  import {
    matchReview,
    type MatchReviewItem,
    type MatchTarget,
    type ProviderDefinition,
  } from "$lib/api";
  import ArtworkSimilarity from "$lib/components/ArtworkSimilarity.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import {
    percent,
    providerResultCounts,
    rankedTargets,
    scoreComponents,
  } from "$lib/mappings";
  import { formatDuration } from "$lib/playlists";

  let {
    open = $bindable(false),
    match,
    providers,
    backend,
    autoSearch = false,
    showReject = true,
    onSaved,
    onReject,
  }: {
    open: boolean;
    match: MatchReviewItem | null;
    providers: ProviderDefinition[];
    backend: string;
    autoSearch?: boolean;
    showReject?: boolean;
    onSaved: (message: string) => void | Promise<void>;
    onReject?: (match: MatchReviewItem) => void;
  } = $props();

  let preparedId = $state("");
  let targetQuery = $state("");
  let providerFilter = $state("");
  let results = $state<MatchTarget[]>([]);
  let searched = $state(false);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");

  const resultProviders = $derived(providerResultCounts(results));
  const visibleResults = $derived(
    providerFilter
      ? results.filter((target) =>
          providerFilter === "local"
            ? !target.externalProvider
            : target.externalProvider?.toLowerCase() === providerFilter.toLowerCase())
      : results,
  );

  $effect(() => {
    if (!open) {
      preparedId = "";
      return;
    }
    if (!match || preparedId === match.externalSnapshotId) return;
    preparedId = match.externalSnapshotId;
    targetQuery = [match.artist, match.title].filter(Boolean).join(" ");
    providerFilter = "";
    results = [];
    searched = false;
    error = "";
    if (autoSearch) void search();
  });

  function provider(providerId: string) {
    return providers.find((item) => item.id.toLowerCase() === providerId.toLowerCase());
  }

  function providerName(providerId?: string | null) {
    if (!providerId || providerId === "local") return backend;
    return provider(providerId)?.name ?? providerId;
  }

  function candidateArtwork(backendItemId?: string | null) {
    return backendItemId
      ? `/api/admin/downloads/artwork/${encodeURIComponent(backendItemId)}`
      : "";
  }

  async function search() {
    if (!match || targetQuery.trim().length < 2 || loading) return;
    loading = true;
    searched = true;
    error = "";
    try {
      const [local, external] = await Promise.all([
        matchReview.searchLocal(
          targetQuery.trim(),
          match.libraryScopeId,
          match.externalSnapshotId,
        ),
        matchReview.searchProviders(
          targetQuery.trim(),
          match.libraryScopeId,
          match.externalSnapshotId,
        ),
      ]);
      results = rankedTargets([...local.tracks, ...external.tracks]);
    } catch (cause) {
      results = [];
      error = cause instanceof Error ? cause.message : "Candidate search failed.";
    } finally {
      loading = false;
    }
  }

  async function chooseLocal(libraryTrackId: string, reason: string) {
    if (!match || saving) return;
    saving = true;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        targetType: "local",
        libraryTrackId,
        reason,
      });
      open = false;
      await onSaved("Local match saved.");
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The match could not be saved.";
    } finally {
      saving = false;
    }
  }

  async function chooseProvider(target: MatchTarget) {
    if (!match || !target.externalProvider || !target.externalId || saving) return;
    saving = true;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        targetType: "provider",
        externalProvider: target.externalProvider,
        externalId: target.externalId,
        reason: "Selected from the provider-neutral match dialog",
      });
      open = false;
      await onSaved("Provider route saved.");
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The route could not be saved.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay match-dialog-overlay" />
    <Dialog.Content class="match-dialog">
      {#if match}
        <header>
          <div>
            <p class="eyebrow">Review match</p>
            <Dialog.Title>{match.title || "Unknown track"}</Dialog.Title>
            <Dialog.Description>{match.artist || "Unknown artist"} · {providerName(match.providerId)}</Dialog.Description>
          </div>
          <Dialog.Close class="icon-button" aria-label="Close match dialog">×</Dialog.Close>
        </header>

        <section class="mapping-source">
          <span class="media-art mapping-art">
            {#if match.sourceArtworkUrl}<img src={match.sourceArtworkUrl} alt="" />{:else}<ProviderMark id={match.providerId} definition={provider(match.providerId)} />{/if}
          </span>
          <div>
            <small>Source track</small>
            <strong>{match.title || "Unknown track"}</strong>
            <span>{match.artist || "Unknown artist"}{match.album ? ` · ${match.album}` : ""}</span>
            <span>{formatDuration(match.durationMilliseconds)}{match.isrc ? ` · ISRC ${match.isrc}` : ""}</span>
          </div>
        </section>

        <details class="match-technical">
          <summary>PostgreSQL and identity data</summary>
          <dl>
            <div><dt>Source snapshot</dt><dd>{match.externalSnapshotId}</dd></div>
            <div><dt>Source provider</dt><dd>{match.providerId}</dd></div>
            <div><dt>Library scope</dt><dd>{match.libraryScopeId}</dd></div>
            {#if match.canonicalRecordingId}<div><dt>Canonical recording</dt><dd>{match.canonicalRecordingId}</dd></div>{/if}
            {#if match.libraryTrackId}<div><dt>Library track</dt><dd>{match.libraryTrackId}</dd></div>{/if}
            {#if match.algorithmVersion}<div><dt>Algorithm</dt><dd>{match.algorithmVersion}</dd></div>{/if}
            {#each match.providerIdentities as identity}
              <div><dt>{providerName(identity.providerId)}</dt><dd>{identity.externalId} · {identity.verification}</dd></div>
            {/each}
          </dl>
        </details>

        <section class="automatic-candidates">
          <div class="dialog-section-heading">
            <div><strong>Automatic candidates</strong><small>Same scores used by automatic matching</small></div>
            <span>{match.candidates.length}</span>
          </div>
          {#if match.candidates.length}
            <div class="candidate-list">
              {#each match.candidates.slice(0, 5) as candidate}
                <article class="candidate-card">
                  <MediaArtwork class="mapping-art" url={candidateArtwork(candidate.backendItemId)} />
                  <div class="candidate-copy">
                    <strong>{candidate.title || candidate.backendItemId || "Indexed track"}</strong>
                    <small>{candidate.artist || "Unknown artist"}{candidate.album ? ` · ${candidate.album}` : ""}</small>
                    <small>{formatDuration(candidate.durationMilliseconds)}{candidate.candidateIsrc ? ` · ISRC ${candidate.candidateIsrc}` : ""}</small>
                    {#each Object.entries(candidate.providerTrackIds ?? {}) as [providerId, externalId]}
                      <small>{providerName(providerId)} · {externalId}</small>
                    {/each}
                  </div>
                  <span class="candidate-confidence">{percent(candidate.confidence)}</span>
                  <div class="score-components">
                    {#each scoreComponents(candidate) as [name, value]}
                      <span><small>{name.replaceAll("_", " ")}</small><strong>{percent(value)}</strong></span>
                    {/each}
                    {#if match.sourceArtworkUrl && candidate.backendItemId}
                      <ArtworkSimilarity
                        source={match.sourceArtworkUrl}
                        candidate={candidateArtwork(candidate.backendItemId)}
                      />
                    {/if}
                  </div>
                  <div class="candidate-reasons">
                    {#each [...(candidate.reasons ?? []), ...(candidate.warnings ?? [])].slice(0, 3) as reason}
                      <span>{reason.replaceAll("_", " ")}</span>
                    {/each}
                  </div>
                  {#if candidate.libraryTrackId}
                    <button type="button" disabled={saving} onclick={() => void chooseLocal(candidate.libraryTrackId!, "Selected from automatic candidate evidence")}>Choose candidate</button>
                  {/if}
                </article>
              {/each}
            </div>
          {:else}
            <p class="dialog-empty">No retained automatic candidates. Search the indexed library or every playable provider.</p>
          {/if}
        </section>

        <form class="target-search" onsubmit={(event) => { event.preventDefault(); void search(); }}>
          <label class="grow">
            <span>Search local library and playable providers</span>
            <input bind:value={targetQuery} required minlength="2" />
          </label>
          <button class="button-primary" type="submit" disabled={loading}>
            {loading ? "Searching…" : "Search"}
          </button>
        </form>

        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        {#if resultProviders.length}
          <div class="provider-result-summary" aria-label="Providers with results">
            {#each resultProviders as resultProvider}
              <button
                type="button"
                aria-pressed={providerFilter === resultProvider.providerId}
                onclick={() => {
                  providerFilter =
                    providerFilter === resultProvider.providerId ? "" : resultProvider.providerId;
                }}
              >
                <ProviderMark
                  id={resultProvider.providerId === "local" ? backend.toLowerCase() : resultProvider.providerId}
                  definition={provider(resultProvider.providerId)}
                />
                <span>{providerName(resultProvider.providerId)}</span>
                <strong>{resultProvider.count}</strong>
              </button>
            {/each}
          </div>
        {/if}
        <div class="target-results">
          {#each visibleResults as target}
            <button
              type="button"
              disabled={saving}
              onclick={() =>
                target.externalProvider
                  ? void chooseProvider(target)
                  : void chooseLocal(target.id, "Selected from indexed library search")}
            >
              <span class="media-art mapping-art">
                {#if target.artworkUrl}<img src={target.artworkUrl} alt="" loading="lazy" />{:else}
                  <ProviderMark id={target.externalProvider || backend.toLowerCase()} definition={provider(target.externalProvider || "")} label={providerName(target.externalProvider)} />
                {/if}
              </span>
              <span><strong>{target.title}</strong><small>{target.artist || "Unknown artist"}{target.album ? ` · ${target.album}` : ""}</small></span>
              <span class="target-meta">
                <strong>{formatDuration(target.durationMilliseconds)}</strong>
                <small>{target.externalProvider ? providerName(target.externalProvider) : backend}</small>
              </span>
              <span>{percent(target.confidence)}</span>
            </button>
          {:else}
            {#if searched && !loading}
              <div class="compact-empty"><strong>No matching candidates found</strong><p>Try a more exact artist and title.</p></div>
            {/if}
          {/each}
        </div>

        <footer>
          {#if showReject}
            <button class="button-danger" type="button" onclick={() => { open = false; onReject?.(match!); }}>Reject candidate</button>
          {/if}
          <Dialog.Close class="button-secondary">Cancel</Dialog.Close>
        </footer>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
