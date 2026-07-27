<script lang="ts">
  import { Dialog } from "bits-ui";
  import {
    playlistLinks,
    type MediaTarget,
    type PlaylistDiscoveryItem,
    type PlaylistSourceAccount,
    type ProviderDefinition,
  } from "$lib/api";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
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
  let accounts = $state<PlaylistSourceAccount[]>([]);
  let providerOrder = $state<string[]>([]);
  let providerNames = $state<Record<string, string>>({});
  let blocked = $state(0);
  let targets = $state<MediaTarget[]>([]);
  let accountId = $state("");
  let targetId = $state("");
  let playlistId = $state("");
  let query = $state("");
  let playlists = $state<PlaylistDiscoveryItem[]>([]);
  let nextCursor = $state("");
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");
  let browseRequest = 0;

  const orderedAccounts = $derived(orderPlaylistSources(accounts, providerOrder));
  const providerIds = $derived([...new Set(orderedAccounts.map((item) => item.providerId))]);
  const selectedAccount = $derived(accounts.find((item) => item.id === accountId));
  const selectedTarget = $derived(targets.find((item) => item.id === targetId));

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
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Playlist Sources are unavailable.";
    } finally {
      loading = false;
    }
  }

  async function chooseAccount(id: string) {
    accountId = id;
    playlistId = "";
    query = "";
    playlists = [];
    nextCursor = "";
    await browse();
  }

  async function browse(cursor = "") {
    if (!accountId) return;
    const selectedAccountId = accountId;
    const request = ++browseRequest;
    loading = true;
    error = "";
    try {
      const response = await playlistLinks.sourcePlaylists(selectedAccountId, query.trim(), cursor);
      if (request !== browseRequest || selectedAccountId !== accountId) return;
      playlists = cursor ? [...playlists, ...response.items] : response.items;
      nextCursor = response.nextCursor ?? "";
    } catch (cause) {
      if (request !== browseRequest) return;
      error = cause instanceof Error ? cause.message : "Source playlists could not be loaded.";
    } finally {
      if (request === browseRequest) loading = false;
    }
  }

  async function save() {
    if (!selectedAccount || !selectedTarget || !playlistId || saving) return;
    saving = true;
    error = "";
    try {
      await playlistLinks.create({
        providerAccountId: selectedAccount.id,
        sourceProviderId: selectedAccount.providerId,
        sourcePlaylistId: playlistId,
        libraryScopeId: selectedAccount.libraryScopeId ||
          `${selectedTarget.protocol}:${selectedTarget.backendInstanceId}`,
        targetProtocol: selectedTarget.protocol,
        targetBackendInstanceId: selectedTarget.backendInstanceId,
        targetCredentialReferenceId: selectedTarget.credentialReferenceId,
      });
      open = false;
      await onSaved("Playlist added. Allstarr will match local tracks first and use configured fallback Sources.");
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Playlist could not be added.";
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
        <div><p class="eyebrow">Library intake</p><Dialog.Title>Add playlist</Dialog.Title><Dialog.Description>Choose any enabled Playlist Source. Allstarr creates a local-first virtual playlist on your media server.</Dialog.Description></div>
        <Dialog.Close class="icon-button" aria-label="Close playlist setup">×</Dialog.Close>
      </header>
      <div class="playlist-add-body" aria-busy={loading}>
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
        {#if !accounts.length && !loading}
          <div class="compact-empty"><strong>No Playlist Sources are available</strong><p>Connect a Playlist-capable account under Sources first.</p><a class="button-primary" href="#/sources">Open Sources</a></div>
        {:else}
          <div class="playlist-add-columns">
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
            <fieldset class="audience-options">
              <legend>Local playlist target</legend>
              {#each targets as target}
                <label class:active={targetId === target.id}>
                  <input bind:group={targetId} type="radio" value={target.id} />
                  <ProviderMark id={target.protocol} definition={definition(target.protocol)} />
                  <span><strong>{target.displayName}</strong><small>{target.protocol} · creates a new playlist</small></span>
                </label>
              {:else}<p class="notice-error">No linked Jellyfin or Subsonic target is available.</p>{/each}
            </fieldset>
          </div>

          {#if accountId}
            <form class="playlist-add-search" onsubmit={(event) => { event.preventDefault(); void browse(); }}>
              <SearchField class="field" bind:value={query} label="Search this Source" placeholder="Playlist name" />
              <button class="button-secondary" type="submit" disabled={loading}>Search</button>
            </form>
            <fieldset class="audience-options playlist-add-list">
              <legend>Source playlists</legend>
              {#each playlists as playlist}
                <label class:active={playlistId === playlist.id}>
                  <input bind:group={playlistId} type="radio" value={playlist.id} />
                  <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                  <span><strong>{playlist.name}</strong><small>{playlist.owner || "Unknown owner"} · {playlist.trackCount ?? "?"} tracks</small></span>
                </label>
              {:else}{#if !loading}<p class="credential-safety">No playlists found. Try another search.</p>{/if}{/each}
            </fieldset>
            {#if nextCursor}<button class="button-secondary" type="button" disabled={loading} onclick={() => void browse(nextCursor)}>Load more</button>{/if}
          {/if}
        {/if}
      </div>
      <footer class="playlist-add-footer">
        <Dialog.Close class="button-secondary">Cancel</Dialog.Close>
        <button class="button-primary" type="button" disabled={!playlistId || !targetId || saving} onclick={() => void save()}>{saving ? "Adding…" : "Add playlist"}</button>
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
