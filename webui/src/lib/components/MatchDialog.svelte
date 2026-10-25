<script lang="ts">
  import { adminUrl } from "$lib/admin-url";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { X } from "@lucide/svelte";
  import {
    matchReview,
    type MatchAuthorityScope,
    type MatchReviewItem,
    type MatchTarget,
    type ProviderDefinition,
  } from "$lib/api";
  import RecordingLyricsPicker from "$lib/components/RecordingLyricsPicker.svelte";
  import ArtworkSimilarity from "$lib/shelved/ArtworkSimilarity.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import {
    candidateResolution,
    matchScore,
    percent,
    playableProviderIds,
    providerResultCounts,
    rankedTargets,
    reportedAudio,
    routingPreference,
    scoreComponents,
    targetRoutingPreference,
  } from "$lib/mappings";
  import {
    allowedAuthorityScopes,
    canEditAuthorityScope,
    expectedAuthorityForScope,
  } from "$lib/matching-api";
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
    onReject?: (match: MatchReviewItem, authorityScope: MatchAuthorityScope) => void;
  } = $props();

  let preparedId = $state("");
  let targetQuery = $state("");
  let providerFilter = $state("");
  let results = $state<MatchTarget[]>([]);
  let searched = $state(false);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");
  let showLyrics = $state(false);
  let authorityScope = $state<MatchAuthorityScope>("personal");

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
  const selectedScopeEditable = $derived(
    match ? canEditAuthorityScope(match, authorityScope) : false,
  );

  $effect(() => {
    if (!open) {
      preparedId = "";
      return;
    }
    if (!match || preparedId === match.externalSnapshotId) return;
    preparedId = match.externalSnapshotId;
    showLyrics = false;
    targetQuery = match.title || "";
    providerFilter = "";
    results = [];
    searched = false;
    error = "";
    authorityScope = "personal";
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

  const evidenceLabels: Record<string, string> = {
    "ArtistGrid best version +5": "ArtistGrid best version +5",
    title_case_folded: "Title matches ignoring case",
    title_normalized: "Title matches after normalization",
    title_decorators_ignored: "Title matches without release labels",
    title_fuzzy: "Fuzzy title comparison",
    semantic_version_mismatch: "Different recording version",
    semantic_version_mismatch_review: "Different version needs review",
    limited_recording_metadata: "Missing duration and matching album evidence limit this score",
  };

  function confidenceLevel(value?: number | null) {
    const score = value ?? 0;
    return score >= 0.9 ? "high" : score >= 0.75 ? "medium" : "low";
  }

  function evidenceLabel(value: string) {
    return evidenceLabels[value] ?? value.replace(/([a-z])([A-Z])/g, "$1 $2").replaceAll("_", " ");
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
          match.externalSnapshotId,
        ),
        matchReview.searchProviders(
          targetQuery.trim(),
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
    if (!match || saving || !selectedScopeEditable) return;
    saving = true;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        targetType: "local",
        libraryTrackId,
        reason,
        authorityScope,
        expectedAuthority: expectedAuthorityForScope(match, authorityScope),
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
    if (!match || !target.externalProvider || !target.externalId || saving || !selectedScopeEditable) return;
    saving = true;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        targetType: "provider",
        externalProvider: target.externalProvider,
        externalId: target.externalId,
        reason: "Selected from the provider-neutral match dialog",
        authorityScope,
        expectedAuthority: expectedAuthorityForScope(match, authorityScope),
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
        <header class="match-header">
          <span class="media-art mapping-art match-source-art">
            {#if match.sourceArtworkUrl}<img src={adminUrl(match.sourceArtworkUrl)} alt="" />{:else}<ProviderMark id={match.providerId} definition={provider(match.providerId)} />{/if}
          </span>
          <div class="match-header-copy">
            <Dialog.Title>{match.title || "Unknown track"}</Dialog.Title>
            <Dialog.Description>{match.artist || "Unknown artist"} · {providerName(match.providerId)}</Dialog.Description>
            <p class="match-source-facts">
              {#if match.album}<span>{match.album}</span>{/if}
              <span>{formatDuration(match.durationMilliseconds)}</span>
              {#if match.isrc}<span>ISRC {match.isrc}</span>{/if}
              {#if match.isExplicit != null}<span>{match.isExplicit ? "Explicit" : "Not explicit"}</span>{/if}
            </p>
          </div>
          <Dialog.Close class="icon-button" aria-label="Close match dialog"><X size={18} aria-hidden="true" /></Dialog.Close>
        </header>

        {#if showLyrics && match.canonicalRecordingId}
          <div class="match-section lyrics-review">
            <RecordingLyricsPicker recordingId={match.canonicalRecordingId} {providers} allowHousehold={allowedAuthorityScopes(match).includes("household")} />
          </div>
        {:else}
        {#if allowedAuthorityScopes(match).includes("household")}
          <fieldset class="match-section authority-scope-picker" disabled={saving}>
            <legend>Save decision for</legend>
            <div class="authority-scope-options">
              <label class:active={authorityScope === "personal"}>
                <input bind:group={authorityScope} type="radio" value="personal" disabled={!canEditAuthorityScope(match, "personal")} />
                <span><strong>Only me</strong><small>Overrides the household decision for your account.</small></span>
              </label>
              <label class:active={authorityScope === "household"}>
                <input bind:group={authorityScope} type="radio" value="household" disabled={!canEditAuthorityScope(match, "household")} />
                <span><strong>Household</strong><small>Applies when a listener has no personal decision.</small></span>
              </label>
            </div>
          </fieldset>
        {/if}

        <section class="match-section automatic-candidates" aria-labelledby="suggested-matches-heading">
          <div class="match-section-heading">
            <h3 id="suggested-matches-heading">Suggested matches</h3>
            <span class="match-count">{eligibleCandidates.length}</span>
          </div>
          {#if eligibleCandidates.length}
            <div class="candidate-list">
              {#each eligibleCandidates as candidate}
                {@const resolution = candidateResolution(candidate, match.providerId, playbackProviders)}
                <article class="candidate-card">
                  <div class="match-row">
                    <MediaArtwork class="mapping-art" url={candidateArtwork(candidate)} fallback="♫" />
                    <div class="candidate-copy">
                      <strong>{#if candidate.isBestVersion && candidateProvider(candidate).toLowerCase() === "artistgrid"}<span role="img" aria-label="Best version on ArtistGrid" title="Best version on ArtistGrid">⭐</span> {/if}{candidate.title || candidate.backendItemId || "Indexed track"}</strong>
                      <small>{candidate.artist || "Unknown artist"}{candidate.album ? ` · ${candidate.album}` : ""}</small>
                      <small>{formatDuration(candidate.durationMilliseconds)}{candidate.candidateIsrc ? ` · ISRC ${candidate.candidateIsrc}` : ""}</small>
                      <span class="candidate-provider">
                        <ProviderMark
                          id={candidateProvider(candidate) || backend.toLowerCase()}
                          definition={provider(candidateProvider(candidate))}
                        />
                        {providerName(candidateProvider(candidate))}
                        {#if candidate.isCurrent}<span>· Current match</span>{/if}
                        {#if routingPreference(candidate.components)}
                          <span>· {routingPreference(candidate.components)}</span>
                        {/if}
                      </span>
                    </div>
                    <span class="candidate-confidence match-confidence" style={`--confidence:${Math.round((candidate.confidence ?? 0) * 100)}%`} data-level={confidenceLevel(candidate.confidence)}>
                      <strong>{matchScore(candidate.confidence)}</strong>
                      <span class="confidence-meter" aria-hidden="true"><span></span></span>
                      <small>match score</small>
                    </span>
                    {#if resolution?.targetType === "local"}
                      <Button class="candidate-action" variant="secondary" size="sm" disabled={saving || !selectedScopeEditable} onclick={() => void chooseLocal(resolution.libraryTrackId, "Selected from automatic candidate evidence")}>Choose candidate</Button>
                    {:else if resolution}
                      <Button class="candidate-action" variant="secondary" size="sm" disabled={saving || !selectedScopeEditable} onclick={() => void chooseProvider({
                        id: resolution.externalId,
                        externalId: resolution.externalId,
                        externalProvider: resolution.externalProvider,
                        title: candidate.title || candidate.backendItemId || "Provider track",
                      })}>Choose candidate</Button>
                    {/if}
                  </div>
                  <div class="candidate-detail">
                    <div class="score-components">
                      {#each scoreComponents(candidate) as [name, value]}
                        {#if name !== "priorityWindow"}
                          <span><small>{name.replaceAll("_", " ")}</small><strong>{percent(value)}</strong></span>
                        {/if}
                      {/each}
                      {#if match.sourceArtworkUrl && candidate.backendItemId}
                        <ArtworkSimilarity source={match.sourceArtworkUrl} candidate={candidateArtwork(candidate)} />
                      {/if}
                    </div>
                    {#if candidate.reasons?.length || candidate.warnings?.length}
                      <div class="candidate-reasons">
                        {#each candidate.reasons ?? [] as reason}<span>{evidenceLabel(reason)}</span>{/each}
                        {#each candidate.warnings ?? [] as warning}<span class="warning">{evidenceLabel(warning)}</span>{/each}
                      </div>
                    {/if}
                    <details class="candidate-evidence">
                      <summary>Full scoring evidence</summary>
                      <dl>
                        <div><dt>Candidate ID</dt><dd>{candidate.libraryTrackId || candidate.backendItemId || candidateExternalId(candidate) || "—"}</dd></div>
                        <div><dt>Match score</dt><dd>{matchScore(candidate.confidence)}</dd></div>
                        {#if routingPreference(candidate.components)}<div><dt>Routing preference</dt><dd>{routingPreference(candidate.components)}</dd></div>{/if}
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
                  </div>
                </article>
              {/each}
            </div>
          {:else}
            <p class="match-empty">No suggestions were kept for this song. Search below to pick one yourself.</p>
          {/if}
        </section>

        <section class="match-section match-search" aria-labelledby="find-match-heading">
          <div class="match-section-heading">
            <h3 id="find-match-heading">Find a different match</h3>
          </div>
          <p class="setting-help">Match scores compare recording details. Equal scores use streaming priority, then the same album edition. Artwork and reported audio do not change the score.</p>
          <form class="target-search" onsubmit={(event) => { event.preventDefault(); void search(); }}>
            <label class="field grow">
              <span class="sr-only">Search local library and playable providers</span>
              <input bind:value={targetQuery} required minlength="2" placeholder="Artist and title" />
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
                class="match-row"
                disabled={saving || !selectedScopeEditable}
                onclick={() =>
                  target.externalProvider
                    ? void chooseProvider(target)
                    : void chooseLocal(target.id, "Selected from indexed library search")}
              >
                <MediaArtwork class="mapping-art" url={target.artworkUrl} />
                <span class="target-copy">
                  <strong>{#if target.isBestVersion && target.externalProvider?.toLowerCase() === "artistgrid"}<span role="img" aria-label="Best version on ArtistGrid" title="Best version on ArtistGrid">⭐</span> {/if}{target.title}</strong>
                  <small>{target.artist || "Unknown artist"} · {target.album || "Unknown album"}</small>
                  <span class="candidate-provider">
                    <ProviderMark
                      id={target.externalProvider || backend.toLowerCase()}
                      definition={provider(target.externalProvider || "")}
                    />
                    {providerName(target.externalProvider)}
                    {#if targetRoutingPreference(target)}
                      <span>· {targetRoutingPreference(target)}</span>
                    {/if}
                  </span>
                  {#if target.isExplicit != null || target.matchesSourceAlbum}
                    <small>{target.isExplicit == null ? "" : target.isExplicit ? "Explicit" : "Not explicit"}{target.isExplicit != null && target.matchesSourceAlbum ? " · " : ""}{target.matchesSourceAlbum ? "Same album edition" : ""}</small>
                  {/if}
                  {#if target.warnings?.includes("limited_recording_metadata")}<small>Limited recording metadata</small>{/if}
                  <small title={reportedAudio(target.media)}>{target.media ? "Reported audio · " : ""}{reportedAudio(target.media)}</small>
                </span>
                <span class="target-meta"><strong>{formatDuration(target.durationMilliseconds)}</strong></span>
                <span class="target-score match-confidence" style={`--confidence:${Math.round((target.confidence ?? 0) * 100)}%`} data-level={confidenceLevel(target.confidence)}>
                  <strong>{matchScore(target.confidence)}</strong>
                  <span class="confidence-meter" aria-hidden="true"><span></span></span>
                  <small>result #{results.indexOf(target) + 1}</small>
                </span>
              </button>
              <details class="target-evidence">
                <summary>Evidence for {target.title}</summary>
                <dl>
                  <div><dt>Result order</dt><dd>#{results.indexOf(target) + 1}</dd></div>
                  <div><dt>Candidate ID</dt><dd>{target.externalId || target.backendItemId || target.id}</dd></div>
                  <div><dt>Match score</dt><dd>{matchScore(target.confidence)}</dd></div>
                  <div><dt>Source ISRC</dt><dd>{match.isrc || "Not provided"}</dd></div>
                  {#if target.isrc}<div><dt>ISRC</dt><dd>{target.isrc}</dd></div>{/if}
                  {#if targetRoutingPreference(target)}<div><dt>Routing preference</dt><dd>{targetRoutingPreference(target)}</dd></div>{/if}
                  {#if target.isExplicit != null}<div><dt>Explicit content</dt><dd>{target.isExplicit ? "Explicit" : "Not explicit"}</dd></div>{/if}
                  {#if target.matchesSourceAlbum != null}<div><dt>Same album edition</dt><dd>{target.matchesSourceAlbum ? "Yes" : "No"}</dd></div>{/if}
                  <div><dt>Reported audio</dt><dd>{reportedAudio(target.media)}. Available metadata for this result; the delivered format can differ.</dd></div>
                  {#each scoreComponents({ components: target.components }) as [name, value]}
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
        </section>

        <details class="match-section match-technical">
          <summary>Database and identity details</summary>
          <dl>
            <div><dt>Source snapshot</dt><dd>{match.externalSnapshotId}</dd></div>
            <div><dt>Source provider</dt><dd>{match.providerId}</dd></div>
            {#if match.canonicalRecordingId}<div><dt>Canonical recording</dt><dd>{match.canonicalRecordingId}</dd></div>{/if}
            {#if match.libraryTrackId}<div><dt>Library track</dt><dd>{match.libraryTrackId}</dd></div>{/if}
            {#if match.algorithmVersion}<div><dt>Algorithm</dt><dd>{match.algorithmVersion}</dd></div>{/if}
            {#each match.providerIdentities as identity}
              <div><dt>{providerName(identity.providerId)}</dt><dd>{identity.externalId} · {identity.verification}</dd></div>
            {/each}
          </dl>
        </details>
        {/if}

        <footer class="dialog-actions match-actions">
          <span class="match-lyrics-action">
            <Button variant="outline" disabled={!match.canonicalRecordingId} aria-expanded={showLyrics}
              aria-describedby={match.canonicalRecordingId ? undefined : "match-lyrics-help"}
              onclick={() => showLyrics = !showLyrics}>{showLyrics ? "Back to match" : "Wrong lyrics?"}</Button>
            {#if !match.canonicalRecordingId}<small id="match-lyrics-help">Play or match this song, then reload its details to choose lyrics.</small>{/if}
          </span>
          {#if showLyrics}
            <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Done</Dialog.Close>
          {:else}
            {#if showReject}
              <Button variant="destructive" disabled={!selectedScopeEditable} onclick={() => onReject?.(match!, authorityScope)}>Reject candidate</Button>
            {/if}
            <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
          {/if}
        </footer>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

