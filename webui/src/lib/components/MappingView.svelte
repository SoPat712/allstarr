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
    type MatchReviewItem,
    type MatchReviewResponse,
    type ProviderDefinition,
  } from "$lib/api";
  import { manualMatchAuthority } from "$lib/matching-api";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import BulkRematchDialog from "$lib/components/BulkRematchDialog.svelte";
  import ArtworkSimilarity from "$lib/components/ArtworkSimilarity.svelte";
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
    | { kind: "reject"; match: MatchReviewItem }
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
    if (action) return;
    if (!resolution) return openMatch(match);
    action = `accept:${match.externalSnapshotId}`;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        ...resolution,
        reason: "Accepted highest-confidence automatic candidate",
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

  function confirm(kind: DestructiveAction["kind"], match: MatchReviewItem) {
    if (kind !== "reject") return;
    destructive = { kind, match };
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
        });
        feedback = "Candidate rejected.";
      } else if (destructive.kind === "manual_rematch") {
        await manualMatchAuthority.rematch(destructive.authority);
        feedback = `${destructive.match.title || "Track"} released and rematched with the current algorithm.`;
      } else {
        await manualMatchAuthority.delete(destructive.authority);
        feedback = "Manual authority deleted; automatic matching is authoritative again.";
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
    return authority.kind === "provider_match"
      ? `Manual ${providerName(authority.targetProviderId)} match`
      : authority.kind === "local_match"
        ? `Manual ${backend} match`
        : "Manual rejection";
  }

  function confirmationCopy() {
    if (destructive?.kind === "manual_rematch") return {
      title: "Release and rematch this decision?",
      description: "Manual authority will be removed first, then the current matching algorithm will calculate a new result. Durable audit history is retained.",
      label: "Release and rematch",
    };
    if (destructive?.kind === "manual_delete") return {
      title: "Delete this manual decision?",
      description: destructive.authority.kind === "provider_match"
        ? "The provider pin will stop being authoritative. Its verified identity history is retained and may still be selected by a future automatic match."
        : "The manual decision will stop being authoritative. Its durable audit history is retained.",
      label: "Delete manual decision",
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
          {@const visibleAuthorities = (match.manualAuthorities ?? []).filter((authority) =>
            view === "manual"
              ? authority.kind !== "rejection"
              : view === "rejected"
                ? authority.kind === "rejection"
                : true)}
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

            {#if view === "manual" || view === "rejected"}
              <div class="manual-authority-list" aria-label={`Manual decisions for ${match.title || "track"}`}>
                {#each visibleAuthorities as authority (authority.id)}
                  <div class="manual-authority-row">
                    <span>
                      <strong>{authorityLabel(authority)}</strong>
                      <small>
                        {authority.reason}
                        {#if !authority.effective} · Superseded by the current algorithm{/if}
                      </small>
                    </span>
                    <Button
                      variant="secondary"
                      size="sm"
                      disabled={Boolean(action)}
                      onclick={() => confirmAuthority("manual_rematch", match, authority)}
                    >Rematch</Button>
                    <Button
                      variant="destructive"
                      size="sm"
                      disabled={Boolean(action)}
                      onclick={() => confirmAuthority("manual_delete", match, authority)}
                    >Delete</Button>
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
                  <Button disabled={Boolean(action)} onclick={() => void accept(match)}>{action === `accept:${match.externalSnapshotId}` ? "Accepting…" : "Accept"}</Button>
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
                      <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action)} onSelect={() => confirm("reject", match)}>Reject candidate</DropdownMenu.Item>
                      {#each match.manualAuthorities ?? [] as authority (authority.id)}
                        <DropdownMenu.Item class="bits-menu-item" disabled={Boolean(action)} onSelect={() => confirmAuthority("manual_rematch", match, authority)}>
                          Rematch {authorityLabel(authority).toLowerCase()}
                        </DropdownMenu.Item>
                        <DropdownMenu.Item class="bits-menu-item danger-item" disabled={Boolean(action)} onSelect={() => confirmAuthority("manual_delete", match, authority)}>
                          Delete {authorityLabel(authority).toLowerCase()}
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
    onReject={(match) => confirm("reject", match)}
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
