<script lang="ts">
  import { Dialog, Tabs } from "bits-ui";
  import {
    matchReview,
    type MatchReviewItem,
    type MatchTarget,
    type ProviderDefinition,
  } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import { percent, playableProviders, scoreComponents } from "$lib/mappings";
  import { formatDuration } from "$lib/playlists";

  let {
    open = $bindable(false),
    match,
    providers,
    backend,
    onSaved,
    onReject,
  }: {
    open: boolean;
    match: MatchReviewItem | null;
    providers: ProviderDefinition[];
    backend: string;
    onSaved: (message: string) => void | Promise<void>;
    onReject: (match: MatchReviewItem) => void;
  } = $props();

  let preparedId = $state("");
  let targetMode = $state<"local" | "provider">("local");
  let targetQuery = $state("");
  let providerFilter = $state("");
  let results = $state<MatchTarget[]>([]);
  let searched = $state(false);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");

  const providerOptions = $derived(playableProviders(providers));

  $effect(() => {
    if (!open) {
      preparedId = "";
      return;
    }
    if (!match || preparedId === match.externalSnapshotId) return;
    preparedId = match.externalSnapshotId;
    targetMode = "local";
    targetQuery = [match.artist, match.title].filter(Boolean).join(" ");
    providerFilter = "";
    results = [];
    searched = false;
    error = "";
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

  function switchMode(mode: "local" | "provider") {
    targetMode = mode;
    results = [];
    searched = false;
    error = "";
  }

  async function search() {
    if (!match || targetQuery.trim().length < 2 || loading) return;
    loading = true;
    searched = true;
    error = "";
    try {
      const response =
        targetMode === "local"
          ? await matchReview.searchLocal(targetQuery.trim(), match.libraryScopeId)
          : await matchReview.searchProviders(
              targetQuery.trim(),
              match.libraryScopeId,
              providerFilter,
            );
      results = response.tracks;
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
    <Dialog.Overlay class="dialog-overlay" />
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

        <section class="automatic-candidates">
          <div class="dialog-section-heading">
            <div><strong>Automatic candidates</strong><small>Same scores used by automatic matching</small></div>
            <span>{match.candidates.length}</span>
          </div>
          {#if match.candidates.length}
            <div class="candidate-list">
              {#each match.candidates.slice(0, 5) as candidate}
                <article class="candidate-card">
                  <span class="media-art mapping-art">
                    {#if candidateArtwork(candidate.backendItemId)}<img src={candidateArtwork(candidate.backendItemId)} alt="" loading="lazy" />{:else}<span aria-hidden="true">♪</span>{/if}
                  </span>
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

        <Tabs.Root
          value={targetMode}
          loop
          onValueChange={(value) => switchMode(value as "local" | "provider")}
        >
          <Tabs.List class="match-target-tabs" aria-label="Candidate source">
            <span class:provider={targetMode === "provider"} aria-hidden="true"></span>
            <Tabs.Trigger value="local">Local library</Tabs.Trigger>
            <Tabs.Trigger value="provider">Playable providers</Tabs.Trigger>
          </Tabs.List>
        </Tabs.Root>

        <form class="target-search" onsubmit={(event) => { event.preventDefault(); void search(); }}>
          {#if targetMode === "provider"}
            <label>
              <span>Provider</span>
              <select bind:value={providerFilter}>
                <option value="">All playable providers</option>
                {#each providerOptions as option}
                  <option value={option.id}>{option.name}</option>
                {/each}
              </select>
            </label>
          {/if}
          <label class="grow">
            <span>Artist and track</span>
            <input bind:value={targetQuery} required minlength="2" />
          </label>
          <button class="button-primary" type="submit" disabled={loading}>
            {loading ? "Searching…" : "Search"}
          </button>
        </form>

        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        <div class="target-results">
          {#each results as target}
            <button
              type="button"
              disabled={saving}
              onclick={() =>
                targetMode === "local"
                  ? void chooseLocal(target.id, "Selected from indexed library search")
                  : void chooseProvider(target)}
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
              <span>Choose</span>
            </button>
          {:else}
            {#if searched && !loading}
              <div class="compact-empty"><strong>No playable candidates found</strong><p>Try a more exact title or another provider filter.</p></div>
            {/if}
          {/each}
        </div>

        <footer>
          <button class="button-danger" type="button" onclick={() => { open = false; onReject(match!); }}>Reject candidate</button>
          <Dialog.Close class="button-secondary">Cancel</Dialog.Close>
        </footer>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
