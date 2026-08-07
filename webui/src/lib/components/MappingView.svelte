<script lang="ts">
  import { onMount } from "svelte";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { ArrowRight, MoreHorizontal } from "lucide-svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import {
    home,
    matchReview,
    type MatchReviewItem,
    type MatchReviewResponse,
    type ProviderDefinition,
  } from "$lib/api";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import ArtworkSimilarity from "$lib/components/ArtworkSimilarity.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import RouteError from "$lib/components/RouteError.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
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
  import { liveUpdates } from "$lib/live-updates.svelte";

  type DestructiveAction = { kind: "reject" | "clear"; match: MatchReviewItem };

  let { initialSearch = "", initialReview = "" }: { initialSearch?: string; initialReview?: string } = $props();

  let data = $state<MatchReviewResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let backend = $state("Local library");
  let stateFilter = $state("attention");
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
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  let dialogOpen = $state(false);
  let initialReviewOpened = $state(false);
  let selected = $state<MatchReviewItem | null>(null);
  let destructiveOpen = $state(false);
  let destructive = $state<DestructiveAction | null>(null);

  function provider(providerId: string) {
    return providers.find((item) => item.id.toLowerCase() === providerId.toLowerCase());
  }

  function providerName(providerId?: string | null) {
    if (!providerId) return "Unresolved";
    if (providerId === "local") return backend;
    return provider(providerId)?.name ?? providerId;
  }

  function relativeTime(value?: string | null) {
    if (!value) return "Not decided";
    const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1_000);
    const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
    const minutes = Math.round(seconds / 60);
    if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
    return formatter.format(Math.round(hours / 24), "day");
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
      if (initialReview && !initialReviewOpened) {
        const requested = data.matches.find((item) => item.externalSnapshotId === initialReview);
        if (requested) {
          initialReviewOpened = true;
          openMatch(requested);
        }
      }
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

  function scheduleRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void load();
    }, 250);
  }

  function setState(value: string) {
    stateFilter = value;
    page = 1;
    void load();
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
    feedback = message;
    await load();
  }

  async function rematch(match: MatchReviewItem) {
    if (action) return;
    action = match.externalSnapshotId;
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
    if (!resolution || action) return openMatch(match);
    action = match.externalSnapshotId;
    try {
      await matchReview.resolve(match.externalSnapshotId, {
        ...resolution,
        reason: "Accepted highest-confidence automatic candidate",
      });
      feedback = "Highest-confidence candidate accepted.";
      await load();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The candidate could not be accepted.";
    } finally {
      action = "";
    }
  }

  function confirm(kind: DestructiveAction["kind"], match: MatchReviewItem) {
    destructive = { kind, match };
    destructiveOpen = true;
  }

  async function applyDestructive() {
    if (!destructive || action) return;
    action = destructive.kind;
    try {
      if (destructive.kind === "reject") {
        await matchReview.resolve(destructive.match.externalSnapshotId, {
          targetType: "reject",
          reason: "Rejected from the match review queue",
        });
        feedback = "Candidate rejected.";
      } else if (destructive.match.overrideId) {
        await matchReview.clear(
          destructive.match.overrideId,
          destructive.match.overrideRevision ?? 0,
        );
        feedback = "Manual review cleared.";
      }
      destructiveOpen = false;
      dialogOpen = false;
      await load();
    } catch (cause) {
      feedback = cause instanceof Error ? cause.message : "The action failed.";
    } finally {
      action = "";
    }
  }

  onMount(() => {
    searchInput = initialSearch;
    search = initialSearch;
    if (initialSearch) stateFilter = "";
    void loadProviders();
    void load();
    const unsubscribe = liveUpdates.subscribe(scheduleRefresh);
    return () => {
      unsubscribe();
      if (refreshTimer) clearTimeout(refreshTimer);
    };
  });
</script>

{#if loading}
  <section class="mapping-page" aria-label="Loading match review" aria-busy="true">
    <div class="panel skeleton-panel"></div>
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
      <button type="button" onclick={() => { void loadProviders(); void load(); }}>Retry</button>
    </div>
  {/if}

  <section class="mapping-page" aria-busy={refreshing}>
    <article class="panel mapping-queue">
      <header class="playlist-toolbar mapping-heading">
        <div>
          <p class="eyebrow">Library matching</p>
          <h2>Match review queue</h2>
          <p>Automatic and manual decisions share one durable matching pipeline.</p>
        </div>
        <button class="button-secondary" type="button" onclick={() => void load()}>Refresh</button>
      </header>

      <div class="mapping-metrics" aria-label="Match totals">
        <button class="metric-card" aria-pressed={stateFilter === "matched"} onclick={() => setState("matched")}>
          <span>Matched</span><strong>{data.stats.matched}</strong>
        </button>
        <button class="metric-card attention-card" aria-pressed={stateFilter === "attention"} onclick={() => setState("attention")}>
          <span>Needs attention</span><strong>{data.stats.attention}</strong>
        </button>
        <button class="metric-card" aria-pressed={stateFilter === "unresolved"} onclick={() => setState("unresolved")}>
          <span>Unresolved</span><strong>{data.stats.unresolved}</strong>
        </button>
        <button class="metric-card" aria-pressed={stateFilter === ""} onclick={() => setState("")}>
          <span>Total</span><strong>{data.stats.total}</strong>
        </button>
      </div>

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
        <button class="button-primary" type="submit">Apply</button>
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

            <div class="mapping-row-footer">
              <div class="mapping-evidence">
                <span class={`status-pill ${match.state}`}>{reviewStateLabel(match.state)}</span>
                <span class="mapping-evidence-summary">
                  {candidate || target ? `${percent(match.confidence)} confidence` : "Not scored"}
                  {#if match.threshold != null} · {percent(match.threshold)} auto threshold{/if}
                  · {relativeTime(match.decidedAt)}
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
                  <button class="button-primary" type="button" disabled={action === match.externalSnapshotId} onclick={() => void accept(match)}>Accept</button>
                {/if}
                {#if !target}
                  <button class="button-secondary" type="button" disabled={action === match.externalSnapshotId} onclick={() => void rematch(match)}>Rematch</button>
                {/if}
                <button class="button-primary" type="button" onclick={() => openMatch(match)}>
                  {target ? "Review match" : "Interactive search"}
                </button>
                <DropdownMenu.Root>
                  <DropdownMenu.Trigger class="track-menu-trigger" aria-label={`More actions for ${match.title || "track"}`}><MoreHorizontal size={18} aria-hidden="true" /></DropdownMenu.Trigger>
                  <DropdownMenu.Portal>
                    <DropdownMenu.Content class="bits-menu" sideOffset={4} align="end">
                      <DropdownMenu.Item class="bits-menu-item" onSelect={() => void rematch(match)}>Rematch</DropdownMenu.Item>
                      <DropdownMenu.Item class="bits-menu-item danger-item" onSelect={() => confirm("reject", match)}>Reject candidate</DropdownMenu.Item>
                      {#if match.overrideId}
                        <DropdownMenu.Item class="bits-menu-item danger-item" onSelect={() => confirm("clear", match)}>Clear manual review</DropdownMenu.Item>
                      {/if}
                    </DropdownMenu.Content>
                  </DropdownMenu.Portal>
                </DropdownMenu.Root>
              </div>

              <details class="mapping-details">
                <summary>Why this score?</summary>
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
          <button type="button" disabled={data.pagination.page <= 1} onclick={() => { page -= 1; void load(); }}>Previous</button>
          <span>Page {data.pagination.page} of {data.pagination.totalPages}</span>
          <button type="button" disabled={data.pagination.page >= data.pagination.totalPages} onclick={() => { page += 1; void load(); }}>Next</button>
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

  <ConfirmDialog
    bind:open={destructiveOpen}
    title={destructive?.kind === "clear" ? "Clear manual review?" : "Reject this candidate?"}
    description={destructive?.kind === "clear"
      ? "The durable manual decision will be revoked and automatic matching will become authoritative again."
      : "The current candidate will be recorded as rejected. You can rematch it later."}
    confirmLabel={destructive?.kind === "clear" ? "Clear review" : "Reject candidate"}
    onConfirm={applyDestructive}
  />
{/if}
