<script lang="ts">
  import { tick, type Snippet } from "svelte";
  import { MoreHorizontal } from "@lucide/svelte";
  import { matchReview, type MatchAuthorityScope, type MatchCandidate, type MatchReviewItem, type MatchTarget, type ProviderDefinition } from "$lib/api";
  import { allowedAuthorityScopes, canEditAuthorityScope, expectedAuthorityForScope } from "$lib/matching-api";
  import { candidateResolution, percent, playableProviderIds, providerResultCounts, rankedTargets, reportedAudio, reviewProviderIdentities, routingPreference, scoreComponents, targetRoutingPreference } from "$lib/mappings";
  import { candidateReason, comparisonRows, currentSong, evidenceSentence, formatConfidence, targetSong } from "$lib/matches-presentation";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";
  import { formatDuration } from "$lib/playlists";
  import { reportHref } from "$lib/support";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import SidePanel from "$lib/components/patterns/SidePanel.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import ItemRow from "$lib/components/patterns/ItemRow.svelte";
  import SplitButton from "$lib/components/patterns/SplitButton.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RecordingLyricsPicker from "$lib/components/RecordingLyricsPicker.svelte";
  import ArtworkSimilarity from "$lib/shelved/ArtworkSimilarity.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import SelectField from "$lib/components/SelectField.svelte";

  let { open = $bindable(false), match, providers, backend, onSaved, onReject, onRematch, onPrevious, onNext, authorities,
    acceptRequest = 0, lyricsRequest = 0, playlists = [] }: {
    open: boolean; match: MatchReviewItem | null; providers: ProviderDefinition[]; backend: string;
    onSaved: (message: string) => void | Promise<void>;
    onReject: (match: MatchReviewItem, scope: MatchAuthorityScope) => void;
    onRematch: (match: MatchReviewItem) => void; onPrevious?: () => void; onNext?: () => void;
    authorities?: Snippet; acceptRequest?: number; lyricsRequest?: number; playlists?: string[];
  } = $props();
  let prepared: MatchReviewItem | null = null;
  let query = $state("");
  let results = $state<MatchTarget[]>([]);
  let providerFilter = $state("");
  let searched = $state(false);
  let searching = $state(false);
  let saving = $state(false);
  let error = $state("");
  let preview = $state<MatchTarget | null>(null);
  let scope = $state("personal");
  let scopeOpen = $state(false);
  let showLyrics = $state(false);
  let searchOpen = $state(false);
  let searchField = $state<HTMLInputElement>();
  let previousAccept = 0;
  let previousLyrics = 0;
  let searchVersion = 0;
  const sourceDetails = $derived(match ? `${match.album || "Your playlist recording"} · ${formatDuration(match.durationMilliseconds)}${match.isExplicit == null ? "" : match.isExplicit ? " · Explicit" : " · Not explicit"}${playlists.length ? ` · in ${playlists.join(", ")}` : ""}` : "");
  const playable = $derived(playableProviderIds(providers));
  const provider = (id: string) => findProviderDefinition(providers, id);
  const name = (id?: string | null) => !id || id === "local" ? backend : providerDisplayName(providers, id);
  function fromCandidate(candidate: MatchCandidate): MatchTarget | null {
    if (!match) return null;
    const resolution = candidateResolution(candidate, match.providerId, playable);
    if (!resolution) return null;
    const id = resolution.targetType === "local" ? resolution.libraryTrackId : resolution.externalId;
    return { ...candidate, id, title: candidate.title || "Indexed track", isrc: candidate.candidateIsrc,
      externalProvider: resolution.targetType === "provider" ? resolution.externalProvider : null,
      externalId: resolution.targetType === "provider" ? resolution.externalId : null,
      artworkUrl: candidate.backendItemId ? `/api/admin/downloads/artwork/${encodeURIComponent(candidate.backendItemId)}` : null,
      reasons: candidate.reasons ?? [], warnings: candidate.warnings ?? [] };
  }
  const candidates = $derived(match?.candidates.map(fromCandidate).filter((item): item is MatchTarget => item != null) ?? []);
  const previewCandidate = $derived(match?.candidates.find(candidate => {
    const target = fromCandidate(candidate);
    return target && target.id === preview?.id && target.externalProvider === preview?.externalProvider;
  }));
  const current = $derived(match ? currentSong(match) : null);
  const better = $derived(candidates.filter((item, index) => !match?.candidates.filter(candidate => fromCandidate(candidate) != null)[index]?.isCurrent &&
    !(current && (item.externalProvider || "local") === current.providerId && (item.externalId || item.id) === current.identity)));
  const shownResults = $derived(providerFilter ? results.filter(item => (item.externalProvider || "local") === providerFilter) : results);
  const scopes = $derived(match ? allowedAuthorityScopes(match).map(id => ({ id, label: id === "personal" ? "For me" : "For the household", disabled: !canEditAuthorityScope(match, id) })) : []);
  const editable = $derived(Boolean(match && scopes.some(item => item.id === scope && !item.disabled)));
  const comparison = $derived(match ? comparisonRows(match, preview ? targetSong(preview) : null) : []);
  $effect(() => {
    if (!open) { prepared = null; return; }
    if (!match || prepared === match) return;
    prepared = match;
    searchVersion++;
    query = match.title || ""; results = []; searched = false; searching = false; error = ""; providerFilter = ""; showLyrics = false; searchOpen = false;
    scope = allowedAuthorityScopes(match).includes("household") ? "household" : "personal";
    const target = currentSong(match);
    const retained = target && candidates.find(item => item.id === target.identity && (item.externalProvider || "local") === target.providerId);
    preview = target ? { ...retained, ...target, id: target.identity, title: target.title || "", externalProvider: target.providerId === "local" ? null : target.providerId,
      externalId: target.providerId === "local" ? null : target.identity, confidence: match.confidence ?? retained?.confidence } : candidates[0] ?? null;
  });
  $effect(() => { if (acceptRequest !== previousAccept) { previousAccept = acceptRequest; if (open && preview) scopeOpen = true; } });
  $effect(() => { if (lyricsRequest !== previousLyrics) { previousLyrics = lyricsRequest; if (open) showLyrics = true; } });

  async function backToMatch() {
    showLyrics = false;
    await tick();
    document.querySelector<HTMLButtonElement>('.panel-controls [aria-label="More"]')?.focus();
  }
  async function toggleSearch() {
    searchOpen = !searchOpen;
    if (searchOpen) { await tick(); searchField?.focus(); }
  }
  async function search() {
    if (!match || query.trim().length < 2 || searching) return;
    const id = match.externalSnapshotId, term = query.trim(), version = ++searchVersion;
    searching = true; searched = true; error = "";
    try {
      let response;
      try { response = await matchReview.searchProviders(term, id, true); }
      catch { response = { ...await matchReview.searchLocal(term, id), partialSearch: true }; }
      if (version !== searchVersion) return;
      results = rankedTargets(response.tracks);
      if (response.partialSearch) error = "Some sources could not be searched. Available results are shown.";
    } catch (cause) { if (version === searchVersion) error = cause instanceof Error ? cause.message : "Search could not finish."; }
    finally { if (version === searchVersion) searching = false; }
  }
  async function choose() {
    if (!match || !preview || saving || !editable) return;
    const item = match, target = preview, authorityScope = scope as MatchAuthorityScope;
    saving = true; error = "";
    try {
      await matchReview.resolve(item.externalSnapshotId, {
        ...(target.externalProvider && target.externalId ? { targetType: "provider", externalProvider: target.externalProvider, externalId: target.externalId } as const
          : { targetType: "local", libraryTrackId: target.id } as const),
        reason: "Selected after comparing recording details", authorityScope, expectedAuthority: expectedAuthorityForScope(item, authorityScope),
      });
      scopeOpen = false; open = false;
      await onSaved(`${name(target.externalProvider)} version saved ${authorityScope === "personal" ? "for you" : "for the household"}.`);
    } catch (cause) { error = cause instanceof Error ? cause.message : "The version could not be saved."; }
    finally { saving = false; }
  }
</script>

<SidePanel bind:open title={match?.title || "Match details"} description={match?.artist || "Unknown artist"} {onPrevious} {onNext}>
  {#snippet artwork()}<MediaArtwork url={match?.sourceArtworkUrl || match?.artworkUrl} />{/snippet}
  {#snippet header()}
    {#if match}
      <span title={sourceDetails}>{sourceDetails}</span>
    {/if}
  {/snippet}
  {#snippet menu()}
    {#if match}
      <DropdownMenu.Root>
        <DropdownMenu.Trigger class={buttonVariants({ variant: "ghost", size: "icon" })} aria-label="More"><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
        <DropdownMenu.Portal><DropdownMenu.Content class="bits-menu" align="end" sideOffset={4}>
          <DropdownMenu.Item class="bits-menu-item" onSelect={() => onRematch(match!)}>Rematch</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" disabled={!match.canonicalRecordingId} onSelect={() => showLyrics = !showLyrics}>Wrong lyrics?</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" onSelect={() => { open = false; window.location.hash = reportHref({ externalSnapshotId: match!.externalSnapshotId }, "/library/matches"); }}>Report a problem</DropdownMenu.Item>
        </DropdownMenu.Content></DropdownMenu.Portal>
      </DropdownMenu.Root>
    {/if}
  {/snippet}
  {#if match}
    {#if showLyrics && match.canonicalRecordingId}
      <Button variant="text" onclick={() => void backToMatch()}>Back to match</Button>
      <RecordingLyricsPicker recordingId={match.canonicalRecordingId} {providers} allowHousehold={allowedAuthorityScopes(match).includes("household")} />
    {:else}
      <section class="comparison-section" aria-label="Recording comparison">
        <p class="preview-label">{preview ? `Previewing ${name(preview.externalProvider)}${preview.title?.trim() ? ` · ${preview.title}` : ""}` : "No current match"}</p>
        <table class="comparison"><thead><tr><th>Detail</th><th>Original<br /><small>Your playlist</small></th><th>Current<br /><small>Plays as</small></th><th>Verdict</th></tr></thead>
          <tbody>{#each comparison as row}<tr><th scope="row">{row.label}</th><td>{row.original}</td><td>{row.current}</td><td><span class:equal={row.verdict === "Same"}>{row.verdict}</span></td></tr>{/each}</tbody>
        </table>
      </section>
      <section class="choices" aria-labelledby="better-choices"><h3 id="better-choices" class="sec-label">Better choices</h3>
        {#each better as target}
          <div class="candidate-card">
            <ItemRow title={`${target.isBestVersion && target.externalProvider?.toLowerCase() === "artistgrid" ? "⭐ " : ""}${target.title}`} subtitle={`${name(target.externalProvider)} · ${target.album || "Album not provided"} · ${formatDuration(target.durationMilliseconds)}`}
              selected={preview?.id === target.id && preview?.externalProvider === target.externalProvider} onclick={() => preview = target}>
              {#snippet leading()}<ProviderMark id={target.externalProvider || backend.toLowerCase()} definition={provider(target.externalProvider || "local")} />{/snippet}
              {#snippet pill()}<StatePill tone={(target.confidence ?? 0) >= (match?.threshold ?? 1) ? "ok" : "warn"} text={formatConfidence(target.confidence)} />{/snippet}
            </ItemRow>
            <p class="candidate-reasons">{candidateReason(target)}</p>
            {#if target.isBestVersion && target.externalProvider?.toLowerCase() === "artistgrid"}<span class="sr-only" role="img" aria-label="Best version on ArtistGrid" title="Best version on ArtistGrid">⭐</span>{/if}
          </div>
        {:else}<EmptyState title="No other suggestions" description="Search for the version you want below." />{/each}
      </section>
      <section class="search-section" aria-labelledby="version-search">
        <Button variant="link" class="search-toggle" id="version-search" aria-expanded={searchOpen} aria-controls="version-search-fields" onclick={() => void toggleSearch()}>Search for another version…</Button>
        {#if searchOpen}<div id="version-search-fields">
          <form class="target-search" onsubmit={(event) => { event.preventDefault(); void search(); }}>
            <label class="field grow"><span class="sr-only">Search local library and playable providers</span><input bind:this={searchField} bind:value={query} required minlength="2" placeholder="Artist and title" /></label>
            <Button type="submit" disabled={searching}>{searching ? "Searching…" : "Search"}</Button>
          </form>
          <p class="search-help">Good matches of the same version follow your streaming priority, then the same album edition.</p>
          {#if searched && results.length}<div class="provider-filters" aria-label="Providers with results">
            {#each providerResultCounts(results) as item}<Button variant="text" aria-pressed={providerFilter === item.providerId} onclick={() => providerFilter = providerFilter === item.providerId ? "" : item.providerId}>{name(item.providerId)} {item.count}</Button>{/each}
          </div>{/if}
          <div class="target-results">
            {#each shownResults as target}
              <div class="search-result">
                <ItemRow title={`${target.isBestVersion && target.externalProvider?.toLowerCase() === "artistgrid" ? "⭐ " : ""}${target.title}`} subtitle={`${name(target.externalProvider)} · ${target.album || "Album not provided"} · ${formatDuration(target.durationMilliseconds)}`} selected={preview?.id === target.id && preview?.externalProvider === target.externalProvider} onclick={() => preview = target}>
                  {#snippet leading()}<ProviderMark id={target.externalProvider || backend.toLowerCase()} definition={provider(target.externalProvider || "local")} />{/snippet}
                  {#snippet pill()}<StatePill tone="info" text={formatConfidence(target.confidence)} />{/snippet}
                </ItemRow>
                {#if target.isBestVersion && target.externalProvider?.toLowerCase() === "artistgrid"}<span class="sr-only" role="img" aria-label="Best version on ArtistGrid" title="Best version on ArtistGrid">⭐</span>{/if}
                <p class="candidate-reasons">{candidateReason(target)}</p>
                <details class="target-evidence"><summary>Full scoring for {target.title}</summary><dl>
                  <div><dt>Result order</dt><dd>{results.indexOf(target) + 1}</dd></div>
                  <div><dt>Source ISRC</dt><dd>{match.isrc || "Not provided"}</dd></div><div><dt>ISRC</dt><dd>{target.isrc || "Not provided"}</dd></div>
                  <div><dt>Routing preference</dt><dd>{targetRoutingPreference(target) || "Not provided"}</dd></div>
                  <div><dt>Album edition</dt><dd>{target.matchesSourceAlbum == null ? "Not provided" : target.matchesSourceAlbum ? "Same album edition" : "Different album"}</dd></div>
                  <div><dt>Content</dt><dd>{target.isExplicit == null ? "Not provided" : target.isExplicit ? "Explicit" : "Not explicit"}</dd></div>
                  <div><dt>Reported audio</dt><dd>{reportedAudio(target.media)}; the delivered format can differ.</dd></div>
                  {#each scoreComponents(target) as [key, value]}<div><dt>{key}</dt><dd>{percent(value)}</dd></div>{/each}
                </dl></details>
              </div>
            {:else}{#if searched && !searching}<EmptyState title="No matching candidates found" description="Try a more exact artist and title." />{/if}{/each}
          </div>
        </div>{/if}
        {#if error}<p role="alert" class="notice-error">{error}</p>{/if}
      </section>
      {#if preview}<section class="evidence"><h3 class="sec-label">What we compared</h3><div class="evidence-chips">
        {#each [...preview.reasons ?? [], ...preview.warnings ?? []] as reason}<span>{evidenceSentence(reason)}</span>{/each}
        {#each scoreComponents(preview) as [key, value]}<span>{evidenceSentence(key)} {value === 1 ? "matches exactly" : `is ${percent(value)} similar`}</span>{/each}
      </div><details><summary>Full scoring</summary><dl>
        <div><dt>Candidate ID</dt><dd>{preview.externalId || preview.backendItemId || preview.id}</dd></div>
        <div><dt>Match score</dt><dd>{formatConfidence(preview.confidence)}</dd></div>
        <div><dt>Routing preference</dt><dd>{routingPreference(preview.components) || targetRoutingPreference(preview) || "Not provided"}</dd></div>
        {#if previewCandidate}
          <div><dt>Normalized source title</dt><dd>{previewCandidate.normalizedSourceTitle || "Not provided"}</dd></div>
          <div><dt>Normalized candidate title</dt><dd>{previewCandidate.normalizedCandidateTitle || "Not provided"}</dd></div>
          <div><dt>Source ISRC</dt><dd>{previewCandidate.sourceIsrc || match.isrc || "Not provided"}</dd></div>
          <div><dt>Candidate ISRC</dt><dd>{previewCandidate.candidateIsrc || "Not provided"}</dd></div>
          <div><dt>Artist overlap</dt><dd>{percent(previewCandidate.artistOverlap)}</dd></div>
          <div><dt>Album evidence</dt><dd>{percent(previewCandidate.albumEvidence)}</dd></div>
          <div><dt>Duration difference</dt><dd>{previewCandidate.durationDeltaMilliseconds == null ? "Not provided" : `${previewCandidate.durationDeltaMilliseconds} ms`}</dd></div>
          {#each Object.entries(previewCandidate.providerTrackIds ?? {}) as [id, trackId]}<div><dt>{name(id)} track ID</dt><dd>{trackId}</dd></div>{/each}
        {/if}{#each scoreComponents(preview) as [key, value]}<div><dt>{key}</dt><dd>{percent(value)}</dd></div>{/each}
        {#each [...preview.reasons ?? [], ...preview.warnings ?? []] as reason}<div><dt>Evidence</dt><dd>{reason}</dd></div>{/each}
      </dl>{#if match.sourceArtworkUrl && preview.artworkUrl}<ArtworkSimilarity source={match.sourceArtworkUrl} candidate={preview.artworkUrl} />{/if}</details></section>{/if}
      {@render authorities?.()}
      <details class="identity-details"><summary>Database and identity details</summary><dl>
        <div><dt>Source snapshot</dt><dd>{match.externalSnapshotId}</dd></div><div><dt>Source provider</dt><dd>{match.providerId}</dd></div>
        <div><dt>Canonical recording</dt><dd>{match.canonicalRecordingId || "Not provided"}</dd></div><div><dt>Library track</dt><dd>{match.libraryTrackId || "Not provided"}</dd></div>
        <div><dt>Algorithm</dt><dd>{match.algorithmVersion || "Not provided"}</dd></div>
        {#each reviewProviderIdentities(match) as identity}<div><dt>{name(identity.providerId)}</dt><dd>{identity.externalId} · {identity.verification}</dd></div>{/each}
      </dl></details>
    {/if}
  {/if}
  {#snippet footer()}
    {#if match && !showLyrics}<div class="decision-footer">
      <Button variant="text" class="nothing-fits" disabled={!editable || saving} onclick={() => onReject(match!, scope as MatchAuthorityScope)}>Nothing fits</Button>
      <SplitButton label={`Use ${name(preview?.externalProvider)} version`} options={scopes} bind:value={scope} disabled={!preview || !editable || saving} onclick={() => void choose()} />
    </div>{/if}
  {/snippet}
</SidePanel>
<ConfirmDialog bind:open={scopeOpen} title="Use this version?" description="Choose who this recording should play for." confirmLabel="Use version" confirmVariant="default" disabled={!editable || saving} closeOnConfirm={false} {error} onConfirm={choose}>
  <SelectField bind:value={scope} label="Save version for" options={scopes.map(item => ({ value: item.id, label: item.label, disabled: item.disabled }))} />
</ConfirmDialog>

<style>
  section { margin-bottom: var(--space-3); min-width: 0; }
  .preview-label, .candidate-reasons { color: var(--color-ink-muted); font-size: .8125rem; margin: var(--space-2) 0; }
  .comparison { width: 100%; border-collapse: collapse; table-layout: fixed; font-size: .75rem; }
  .comparison th, .comparison td { text-align: left; padding: var(--space-2) var(--space-1); border-bottom: 1px solid var(--color-edge); overflow-wrap: anywhere; vertical-align: top; }
  .comparison th { font-weight: 500; color: var(--color-ink-muted); }
  .comparison th:first-child { width: 16%; }.comparison th:last-child { width: 20%; }
  .equal { color: var(--success); }
  .candidate-card, .search-result { border-bottom: 1px solid var(--color-edge); padding-block: var(--space-2); }
  .candidate-reasons { padding-inline: var(--space-3); }
  .search-section :global(.search-toggle) { height: auto; min-height: var(--touch-target-min, 32px); padding: 0; color: var(--color-signal-text); font-size: .8125rem; font-weight: 500; }
  .target-search { display: flex; flex-direction: row; align-items: center; gap: var(--space-2); margin-top: var(--space-2); }.grow { min-width: 0; flex: 1; }.grow input { width: 100%; }
  .search-help { margin: var(--space-2) 0 0; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .provider-filters, .evidence-chips { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .evidence-chips span { border-radius: var(--radius-sm); padding: var(--space-1) var(--space-2); background: var(--color-panel-raised); font-size: .75rem; }
  details { margin-block: var(--space-3); font-size: .8125rem; } summary { cursor: pointer; min-height: 32px; align-content: center; color: var(--color-ink-muted); }
  dl { margin: 0; } dl > div { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.5fr); gap: var(--space-2); padding-block: var(--space-1); }
  dt { color: var(--color-ink-muted); } dd { margin: 0; overflow-wrap: anywhere; }
  .decision-footer { width: 100%; min-width: 0; display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); }
  .decision-footer :global(.nothing-fits) { flex: 0 0 auto; padding-inline: var(--space-2); color: var(--danger); font-size: .75rem; }
  .decision-footer :global(.split-button) { min-width: 0; margin-left: auto; }
  .decision-footer :global(.split-primary), .decision-footer :global(.split-trigger) { padding-inline: var(--space-2); font-size: .75rem; }
</style>
