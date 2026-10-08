<script lang="ts">
  import DisclosureLabel from "$lib/components/DisclosureLabel.svelte";
  import { onMount } from "svelte";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import {
    ArrowRight,
    Ban,
    CircleQuestionMark,
    Clock3,
    Hand,
    List,
    MoreHorizontal,
    ScanSearch,
    Sparkles,
  } from "@lucide/svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    home,
    matchReview,
    type ManualMatchAuthority,
    type MatchAuthorityScope,
    type MatchReviewItem,
    type MatchReviewResponse,
    type ProviderDefinition,
  } from "$lib/api";
  import {
    authorityForScope,
    authorityScopeOf,
    canEditAuthority,
    canEditAuthorityScope,
    expectedAuthorityForScope,
    manualMatchAuthority,
  } from "$lib/matching-api";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import BulkRematchDialog from "$lib/components/BulkRematchDialog.svelte";
  import ArtworkSimilarity from "$lib/shelved/ArtworkSimilarity.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import {
    candidateResolution,
    currentTarget,
    isAttention,
    playableProviderIds,
    percent,
    reviewStateLabel,
    scoreComponents,
  } from "$lib/mappings";
  import { formatDuration } from "$lib/playlists";
  import { createRefreshScheduler, liveUpdates } from "$lib/live-updates.svelte";
  import { relativeTime } from "$lib/activity";
  import { findProviderDefinition, providerDisplayName } from "$lib/sources";

  type DestructiveAction =
    | { kind: "reject"; match: MatchReviewItem; authorityScope: MatchAuthorityScope }
    | {
        kind: "manual_delete" | "manual_rematch";
        match: MatchReviewItem;
        authority: ManualMatchAuthority;
      };
  type MappingView =
    | "all"
    | "manual"
    | "rejected"
    | "automatic"
    | "tentative"
    | "review"
    | "unresolved";

  const viewFilters: Record<MappingView, string> = {
    all: "",
    manual: "manual_matches",
    rejected: "manual_rejections",
    automatic: "automatic",
    tentative: "suggested",
    review: "ambiguous",
    unresolved: "unresolved",
  };

  let { initialSearch = "", initialReview = "" }: { initialSearch?: string; initialReview?: string } = $props();

  let data = $state<MatchReviewResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let backend = $state("Local library");
  let stateFilter = $state(viewFilters.review);
  let view = $state<MappingView>("review");
  let searchInput = $state("");
  let search = $state("");
  let libraryScopeId = $state("");
  let sort = $state("");
  let page = $state(1);
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let degraded = $state("");
  const playbackProviders = $derived(playableProviderIds(providers));
  let feedback = $state("");
  let action = $state("");
  let loadVersion = 0;

  let dialogOpen = $state(false);
  let initialReviewOpened = $state(false);
  let selected = $state<MatchReviewItem | null>(null);
  let destructiveOpen = $state(false);
  let destructive = $state<DestructiveAction | null>(null);
  let rematchAllOpen = $state(false);

  const provider = (providerId: string) => findProviderDefinition(providers, providerId);

  function providerName(providerId?: string | null) {
    if (!providerId) return "Unresolved";
    if (providerId === "local") return backend;
    return providerDisplayName(providers, providerId);
  }

  async function load() {
    const version = ++loadVersion;
    refreshing = true;
    error = "";
    try {
      const response = await matchReview.list({
        page,
        pageSize: 50,
        search,
        state: stateFilter,
        sort,
        libraryScopeId,
      });
      if (version !== loadVersion) return;
      data = response;
    } catch (cause) {
      if (version !== loadVersion) return;
      error = cause instanceof Error ? cause.message : "Match review is unavailable.";
    } finally {
      if (version === loadVersion) {
        loading = false;
        refreshing = false;
      }
    }
  }

  async function loadProviders() {
    try {
      const schema = await home.schema();
      providers = schema.providers;
      backend = schema.activeBackend || backend;
    } catch (cause) {
      degraded =
        cause instanceof Error ? cause.message : "Provider presentation is unavailable.";
    }
  }

  async function openInitialReview() {
    if (!initialReview || initialReviewOpened) return;
    try {
      const requested = data?.matches.find((item) => item.externalSnapshotId === initialReview) ??
        await matchReview.get(initialReview);
      if (!requested) return;
      initialReviewOpened = true;
      openMatch(requested);
    } catch {
      // The review queue remains usable when a stale deep link no longer resolves.
    }
  }

  const refreshScheduler = createRefreshScheduler(load);
  const scheduleRefresh = refreshScheduler.schedule;

  function setState(value: string) {
    stateFilter = value;
    page = 1;
    void load();
  }

  function setView(value: string) {
    if (!(value in viewFilters)) return;
    view = value as MappingView;
    setState(viewFilters[view]);
  }

  function removeResolvedFromQueue(match: MatchReviewItem) {
    if (!data || !["tentative", "review", "unresolved"].includes(view)) return;
    const wasVisible = data.matches.some((item) => item.externalSnapshotId === match.externalSnapshotId);
    data = {
      ...data,
      matches: data.matches.filter((item) => item.externalSnapshotId !== match.externalSnapshotId),
      stats: {
        ...data.stats,
        suggested: Math.max(0, data.stats.suggested - (wasVisible && view === "tentative" ? 1 : 0)),
        ambiguous: Math.max(0, (data.stats.ambiguous ?? 0) - (wasVisible && view === "review" ? 1 : 0)),
        attention: Math.max(0, data.stats.attention - (wasVisible && ["tentative", "review"].includes(view) ? 1 : 0)),
        unresolved: Math.max(0, data.stats.unresolved - (wasVisible && view === "unresolved" ? 1 : 0)),
      },
      pagination: {
        ...data.pagination,
        total: Math.max(0, data.pagination.total - (wasVisible ? 1 : 0)),
      },
    };
  }

  function submitFilters() {
    search = searchInput.trim();
    page = 1;
    void load();
  }

  function openMatch(match: MatchReviewItem) {
    selected = match;
    dialogOpen = true;
  }

  async function matchSaved(message: string) {
    const resolved = selected;
    feedback = message;
    await load();
    if (resolved) removeResolvedFromQueue(resolved);
  }

  async function rematchAllQueued(_jobId: string, message: string) {
    feedback = message;
    await load();
  }

  async function rematch(match: MatchReviewItem) {
    if (action) return;
    action = `rematch:${match.externalSnapshotId}`;
    try {
      await matchReview.rematch(match.externalSnapshotId);
      feedback = `${match.title || "Track"} rematched.`;
      await load();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "Rematch failed.";
    } finally {
      action = "";
    }
  }

  async function accept(match: MatchReviewItem) {
    const candidate = match.candidates.find((item) =>
      candidateResolution(item, match.providerId, playbackProviders));
    const resolution = candidateResolution(candidate, match.providerId, playbackProviders);
    if (action || !canEditAuthorityScope(match, "personal")) return;
    if (!resolution) return openMatch(match);
    action = `accept:${match.externalSnapshotId}`;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        ...resolution,
        reason: "Accepted highest-confidence automatic candidate",
        authorityScope: "personal",
        expectedAuthority: expectedAuthorityForScope(match, "personal"),
      });
      feedback = "Highest-confidence candidate accepted.";
      await load();
      removeResolvedFromQueue(match);
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The candidate could not be accepted.";
    } finally {
      action = "";
    }
  }

  function confirm(
    kind: DestructiveAction["kind"],
    match: MatchReviewItem,
    authorityScope: MatchAuthorityScope = "personal",
  ) {
    if (kind !== "reject") return;
    destructive = { kind, match, authorityScope };
    destructiveOpen = true;
  }

  function confirmAuthority(
    kind: "manual_delete" | "manual_rematch",
    match: MatchReviewItem,
    authority: ManualMatchAuthority,
  ) {
    destructive = { kind, match, authority };
    destructiveOpen = true;
  }

  async function applyDestructive() {
    if (!destructive || action) return;
    action = destructive.kind === "reject"
      ? destructive.kind
      : `authority:${destructive.authority.id}`;
    try {
      if (destructive.kind === "reject") {
        await matchReview.resolve(destructive.match.externalSnapshotId, {
          targetType: "reject",
          reason: "Rejected from the match review queue",
          authorityScope: destructive.authorityScope,
          expectedAuthority: expectedAuthorityForScope(
            destructive.match,
            destructive.authorityScope,
          ),
        });
        feedback = "Candidate rejected.";
      } else if (destructive.kind === "manual_rematch") {
        await manualMatchAuthority.rematch(destructive.authority);
        feedback = `${scopeLabel(authorityScopeOf(destructive.authority))} decision cleared and track rematched.`;
      } else {
        await manualMatchAuthority.delete(destructive.authority);
        feedback = `${scopeLabel(authorityScopeOf(destructive.authority))} decision cleared.`;
      }
      destructiveOpen = false;
      dialogOpen = false;
      await load();
      if (destructive.kind === "reject") removeResolvedFromQueue(destructive.match);
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The action failed.";
    } finally {
      action = "";
    }
  }

  function authorityLabel(authority: ManualMatchAuthority) {
    const decision = authority.kind === "provider_match"
      ? `Manual ${providerName(authority.targetProviderId)} match`
      : authority.kind === "local_match"
        ? `Manual ${backend} match`
        : "Manual rejection";
    return `${scopeLabel(authorityScopeOf(authority))} · ${decision}`;
  }

  function scopeLabel(scope: MatchAuthorityScope) {
    return scope === "household" ? "Household" : "Personal";
  }

  function authorityState(match: MatchReviewItem, authority: ManualMatchAuthority) {
    if (authority.effective) return "Effective";
    if (authorityScopeOf(authority) === "household" && authorityForScope(match, "personal"))
      return "Superseded by personal";
    return "Not currently effective";
  }

  function revealedLayer(match: MatchReviewItem, authority: ManualMatchAuthority) {
    const scope = authorityScopeOf(authority);
    if (scope === "personal" && authorityForScope(match, "household"))
      return "The household decision beneath it will become effective.";
    if (scope === "household" && authorityForScope(match, "personal"))
      return "The personal decision remains effective.";
    return "Automatic matching will become effective.";
  }

  function confirmationCopy() {
    if (destructive?.kind === "manual_rematch") return {
      title: `Clear ${authorityScopeOf(destructive.authority)} and rematch?`,
      description: `The ${authorityScopeOf(destructive.authority)} decision will be cleared before the matching algorithm recalculates the track. ${revealedLayer(destructive.match, destructive.authority)} Durable audit history is retained.`,
      label: `Clear ${authorityScopeOf(destructive.authority)} and rematch`,
    };
    if (destructive?.kind === "manual_delete") return {
      title: `Clear ${authorityScopeOf(destructive.authority)} decision?`,
      description: `The ${authorityScopeOf(destructive.authority)} decision will stop being authoritative. ${revealedLayer(destructive.match, destructive.authority)} Durable audit history is retained.`,
      label: `Clear ${authorityScopeOf(destructive.authority)}`,
    };
    return {
      title: "Reject this candidate?",
      description: "The current candidate will be recorded as rejected. You can rematch it later.",
      label: "Reject candidate",
    };
  }

  onMount(() => {
    searchInput = initialSearch;
    search = initialSearch;
    if (initialSearch) {
      stateFilter = "";
      view = "all";
    }
    void loadProviders();
    void (async () => {
      await load();
      await openInitialReview();
    })();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      refreshScheduler.cancel();
    };
  });
</script>

{#if loading}
  <section class="mapping-page" aria-label="Loading match review" aria-busy="true">
    <Skeleton class="panel skeleton-panel" />
  </section>
{:else if error && !data}
  <RouteError
    eyebrow="Match review unavailable"
    title="Allstarr could not load canonical match decisions."
    message={error}
    onRetry={load}
  />
{:else if data}
  {#if degraded || error}
    <div class="degraded-banner" role="status">
      <span aria-hidden="true">!</span>
      <p><strong>Some mapping data is unavailable.</strong> {error || degraded}</p>
      <Button variant="secondary" size="sm" onclick={() => { void loadProviders(); void load(); }}>Retry</Button>
    </div>
  {/if}

  <section class="mapping-page" aria-busy={refreshing}>
    <article class="panel mapping-queue">
      <header class="panel-heading playlist-toolbar mapping-heading">
        <div>
          <p class="eyebrow">Library matching</p>
          <h2>Match review queue</h2>
          <p>Automatic and manual decisions share one durable matching pipeline.</p>
        </div>
        <div class="panel-heading-actions playlist-toolbar-actions mapping-heading-actions">
          <Button variant="secondary" disabled={Boolean(action) || refreshing} onclick={() => rematchAllOpen = true}>Rematch all</Button>
          <Button variant="secondary" disabled={Boolean(action) || refreshing} onclick={() => void load()}>Refresh</Button>
        </div>
      </header>

      <SegmentedNav
        items={[
          { id: "all", label: "All", count: data.stats.total, icon: List },
          { id: "manual", label: "Manual", count: data.stats.manualMatches ?? 0, icon: Hand },
          { id: "rejected", label: "Rejected", count: data.stats.manualRejections ?? 0, icon: Ban },
          {
            id: "automatic",
            label: "Automatic",
            count: data.stats.automatic ?? Math.max(0, data.stats.accepted - (data.stats.manualMatches ?? 0)),
            icon: Sparkles,
          },
          { id: "tentative", label: "Tentative", count: data.stats.suggested, icon: Clock3 },
          {
            id: "review",
            label: "Review",
            count: data.stats.ambiguous ?? Math.max(0, data.stats.review - data.stats.suggested),
            icon: ScanSearch,
          },
          { id: "unresolved", label: "Unresolved", count: data.stats.unresolved, icon: CircleQuestionMark },
        ]}
        active={view}
        label="Mapping views"
        class="mapping-view-tabs"
        onchange={setView}
      />

      <form class="playlist-filters mapping-filters" onsubmit={(event) => { event.preventDefault(); submitFilters(); }}>
        <SearchField bind:value={searchInput} label="Search" placeholder="Title, artist, album, or provider" />
        <label>
          <span>Library scope</span>
          <input bind:value={libraryScopeId} placeholder="All libraries" />
        </label>
        <div class="filter-field"><span>Confidence</span><SelectField bind:value={sort} label="Confidence" onchange={() => { page = 1; void load(); }} options={[
          { value: "", label: "Default order" }, { value: "confidence_desc", label: "Highest first" },
          { value: "confidence_asc", label: "Lowest first" },
        ]} /></div>
        <Button type="submit">Apply</Button>
      </form>

      {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

      <div class="mapping-rows">
        {#each data.matches as match}
          {@const target = currentTarget(match)}
          {@const candidate = match.candidates.find((item) =>
            candidateResolution(item, match.providerId, playbackProviders))}
          {@const resolution = candidateResolution(candidate, match.providerId, playbackProviders)}
          {@const candidateProviderId = resolution?.targetType === "provider" ? resolution.externalProvider : "local"}
          {@const candidateExternalId = resolution?.targetType === "provider" ? resolution.externalId : null}
          {@const candidateArtwork = candidateExternalId
            ? `/api/admin/downloads/artwork/${encodeURIComponent(`ext-${candidateProviderId}-song-${candidateExternalId}`)}`
            : candidate?.backendItemId
              ? `/api/admin/downloads/artwork/${encodeURIComponent(candidate.backendItemId)}`
              : ""}
          {@const visibleAuthorities = [...(match.manualAuthorities ?? [])].sort((left, right) =>
            authorityScopeOf(left) === authorityScopeOf(right)
              ? 0
              : authorityScopeOf(left) === "personal" ? -1 : 1)}
          <article class:needs-attention={isAttention(match.state)} class="mapping-row">
            <div class="mapping-comparison">
              <div class="mapping-party">
                <MediaArtwork
                  class="mapping-art"
                  url={match.sourceArtworkUrl || match.artworkUrl}
                />
                <span class="mapping-party-copy">
                  <span class="mapping-provider">
                    <ProviderMark id={match.providerId} definition={provider(match.providerId)} />
                    {providerName(match.providerId)} source
                  </span>
                  <strong>{match.title || "Unknown track"}</strong>
                  <small>{match.artist || "Unknown artist"}</small>
                  <small>{match.album || "Unknown album"}</small>
                  <small>{formatDuration(match.durationMilliseconds)} · Snapshot {match.externalSnapshotId}</small>
                </span>
              </div>

              <ArrowRight class="mapping-arrow" size={20} aria-hidden="true" />

              <div class:unresolved={!target && !candidate} class="mapping-party">
                {#if target || candidate}
                  <MediaArtwork
                    class="mapping-art"
                    url={target?.artworkUrl || match.candidateArtworkUrl || candidateArtwork}
                  />
                {:else}
                  <span class="mapping-route-missing" aria-hidden="true">?</span>
                {/if}
                <span class="mapping-party-copy">
                  <span class="mapping-provider">
                    {#if target || candidate}
                      <ProviderMark
                        id={(target?.providerId ?? candidateProviderId) === "local" ? backend.toLowerCase() : (target?.providerId ?? candidateProviderId)}
                        definition={provider(target?.providerId ?? candidateProviderId)}
                        label={providerName(target?.providerId ?? candidateProviderId)}
                      />
                    {/if}
                    {target
                      ? `${providerName(target.providerId)} match`
                      : candidate
                        ? `${providerName(candidateProviderId)} candidate`
                        : "No candidate"}
                  </span>
                  <strong>{target?.title || candidate?.title || "Unmatched"}</strong>
                  <small>{target?.artist || candidate?.artist || "No playable candidate"}</small>
                  <small>{target?.album || candidate?.album || "Rematch or search interactively"}</small>
                  <small>
                    {formatDuration(target?.durationMilliseconds ?? candidate?.durationMilliseconds)}
                    {#if target?.identity || candidate?.libraryTrackId || candidateExternalId}
                      · {target?.identity || candidate?.libraryTrackId || candidateExternalId}
                    {/if}
                  </small>
                </span>
              </div>
            </div>

            {#if visibleAuthorities.length}
              <div class="manual-authority-list" aria-label={`Manual decisions for ${match.title || "track"}`}>
                {#each visibleAuthorities as authority (authority.id)}
                  <div class="manual-authority-row">
                    <span>
                      <strong>{authorityLabel(authority)}</strong>
                      <small>
                        {authority.reason}
                        · {authorityState(match, authority)}
                      </small>
                    </span>
                    <Button
                      variant="secondary"
                      size="sm"
                      disabled={Boolean(action) || !canEditAuthority(authority)}
                      onclick={() => confirmAuthority("manual_rematch", match, authority)}
                    >Clear {authorityScopeOf(authority)} and rematch</Button>
                    <Button
                      variant="destructive"
                      size="sm"
                      disabled={Boolean(action) || !canEditAuthority(authority)}
                      onclick={() => confirmAuthority("manual_delete", match, authority)}
                    >Clear {authorityScopeOf(authority)}</Button>
                  </div>
                {/each}
              </div>
            {/if}

            <div class="mapping-row-footer">
              <div class="mapping-evidence">
                <Badge state={match.state}>{reviewStateLabel(match.state)}</Badge>
                <span class="mapping-evidence-summary">
                  {candidate || target ? `${percent(match.confidence)} confidence` : "Not scored"}
                  {#if match.threshold != null} · {percent(match.threshold)} auto threshold{/if}
                  · {relativeTime(match.decidedAt, "Not decided")}
                  {#if match.reasons.length || match.warnings.length}
                    · {[...match.reasons, ...match.warnings].slice(0, 2).map((reason) => reason.replaceAll("_", " ")).join(" · ")}
                  {/if}
                </span>
                {#if match.sourceArtworkUrl && match.candidateArtworkUrl}
                  <ArtworkSimilarity source={match.sourceArtworkUrl} candidate={match.candidateArtworkUrl} />
                {/if}
              </div>

              <div class="mapping-row-actions">
                {#if !target && resolution}
                  <span class="mapping-action-confidence">
                    <strong>{percent(candidate?.confidence ?? match.confidence)}</strong>
                    <small>Confidence</small>
                  </span>
                  <Button disabled={Boolean(action) || !canEditAuthorityScope(match, "personal")} onclick={() => void accept(match)}>{action === `accept:${match.externalSnapshotId}` ? "Accepting…" : "Accept"}</Button>
                {/if}
                {#if !target}
                  <Button class="mapping-rematch-action" variant="secondary" disabled={Boolean(action)} onclick={() => void rematch(match)}>{action === `rematch:${match.externalSnapshotId}` ? "Rematching…" : "Rematch"}</Button>
                {/if}
                <Button class="mapping-search-action" onclick={() => openMatch(match)}>
                  {target ? "Review match" : "Interactive search"}
                </Button>
                <DropdownMenu.Root>
                  <DropdownMenu.Trigger class="track-menu-trigger" disabled={Boolean(action)} aria-label={`More actions for ${match.title || "track"}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                  <DropdownMenu.Portal>
                    <DropdownMenu.Content class="bits-menu" sideOffset={4} align="end">
                      <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => void rematch(match)}>Rematch</DropdownMenu.Item>
                      <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action) || !canEditAuthorityScope(match, "personal")} onSelect={() => confirm("reject", match)}>Reject candidate</DropdownMenu.Item>
                      {#each match.manualAuthorities ?? [] as authority (authority.id)}
                        <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action) || !canEditAuthority(authority)} onSelect={() => confirmAuthority("manual_rematch", match, authority)}>
                          Clear {authorityScopeOf(authority)} and rematch
                        </DropdownMenu.Item>
                        <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action) || !canEditAuthority(authority)} onSelect={() => confirmAuthority("manual_delete", match, authority)}>
                          Clear {authorityScopeOf(authority)}
                        </DropdownMenu.Item>
                      {/each}
                    </DropdownMenu.Content>
                  </DropdownMenu.Portal>
                </DropdownMenu.Root>
              </div>

              <details class="mapping-details">
                <summary class="disclosure-summary compact"><DisclosureLabel title="Why this score?" description="See every matching signal" /></summary>
                <p>
                  Base evidence uses available title, artist, album, duration, ISRC, artwork,
                  and verified provider identity. Missing evidence is omitted rather than
                  scored as zero. The acceptance threshold is {percent(match.threshold)};
                  ambiguity and warnings keep a close result in review. Configured local and
                  extension preferences apply only after compatible evidence is scored.
                </p>
                <dl>
                  {#if candidate}
                    {#each scoreComponents(candidate) as [name, value]}
                      <div><dt>{name.replace(/([a-z])([A-Z])/g, "$1 $2").replaceAll("_", " ")}</dt><dd>{percent(value)}</dd></div>
                    {/each}
                  {/if}
                  <div><dt>Source snapshot</dt><dd>{match.externalSnapshotId}</dd></div>
                  {#if match.canonicalRecordingId}<div><dt>Canonical recording</dt><dd>{match.canonicalRecordingId}</dd></div>{/if}
                  {#if match.libraryTrackId}<div><dt>Library track</dt><dd>{match.libraryTrackId}</dd></div>{/if}
                  {#if match.isrc}<div><dt>ISRC</dt><dd>{match.isrc}</dd></div>{/if}
                  {#if match.algorithmVersion}<div><dt>Algorithm</dt><dd>{match.algorithmVersion}</dd></div>{/if}
                  {#each match.providerIdentities as identity}
                    <div><dt>{providerName(identity.providerId)}</dt><dd>{identity.externalId} · {identity.verification}</dd></div>
                  {/each}
                </dl>
              </details>
            </div>
          </article>
        {:else}
          <div class="compact-empty">
            <strong>No mappings found</strong>
            <p>Try another filter, or wait for the next playlist match.</p>
          </div>
        {/each}
      </div>

      <nav class="playlist-pagination mapping-pagination" aria-label="Match review pages">
        <span>{data.pagination.total} tracks</span>
        <div>
          <Button variant="secondary" size="sm" disabled={data.pagination.page <= 1} onclick={() => { page -= 1; void load(); }}>Previous</Button>
          <span>Page {data.pagination.page} of {data.pagination.totalPages}</span>
          <Button variant="secondary" size="sm" disabled={data.pagination.page >= data.pagination.totalPages} onclick={() => { page += 1; void load(); }}>Next</Button>
        </div>
      </nav>
    </article>
  </section>

  <MatchDialog
    bind:open={dialogOpen}
    match={selected}
    {providers}
    {backend}
    onSaved={matchSaved}
    onReject={(match, authorityScope) => confirm("reject", match, authorityScope)}
  />

  <BulkRematchDialog kind="automatic" bind:open={rematchAllOpen} onQueued={rematchAllQueued} />

  <ConfirmDialog
    bind:open={destructiveOpen}
    preventScroll={!dialogOpen}
    title={confirmationCopy().title}
    description={confirmationCopy().description}
    confirmLabel={confirmationCopy().label}
    onConfirm={applyDestructive}
  />
{/if}

<style>
  .manual-authority-row :global([data-slot="button"]) {
    min-width: 0;
    height: auto;
    min-height: var(--control-sm);
    padding-block: var(--space-2);
    line-height: 1.25;
    white-space: normal;
  }
</style>
