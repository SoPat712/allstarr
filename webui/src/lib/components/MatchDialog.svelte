<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { X } from "@lucide/svelte";
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
    candidateResolution,
    percent,
    playableProviderIds,
    providerResultCounts,
    rankedTargets,
    scoreComponents,
  } from "$lib/mappings";
  import { formatDuration } from "$lib/playlists";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

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

  const playbackProviders = $derived(playableProviderIds(providers));
  const eligibleCandidates = $derived(
    match?.candidates.filter((candidate) =>
      candidateResolution(candidate, match.providerId, playbackProviders)) ?? [],
  );
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
    targetQuery = match.searchQuery || match.title || "";
    providerFilter = "";
    results = [];
    searched = false;
    error = "";
    if (autoSearch) void search();
  });

  const provider = (providerId: string) => findProviderDefinition(providers, providerId);

  function providerName(providerId?: string | null) {
    if (!providerId || providerId === "local")
      return backend.toLowerCase() === "jellyfin" ? "Jellyfin" : backend;
    return providerDisplayName(providers, providerId);
  }

  function candidateProvider(candidate: MatchReviewItem["candidates"][number]) {
    const resolution = match
      ? candidateResolution(candidate, match.providerId, playbackProviders)
      : null;
    return resolution?.targetType === "provider" ? resolution.externalProvider : "";
  }

  function candidateExternalId(candidate: MatchReviewItem["candidates"][number]) {
    const providerId = candidateProvider(candidate);
    return providerId ? candidate.providerTrackIds?.[providerId] : null;
  }

  function candidateArtwork(candidate: MatchReviewItem["candidates"][number]) {
    const providerId = candidateProvider(candidate);
    const externalId = candidateExternalId(candidate);
    const itemId = providerId && externalId
      ? `ext-${providerId}-song-${externalId}`
      : candidate.backendItemId;
    return itemId
      ? `/api/admin/downloads/artwork/${encodeURIComponent(itemId)}`
      : "";
  }

  function evidenceLabel(value: string) {
    return value.replace(/([a-z])([A-Z])/g, "$1 $2").replaceAll("_", " ");
  }

  function candidateFacts(candidate: MatchReviewItem["candidates"][number]) {
    const facts: Array<readonly [string, string | null | undefined]> = [
      ["Normalized source title", candidate.normalizedSourceTitle],
      ["Normalized candidate title", candidate.normalizedCandidateTitle],
      ["Source ISRC", candidate.sourceIsrc],
      ["Candidate ISRC", candidate.candidateIsrc],
      ["Artist overlap", candidate.artistOverlap == null ? null : percent(candidate.artistOverlap)],
      ["Album evidence", candidate.albumEvidence == null ? null : percent(candidate.albumEvidence)],
      ["Duration difference", candidate.durationDeltaMilliseconds == null ? null : `${Math.round(candidate.durationDeltaMilliseconds)} ms`],
      ...Object.entries(candidate.providerTrackIds ?? {}).map(([providerId, trackId]) =>
        [`${providerName(providerId)} track ID`, trackId] as const),
    ];
    return facts.filter((fact) => fact[1]);
  }

  async function search() {
    if (!match || targetQuery.trim().length < 2 || loading) return;
    loading = true;
    searched = true;
    error = "";
    try {
      const [local, external] = await Promise.allSettled([
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
      results = rankedTargets([
        ...(local.status === "fulfilled" ? local.value.tracks : []),
        ...(external.status === "fulfilled" ? external.value.tracks : []),
      ]);
      if (local.status === "rejected" || external.status === "rejected")
        error = "Some providers could not be searched. Available results are shown.";
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
    <Dialog.Content class="match-dialog" preventScroll={false}>
      {#if match}
        <header>
          <div>
            <p class="eyebrow">Review match</p>
            <Dialog.Title>{match.title || "Unknown track"}</Dialog.Title>
            <Dialog.Description>{match.artist || "Unknown artist"} · {providerName(match.providerId)}</Dialog.Description>
          </div>
          <Dialog.Close class="icon-button" aria-label="Close match dialog"><X size={18} aria-hidden="true" /></Dialog.Close>
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
            <span>{eligibleCandidates.length}</span>
          </div>
          {#if eligibleCandidates.length}
            <div class="candidate-list">
              {#each eligibleCandidates as candidate}
                {@const resolution = candidateResolution(candidate, match.providerId, playbackProviders)}
                <article class="candidate-card">
                  <MediaArtwork
                    class="mapping-art"
                    url={candidateArtwork(candidate)}
                    fallback="♫"
                  />
                  <div class="candidate-copy">
                    <strong>{candidate.title || candidate.backendItemId || "Indexed track"}</strong>
                    <small>{candidate.artist || "Unknown artist"}{candidate.album ? ` · ${candidate.album}` : ""}</small>
                    <small>{formatDuration(candidate.durationMilliseconds)}{candidate.candidateIsrc ? ` · ISRC ${candidate.candidateIsrc}` : ""}</small>
                    <span class="candidate-provider">
                      <ProviderMark
                        id={candidateProvider(candidate) || backend.toLowerCase()}
                        definition={provider(candidateProvider(candidate))}
                      />
                      {providerName(candidateProvider(candidate))}
                      {#if resolution?.targetType === "local" && candidate.components?.localPreference}
                        <span>· +{percent(candidate.components.localPreference)} local boost</span>
                      {:else if candidate.components?.extensionPenalty}
                        <span>· {percent(candidate.components.extensionPenalty)} extension penalty</span>
                      {/if}
                    </span>
                  </div>
                  <span
                    class="candidate-confidence"
                  >
                    <strong>{percent(
                      candidate.components?.preferenceScore ?? candidate.confidence
                    )}</strong>
                    <small>confidence</small>
                  </span>
                  <div class="score-components">
                    {#each scoreComponents(candidate) as [name, value]}
                      {#if name !== "localPreference" && name !== "extensionPenalty" && name !== "preferenceScore"}
                        <span>
                          <small>{name.replaceAll("_", " ")}</small>
                          <strong>{percent(value)}</strong>
                        </span>
                      {/if}
                    {/each}
                    {#if match.sourceArtworkUrl && candidate.backendItemId}
                      <ArtworkSimilarity
                        source={match.sourceArtworkUrl}
                        candidate={candidateArtwork(candidate)}
                      />
                    {/if}
                  </div>
                  <div class="candidate-reasons">
                    {#each [...(candidate.reasons ?? []), ...(candidate.warnings ?? [])] as reason}
                      <span>{evidenceLabel(reason)}</span>
                    {/each}
                  </div>
                  <details class="candidate-evidence">
                    <summary>Full evidence</summary>
                    <dl>
                      <div><dt>Candidate ID</dt><dd>{candidate.libraryTrackId || candidate.backendItemId || candidateExternalId(candidate) || "—"}</dd></div>
                      <div><dt>Raw confidence</dt><dd>{percent(candidate.confidence)}</dd></div>
                      {#each candidateFacts(candidate) as [label, value]}
                        <div><dt>{label}</dt><dd>{value}</dd></div>
                      {/each}
                      {#each scoreComponents(candidate) as [name, value]}
                        <div><dt>{evidenceLabel(name)}</dt><dd>{percent(value)}</dd></div>
                      {/each}
                      {#each candidate.reasons ?? [] as reason}<div><dt>Reason</dt><dd>{evidenceLabel(reason)}</dd></div>{/each}
                      {#each candidate.warnings ?? [] as warning}<div><dt>Warning</dt><dd>{evidenceLabel(warning)}</dd></div>{/each}
                    </dl>
                  </details>
                  {#if resolution?.targetType === "local"}
                    <Button class="candidate-action" variant="secondary" size="sm" disabled={saving} onclick={() => void chooseLocal(resolution.libraryTrackId, "Selected from automatic candidate evidence")}>Choose candidate</Button>
                  {:else if resolution}
                    <Button class="candidate-action" variant="secondary" size="sm" disabled={saving} onclick={() => void chooseProvider({
                      id: resolution.externalId,
                      externalId: resolution.externalId,
                      externalProvider: resolution.externalProvider,
                      title: candidate.title || candidate.backendItemId || "Provider track",
                    })}>Choose candidate</Button>
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
          <Button type="submit" disabled={loading}>
            {loading ? "Searching…" : "Search"}
          </Button>
        </form>

        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        {#if searched && !loading}
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
              <MediaArtwork class="mapping-art" url={target.artworkUrl} />
              <span class="target-copy">
                <span class="candidate-provider">
                  <ProviderMark
                    id={target.externalProvider || backend.toLowerCase()}
                    definition={provider(target.externalProvider || "")}
                  />
                  {providerName(target.externalProvider)}
                  {#if !target.externalProvider && target.components?.localPreference}
                    <span>· +{percent(target.components.localPreference)} local boost</span>
                  {:else if target.components?.extensionPenalty}
                    <span>· {percent(target.components.extensionPenalty)} extension penalty</span>
                  {/if}
                </span>
                <strong>{target.title}</strong>
                <small>{target.artist || "Unknown artist"}</small>
                <small>{target.album || "Unknown album"}</small>
              </span>
              <span class="target-meta">
                <strong>{formatDuration(target.durationMilliseconds)}</strong>
                <small>{target.externalId || target.backendItemId || target.id}</small>
              </span>
              <span class="target-score">
                <strong>{percent(
                  target.components?.preferenceScore ?? target.confidence
                )}</strong>
                <small>confidence</small>
                <small>rank #{results.indexOf(target) + 1}</small>
              </span>
            </button>
            <details class="target-evidence">
              <summary>Evidence for {target.title}</summary>
              <dl>
                <div><dt>Rank</dt><dd>#{results.indexOf(target) + 1}</dd></div>
                <div><dt>Candidate ID</dt><dd>{target.externalId || target.backendItemId || target.id}</dd></div>
                <div><dt>Raw confidence</dt><dd>{percent(target.confidence)}</dd></div>
                {#if target.isrc}<div><dt>ISRC</dt><dd>{target.isrc}</dd></div>{/if}
                {#each Object.entries(target.components ?? {}) as [name, value]}
                  <div><dt>{evidenceLabel(name)}</dt><dd>{percent(value)}</dd></div>
                {/each}
                {#each target.reasons ?? [] as reason}<div><dt>Reason</dt><dd>{evidenceLabel(reason)}</dd></div>{/each}
                {#each target.warnings ?? [] as warning}<div><dt>Warning</dt><dd>{evidenceLabel(warning)}</dd></div>{/each}
              </dl>
            </details>
          {:else}
            {#if searched && !loading}
              <div class="compact-empty"><strong>No matching candidates found</strong><p>Try a more exact artist and title.</p></div>
            {/if}
          {/each}
        </div>

        <footer>
          {#if showReject}
            <Button variant="destructive" onclick={() => onReject?.(match!)}>Reject candidate</Button>
          {/if}
          <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
        </footer>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
