<script lang="ts">
  import { onMount } from "svelte";
  import { AlertDialog, DropdownMenu } from "bits-ui";
  import {
    home,
    matchReview,
    type MatchReviewItem,
    type MatchReviewResponse,
    type ProviderDefinition,
  } from "$lib/api";
  import MatchDialog from "$lib/components/MatchDialog.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import {
    currentTarget,
    isAttention,
    percent,
  } from "$lib/mappings";
  import { liveUpdates } from "$lib/live-updates.svelte";

  type DestructiveAction = { kind: "reject" | "clear"; match: MatchReviewItem };

  let data = $state<MatchReviewResponse | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let backend = $state("Local library");
  let stateFilter = $state("attention");
  let searchInput = $state("");
  let search = $state("");
  let libraryScopeId = $state("");
  let page = $state(1);
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");
  let degraded = $state("");
  let feedback = $state("");
  let action = $state("");
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;

  let dialogOpen = $state(false);
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
    if (refreshing) return;
    refreshing = true;
    error = "";
    try {
      data = await matchReview.list({
        page,
        pageSize: 50,
        search,
        state: stateFilter,
        libraryScopeId,
      });
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Match review is unavailable.";
    } finally {
      loading = false;
      refreshing = false;
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
  <section class="panel route-error" role="alert">
    <span aria-hidden="true">!</span>
    <div>
      <p class="eyebrow">Match review unavailable</p>
      <h2>Allstarr could not load canonical match decisions.</h2>
      <p>{error}</p>
    </div>
    <button class="button-secondary" type="button" onclick={() => void load()}>Try again</button>
  </section>
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
        <button class="metric-card" aria-pressed={stateFilter === ""} onclick={() => setState("")}>
          <span>Total</span><strong>{data.stats.total}</strong>
        </button>
        <button class="metric-card attention-card" aria-pressed={stateFilter === "attention"} onclick={() => setState("attention")}>
          <span>Needs attention</span><strong>{data.stats.attention}</strong>
        </button>
        <button class="metric-card" aria-pressed={stateFilter === "matched"} onclick={() => setState("matched")}>
          <span>Matched</span><strong>{data.stats.matched}</strong>
        </button>
        <button class="metric-card" aria-pressed={stateFilter === "unresolved"} onclick={() => setState("unresolved")}>
          <span>Unresolved</span><strong>{data.stats.unresolved}</strong>
        </button>
      </div>

      <form class="playlist-filters mapping-filters" onsubmit={(event) => { event.preventDefault(); submitFilters(); }}>
        <label>
          <span>Search</span>
          <input bind:value={searchInput} type="search" placeholder="Title, artist, album, or provider" />
        </label>
        <label>
          <span>Library scope</span>
          <input bind:value={libraryScopeId} placeholder="All libraries" />
        </label>
        <label>
          <span>Status</span>
          <select bind:value={stateFilter} onchange={() => { page = 1; void load(); }}>
            <option value="attention">Needs attention</option>
            <option value="">All tracks</option>
            <option value="matched">Matched</option>
            <option value="suggested">Suggested</option>
            <option value="ambiguous">Ambiguous</option>
            <option value="rejected">Rejected</option>
          </select>
        </label>
        <button class="button-primary" type="submit">Apply</button>
      </form>

      {#if feedback}<p class="action-feedback" role="status">{feedback}</p>{/if}

      <div class="mapping-rows">
        {#each data.matches as match}
          {@const target = currentTarget(match)}
          <article class:needs-attention={isAttention(match.state)} class="mapping-row">
            <div class="mapping-track-copy">
              <div class="mapping-source">
                <span class="media-art mapping-art">
                  {#if match.sourceArtworkUrl || match.artworkUrl}
                    <img src={match.sourceArtworkUrl || match.artworkUrl || ""} alt="" loading="lazy" />
                  {:else}
                    <ProviderMark id={match.providerId} definition={provider(match.providerId)} />
                  {/if}
                </span>
                <span>
                  <strong>{match.title || "Unknown track"}</strong>
                  <small>{match.artist || "Unknown artist"}{match.album ? ` · ${match.album}` : ""}</small>
                </span>
              </div>

              <div class="mapping-route">
                <span class="mapping-route-node">
                  <ProviderMark id={match.providerId} definition={provider(match.providerId)} />
                  <span><small>Source</small><strong>{providerName(match.providerId)}</strong></span>
                </span>
                <span class="mapping-arrow" aria-hidden="true">→</span>
                <span class:unresolved={!target} class="mapping-route-node">
                  {#if target}
                    <ProviderMark
                      id={target.providerId === "local" ? backend.toLowerCase() : target.providerId}
                      definition={provider(target.providerId)}
                      label={providerName(target.providerId)}
                    />
                  {:else}
                    <span class="mapping-route-missing" aria-hidden="true">?</span>
                  {/if}
                  <span>
                    <small>Current match</small>
                    <strong>{target?.title || "No playable match"}</strong>
                    <em>{target?.detail || "Review candidates"}</em>
                  </span>
                </span>
              </div>

              <div class="mapping-evidence">
                <span class={`status-pill ${match.state}`}>{match.state.replaceAll("_", " ")}</span>
                <span>{percent(match.confidence)} confidence</span>
                {#if match.threshold != null}<span>{percent(match.threshold)} threshold</span>{/if}
                <span>{relativeTime(match.decidedAt)}</span>
                {#each [...match.reasons, ...match.warnings].slice(0, 3) as reason}
                  <span>{reason.replaceAll("_", " ")}</span>
                {/each}
                <details>
                  <summary>Technical details</summary>
                  <dl>
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
            </div>

            <div class="mapping-row-actions">
              <button class="button-primary" type="button" onclick={() => openMatch(match)}>Review match</button>
              <DropdownMenu.Root>
                <DropdownMenu.Trigger class="track-menu-trigger" aria-label={`More actions for ${match.title || "track"}`}>•••</DropdownMenu.Trigger>
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

  <AlertDialog.Root bind:open={destructiveOpen}>
    <AlertDialog.Portal>
      <AlertDialog.Overlay class="dialog-overlay" />
      <AlertDialog.Content class="confirm-dialog">
        <AlertDialog.Title>{destructive?.kind === "clear" ? "Clear manual review?" : "Reject this candidate?"}</AlertDialog.Title>
        <AlertDialog.Description>
          {destructive?.kind === "clear"
            ? "The durable manual decision will be revoked and automatic matching will become authoritative again."
            : "The current candidate will be recorded as rejected. You can rematch it later."}
        </AlertDialog.Description>
        <footer>
          <AlertDialog.Cancel class="button-secondary">Cancel</AlertDialog.Cancel>
          <AlertDialog.Action class="button-danger" onclick={() => void applyDestructive()}>
            {destructive?.kind === "clear" ? "Clear review" : "Reject candidate"}
          </AlertDialog.Action>
        </footer>
      </AlertDialog.Content>
    </AlertDialog.Portal>
  </AlertDialog.Root>
{/if}
