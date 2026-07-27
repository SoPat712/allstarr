<script lang="ts">
  import { Dialog } from "bits-ui";
  import {
    playlistLinks,
    type MediaTarget,
    type PlaylistDiscoveryItem,
    type PlaylistSourceAccount,
    type ProviderDefinition,
    type TargetPlaylist,
  } from "$lib/api";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { orderPlaylistSources } from "$lib/playlists";

  let {
    open = $bindable(false),
    providers,
    onSaved,
  }: {
    open: boolean;
    providers: ProviderDefinition[];
    onSaved: (message: string) => void | Promise<void>;
  } = $props();

  let prepared = $state(false);
  let step = $state(1);
  let accounts = $state<PlaylistSourceAccount[]>([]);
  let providerOrder = $state<string[]>([]);
  let providerNames = $state<Record<string, string>>({});
  let blocked = $state(0);
  let targets = $state<MediaTarget[]>([]);
  let targetId = $state("");
  let targetPlaylistId = $state("");
  let targetQuery = $state("");
  let targetPlaylists = $state<TargetPlaylist[]>([]);
  let accountId = $state("");
  let sourcePlaylistId = $state("");
  let sourceQuery = $state("");
  let sourcePlaylists = $state<PlaylistDiscoveryItem[]>([]);
  let sourceCursor = $state("");
  let schedule = $state("manual");
  let syncBehavior = $state("preserve");
  let syncName = $state(true);
  let syncDescription = $state(true);
  let syncArtwork = $state(true);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");
  let browseRequest = 0;

  const orderedAccounts = $derived(orderPlaylistSources(accounts, providerOrder));
  const providerIds = $derived([...new Set(orderedAccounts.map((item) => item.providerId))]);
  const selectedAccount = $derived(accounts.find((item) => item.id === accountId));
  const selectedTarget = $derived(targets.find((item) => item.id === targetId));
  const selectedLibraryScope = $derived(selectedAccount?.libraryScopeId || selectedTarget?.libraryScopeId);
  const stepReady = $derived(step === 1 ? Boolean(targetPlaylistId) : step === 2 ? Boolean(sourcePlaylistId) : true);

  function definition(id: string) {
    return providers.find((item) => item.id.toLowerCase() === id.toLowerCase());
  }

  $effect(() => {
    if (!open) {
      prepared = false;
      return;
    }
    if (prepared) return;
    prepared = true;
    void load();
  });

  async function load() {
    loading = true;
    error = "";
    step = 1;
    targetPlaylistId = "";
    targetQuery = "";
    accountId = "";
    sourcePlaylistId = "";
    sourceQuery = "";
    sourcePlaylists = [];
    sourceCursor = "";
    schedule = "manual";
    syncBehavior = "preserve";
    try {
      const [sourceResponse, targetResponse] = await Promise.all([
        playlistLinks.sources(), playlistLinks.targets(),
      ]);
      accounts = sourceResponse.accounts;
      providerOrder = sourceResponse.providers.map((item) => item.id);
      providerNames = Object.fromEntries(sourceResponse.providers.map((item) => [item.id, item.displayName]));
      blocked = sourceResponse.blockedAccounts.length;
      targets = targetResponse.targets;
      targetId = targets[0]?.id ?? "";
      if (targetId) await browseTargets();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Playlist setup is unavailable.";
    } finally {
      loading = false;
    }
  }

  async function chooseTarget(id: string) {
    targetId = id;
    targetPlaylistId = "";
    targetQuery = "";
    targetPlaylists = [];
    await browseTargets();
  }

  async function browseTargets() {
    if (!targetId) return;
    const requestedTarget = targetId;
    const request = ++browseRequest;
    loading = true;
    error = "";
    try {
      const response = await playlistLinks.targetPlaylists(requestedTarget, targetQuery.trim());
      if (request !== browseRequest || requestedTarget !== targetId) return;
      targetPlaylists = response.items.filter((item) => item.writable);
    } catch (cause) {
      if (request !== browseRequest) return;
      error = cause instanceof Error ? cause.message : "Jellyfin playlists could not be loaded.";
    } finally {
      if (request === browseRequest) loading = false;
    }
  }

  async function chooseAccount(id: string) {
    accountId = id;
    sourcePlaylistId = "";
    sourceQuery = "";
    sourcePlaylists = [];
    sourceCursor = "";
    await browseSources();
  }

  async function browseSources(cursor = "") {
    if (!accountId) return;
    const requestedAccount = accountId;
    const request = ++browseRequest;
    loading = true;
    error = "";
    try {
      const response = await playlistLinks.sourcePlaylists(requestedAccount, sourceQuery.trim(), cursor);
      if (request !== browseRequest || requestedAccount !== accountId) return;
      sourcePlaylists = cursor ? [...sourcePlaylists, ...response.items] : response.items;
      sourceCursor = response.nextCursor ?? "";
    } catch (cause) {
      if (request !== browseRequest) return;
      error = cause instanceof Error ? cause.message : "Source playlists could not be loaded.";
    } finally {
      if (request === browseRequest) loading = false;
    }
  }

  function next() {
    if (!stepReady) return;
    step = Math.min(3, step + 1);
  }

  async function save() {
    if (!selectedAccount || !selectedTarget || !selectedLibraryScope || !sourcePlaylistId || !targetPlaylistId || saving) return;
    saving = true;
    error = "";
    try {
      const link = await playlistLinks.create({
        providerAccountId: selectedAccount.id,
        sourceProviderId: selectedAccount.providerId,
        sourcePlaylistId,
        libraryScopeId: selectedLibraryScope,
        targetProtocol: selectedTarget.protocol,
        targetBackendInstanceId: selectedTarget.backendInstanceId,
        targetCredentialReferenceId: selectedTarget.credentialReferenceId,
        targetPlaylistId,
        mode: "materialized",
        materializationMode: "reconcile",
        mirrorStaleEntries: syncBehavior === "mirror",
        preserveManualEntries: syncBehavior === "preserve",
        syncName,
        syncDescription,
        syncArtwork,
      });
      if (schedule !== "manual") {
        const cronExpression = ({
          hourly: "0 * * * *",
          daily: "0 3 * * *",
          weekly: "0 3 * * 1",
        } as Record<string, string>)[schedule];
        try {
          await playlistLinks.createSchedule(link.id, {
            cronExpression,
            timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
            overlapPolicy: "skip",
            misfirePolicy: "runOnce",
          });
        } catch {
          open = false;
          await onSaved("Playlist linked, but its automatic schedule could not be saved.");
          return;
        }
      }
      open = false;
      await onSaved("Playlist linked. Allstarr will match local tracks first and use configured fallback Sources.");
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Playlist could not be linked.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog playlist-add-dialog">
      <header>
        <div>
          <p class="eyebrow">Library intake</p>
          <Dialog.Title>Link a playlist</Dialog.Title>
          <Dialog.Description>Choose the Jellyfin playlist first, then connect its source and sync rules.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close playlist setup">×</Dialog.Close>
      </header>

      <nav class="playlist-add-steps" aria-label="Playlist setup progress">
        {#each ["Jellyfin playlist", "Source playlist", "Sync settings"] as label, index}
          <button class:active={step === index + 1} class:complete={step > index + 1} type="button" onclick={() => { if (index + 1 < step) step = index + 1; }}>
            <span>{index + 1}</span>{label}
          </button>
        {/each}
      </nav>

      <div class="playlist-add-body" aria-busy={loading}>
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

        {#if step === 1}
          <section class="playlist-add-step">
            <div class="dialog-section-heading">
              <div><strong>Choose the playlist in your media library</strong><small>This is the playlist Allstarr will keep synchronized.</small></div>
            </div>
            <fieldset class="audience-options playlist-targets">
              <legend>Media server</legend>
              {#each targets as target}
                <label class:active={targetId === target.id}>
                  <input type="radio" name="playlist-target" value={target.id} checked={targetId === target.id} onchange={() => void chooseTarget(target.id)} />
                  <ProviderMark id={target.protocol} definition={definition(target.protocol)} />
                  <span><strong>{target.displayName}</strong><small>{target.protocol} · {target.libraryScopeId || "music library"}</small></span>
                </label>
              {:else}<p class="notice-error">No linked Jellyfin or Subsonic target is available.</p>{/each}
            </fieldset>
            {#if targetId}
              <form class="playlist-add-search" onsubmit={(event) => { event.preventDefault(); void browseTargets(); }}>
                <SearchField class="field" bind:value={targetQuery} label="Find a Jellyfin playlist" placeholder="Playlist name" />
                <button class="button-secondary" type="submit" disabled={loading}>Search</button>
              </form>
              <fieldset class="audience-options playlist-add-list">
                <legend>Jellyfin playlists</legend>
                {#each targetPlaylists as playlist}
                  <label class:active={targetPlaylistId === playlist.id}>
                    <input bind:group={targetPlaylistId} type="radio" value={playlist.id} />
                    <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                    <span><strong>{playlist.name}</strong><small>{playlist.trackCount ?? "?"} tracks{playlist.description ? ` · ${playlist.description}` : ""}</small></span>
                  </label>
                {:else}{#if !loading}<p class="credential-safety">No writable playlists found.</p>{/if}{/each}
              </fieldset>
            {/if}
          </section>
        {:else if step === 2}
          <section class="playlist-add-step">
            {#if !accounts.length && !loading}
              <div class="compact-empty"><strong>No Playlist Sources are available</strong><p>Connect a Playlist-capable account under Sources first.</p><a class="button-primary" href="#/sources">Open Sources</a></div>
            {:else}
              <div class="playlist-source-groups">
                {#each providerIds as providerId}
                  <fieldset class="audience-options">
                    <legend>{definition(providerId)?.name ?? providerNames[providerId] ?? providerId}</legend>
                    {#each orderedAccounts.filter((item) => item.providerId === providerId) as account}
                      <label class:active={accountId === account.id}>
                        <input type="radio" name="playlist-source-account" value={account.id} checked={accountId === account.id} onchange={() => void chooseAccount(account.id)} />
                        <ProviderMark id={providerId} definition={definition(providerId)} />
                        <span><strong>{account.displayName}</strong><small>{account.ownerDisplayName ? `${account.ownerDisplayName} · ` : ""}{account.accessLabel}</small></span>
                      </label>
                    {/each}
                  </fieldset>
                {/each}
                {#if blocked}<p class="credential-safety">{blocked} shared Source {blocked === 1 ? "is" : "are"} hidden by deployment policy.</p>{/if}
              </div>
              {#if accountId}
                <form class="playlist-add-search" onsubmit={(event) => { event.preventDefault(); void browseSources(); }}>
                  <SearchField class="field" bind:value={sourceQuery} label="Find a source playlist" placeholder="Playlist name" />
                  <button class="button-secondary" type="submit" disabled={loading}>Search</button>
                </form>
                <fieldset class="audience-options playlist-add-list">
                  <legend>Source playlists</legend>
                  {#each sourcePlaylists as playlist}
                    <label class:active={sourcePlaylistId === playlist.id}>
                      <input bind:group={sourcePlaylistId} type="radio" value={playlist.id} />
                      <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                      <span><strong>{playlist.name}</strong><small>{playlist.owner || "Unknown owner"} · {playlist.trackCount ?? "?"} tracks</small></span>
                    </label>
                  {:else}{#if !loading}<p class="credential-safety">Choose a Source account to browse its playlists.</p>{/if}{/each}
                </fieldset>
                {#if sourceCursor}<button class="button-secondary" type="button" disabled={loading} onclick={() => void browseSources(sourceCursor)}>Load more</button>{/if}
              {/if}
            {/if}
          </section>
        {:else}
          <section class="playlist-add-step playlist-sync-settings">
            <div class="setting-field">
              <span><strong>Automatic sync</strong><small>Times use {Intl.DateTimeFormat().resolvedOptions().timeZone}.</small></span>
              <SelectField bind:value={schedule} label="Automatic sync" options={[
                { value: "manual", label: "Manual only" },
                { value: "hourly", label: "Every hour" },
                { value: "daily", label: "Daily at 3:00 AM" },
                { value: "weekly", label: "Mondays at 3:00 AM" },
              ]} />
            </div>
            <div class="setting-field">
              <span><strong>Playlist ordering</strong><small>Choose how local changes are handled.</small></span>
              <SelectField bind:value={syncBehavior} label="Playlist ordering" options={[
                { value: "preserve", label: "Keep local additions" },
                { value: "mirror", label: "Mirror source exactly" },
              ]} />
            </div>
            <fieldset class="playlist-sync-fields">
              <legend>Metadata to synchronize</legend>
              <label><input bind:checked={syncName} type="checkbox" /> Playlist name</label>
              <label><input bind:checked={syncDescription} type="checkbox" /> Description</label>
              <label><input bind:checked={syncArtwork} type="checkbox" /> Artwork</label>
            </fieldset>
          </section>
        {/if}
      </div>

      <footer class="playlist-add-footer">
        {#if step === 1}<Dialog.Close class="button-secondary">Cancel</Dialog.Close>{:else}<button class="button-secondary" type="button" onclick={() => step--}>Back</button>{/if}
        {#if step < 3}
          <button class="button-primary" type="button" disabled={!stepReady || loading} onclick={next}>Continue</button>
        {:else}
          <button class="button-primary" type="button" disabled={!sourcePlaylistId || !targetPlaylistId || !selectedLibraryScope || saving} onclick={() => void save()}>{saving ? "Linking…" : "Link playlist"}</button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
