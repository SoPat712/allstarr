<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { X } from "@lucide/svelte";
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
  import {
    orderPlaylistSources,
    playlistBehaviorSummary,
    playlistDestinationOptions,
    playlistProjectionOptions,
  } from "$lib/playlists";

  type DestinationMode = "virtual" | "materialized" | "hybrid";
  type ProjectionMode = "resolved" | "source" | "target";

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
  let mode = $state<DestinationMode>("materialized");
  let projectionMode = $state<ProjectionMode>("resolved");
  let materializationMode = $state<"reconcile" | "recreate">("reconcile");
  let syncBehavior = $state("preserve");
  let syncName = $state(true);
  let syncDescription = $state(true);
  let syncArtwork = $state(true);
  let loading = $state(false);
  let saving = $state(false);
  let error = $state("");
  let browseRequest = 0;
  let addBody = $state<HTMLElement>();

  const orderedAccounts = $derived(orderPlaylistSources(accounts, providerOrder));
  const providerIds = $derived([...new Set(orderedAccounts.map((item) => item.providerId))]);
  const selectedAccount = $derived(accounts.find((item) => item.id === accountId));
  const compatibleTargets = $derived(targets.filter((item) =>
    !selectedAccount?.libraryScopeId || !item.libraryScopeId ||
    item.libraryScopeId === selectedAccount.libraryScopeId));
  const selectedTarget = $derived(targets.find((item) => item.id === targetId));
  const selectedLibraryScope = $derived(selectedAccount?.libraryScopeId || selectedTarget?.libraryScopeId);
  const sourceName = $derived(selectedAccount?.displayName ?? "the source service");
  const sourcePlaylistName = $derived(
    sourcePlaylists.find((item) => item.id === sourcePlaylistId)?.name ?? "this playlist",
  );
  const targetName = $derived(
    selectedTarget?.protocol === "jellyfin"
      ? "Jellyfin"
      : selectedTarget?.protocol === "subsonic"
        ? "Subsonic"
        : selectedTarget?.displayName ?? "your media server",
  );
  const targetPlaylistName = $derived(
    targetPlaylists.find((item) => item.id === targetPlaylistId)?.name ?? `the selected ${targetName} playlist`,
  );
  const destinationOptions = $derived(playlistDestinationOptions(
    targetName,
    targetPlaylistName,
    sourcePlaylistName,
  ));
  const projectionOptions = $derived(playlistProjectionOptions(sourceName, targetName, targetPlaylistName));
  const updateCadence = $derived(schedule === "hourly"
    ? "every hour"
    : schedule === "daily"
      ? "every day at 3:00 AM"
      : schedule === "weekly"
        ? "every Monday at 3:00 AM"
        : undefined);
  const behaviorSummary = $derived(playlistBehaviorSummary(
    mode,
    materializationMode,
    sourcePlaylistName,
    targetName,
    targetPlaylistName,
    updateCadence,
  ));
  const needsTargetPlaylist = $derived(mode !== "virtual" || projectionMode === "target");
  const stepReady = $derived(
    step === 1
      ? Boolean(sourcePlaylistId)
      : step === 3
        ? Boolean(targetId) && (!needsTargetPlaylist || Boolean(targetPlaylistId))
        : true,
  );

  $effect(() => {
    step;
    queueMicrotask(() => addBody?.scrollTo({ top: 0 }));
  });

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
    browseRequest++;
    loading = true;
    error = "";
    step = 1;
    accounts = [];
    providerOrder = [];
    providerNames = {};
    blocked = 0;
    targets = [];
    targetId = "";
    targetPlaylistId = "";
    targetQuery = "";
    targetPlaylists = [];
    accountId = "";
    sourcePlaylistId = "";
    sourceQuery = "";
    sourcePlaylists = [];
    sourceCursor = "";
    schedule = "manual";
    mode = "materialized";
    projectionMode = "resolved";
    materializationMode = "reconcile";
    syncBehavior = "preserve";
    syncName = true;
    syncDescription = true;
    syncArtwork = true;
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
    if (needsTargetPlaylist) await browseTargets();
  }

  async function chooseDestination(value: DestinationMode) {
    mode = value;
    if (needsTargetPlaylist && targetId && !targetPlaylists.length) await browseTargets();
  }

  async function browseTargets() {
    if (!targetId) return;
    const requestedTarget = targetId;
    const request = ++browseRequest;
    targetPlaylistId = "";
    targetPlaylists = [];
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
    const libraryScopeId = accounts.find((item) => item.id === id)?.libraryScopeId;
    const compatible = targets.filter((item) =>
      !libraryScopeId || !item.libraryScopeId || item.libraryScopeId === libraryScopeId);
    if (!compatible.some((item) => item.id === targetId)) {
      targetId = compatible[0]?.id ?? "";
      targetPlaylistId = "";
      targetPlaylists = [];
    }
    await browseSources();
  }

  async function browseSources(cursor = "") {
    if (!accountId) return;
    const requestedAccount = accountId;
    const request = ++browseRequest;
    if (!cursor) {
      sourcePlaylistId = "";
      sourcePlaylists = [];
    }
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

  async function next() {
    if (!stepReady) return;
    step = Math.min(4, step + 1);
    if (step === 3 && needsTargetPlaylist && targetId && !targetPlaylists.length)
      await browseTargets();
  }

  async function save() {
    if (!selectedAccount || !selectedTarget || !selectedLibraryScope || !sourcePlaylistId ||
        (needsTargetPlaylist && !targetPlaylistId) || saving) return;
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
        targetPlaylistId: needsTargetPlaylist ? targetPlaylistId : null,
        mode,
        projectionMode,
        materializationMode,
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
      await onSaved(`Playlist linked. ${behaviorSummary}`);
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
          <Dialog.Description>Choose the original playlist, what listeners see, and whether Allstarr updates a playlist in your media server.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close playlist setup"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>

      <nav class="playlist-add-steps" aria-label="Playlist setup progress">
        {#each ["Source", "What listeners see", "Where it appears", "Updates"] as label, index}
          <button class:active={step === index + 1} class:complete={step > index + 1} type="button" onclick={() => { if (index + 1 < step) step = index + 1; }}>
            <span>{index + 1}</span>{label}
          </button>
        {/each}
      </nav>

      <div class="playlist-add-body" bind:this={addBody} aria-busy={loading}>
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

        {#if loading && step === 1 && !accounts.length}
          <div class="detail-loading" aria-busy="true">Loading playlist sources…</div>
        {:else if error && step === 1 && !accounts.length}
          <div class="compact-empty">
            <strong>Playlist setup is unavailable</strong>
            <Button variant="secondary" onclick={() => void load()}>Try again</Button>
          </div>
        {:else if step === 3}
          <section class="playlist-add-step">
            <div class="dialog-section-heading">
              <div><strong>Choose where listeners find this playlist</strong><small>Show it through Allstarr, add songs to a playlist in {targetName}, or do both.</small></div>
            </div>
            <fieldset class="audience-options playlist-mode-options">
              <legend>Where it appears</legend>
              {#each destinationOptions as option}
                <label class:active={mode === option.id}>
                  <input type="radio" name="playlist-destination" value={option.id} checked={mode === option.id} onchange={() => void chooseDestination(option.id)} />
                  <span><strong>{option.label}</strong><small>{option.description}</small></span>
                </label>
              {/each}
            </fieldset>
            <fieldset class="audience-options playlist-targets">
              <legend>Media server</legend>
              {#each compatibleTargets as target}
                <label class:active={targetId === target.id}>
                  <input type="radio" name="playlist-target" value={target.id} checked={targetId === target.id} onchange={() => void chooseTarget(target.id)} />
                  <ProviderMark id={target.protocol} definition={definition(target.protocol)} />
                  <span><strong>{target.displayName}</strong><small>{target.protocol} · {target.libraryScopeId || "music library"}</small></span>
                </label>
              {:else}<p class="notice-error">No media server is connected to this music library.</p>{/each}
            </fieldset>
            {#if targetId}
              {#if needsTargetPlaylist}
                <form class="playlist-add-search" onsubmit={(event) => { event.preventDefault(); void browseTargets(); }}>
                  <SearchField class="field" bind:value={targetQuery} label={`Find a playlist in ${targetName}`} placeholder="Playlist name" />
                  <Button variant="secondary" type="submit" disabled={loading}>Search</Button>
                </form>
                <fieldset class="audience-options playlist-add-list">
                  <legend>Playlists in {targetName}</legend>
                  {#each targetPlaylists as playlist}
                    <label class:active={targetPlaylistId === playlist.id}>
                      <input bind:group={targetPlaylistId} type="radio" value={playlist.id} />
                      <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                      <span><strong>{playlist.name}</strong><small>{playlist.trackCount ?? "?"} tracks{playlist.description ? ` · ${playlist.description}` : ""}</small></span>
                    </label>
                  {:else}{#if !loading}<p class="credential-safety">No writable playlists found.</p>{/if}{/each}
                </fieldset>
              {:else}
                <p class="credential-safety">Allstarr will show this playlist but will not create or change a playlist in {targetName}.</p>
              {/if}
            {/if}
          </section>
        {:else if step === 1}
          <section class="playlist-add-step">
            {#if !accounts.length && !loading}
              <div class="compact-empty"><strong>No Playlist Sources are available</strong><p>Connect a Playlist-capable account under Sources first.</p><Button href="#/sources">Open Sources</Button></div>
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
                  <Button variant="secondary" type="submit" disabled={loading}>Search</Button>
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
                {#if sourceCursor}<Button variant="secondary" disabled={loading} onclick={() => void browseSources(sourceCursor)}>Load more</Button>{/if}
              {/if}
            {/if}
          </section>
        {:else if step === 2}
          <section class="playlist-add-step playlist-sync-settings">
            <div class="dialog-section-heading">
              <div><strong>Choose what listeners see</strong><small>This does not change the original playlist in {sourceName}.</small></div>
            </div>
            <fieldset class="audience-options playlist-mode-options">
              <legend>What listeners see</legend>
              {#each projectionOptions as option}
                <label class:active={projectionMode === option.id}>
                  <input bind:group={projectionMode} type="radio" value={option.id} />
                  <span><strong>{option.label}</strong><small>{option.description}</small></span>
                </label>
              {/each}
            </fieldset>
          </section>
        {:else}
          <section class="playlist-add-step playlist-sync-settings">
            <p class="credential-safety">{behaviorSummary}</p>
            {#if mode !== "virtual"}
              <div class="setting-field">
                <span><strong>Which playlist Allstarr changes</strong><small>Change {targetPlaylistName}, or create a new playlist in {targetName} instead.</small></span>
                <SelectField bind:value={materializationMode} label={`How ${targetName} is updated`} options={[
                  { value: "reconcile", label: `Change ${targetPlaylistName}` },
                  { value: "recreate", label: `Create a new playlist in ${targetName}` },
                ]} />
              </div>
            {/if}
            <div class="setting-field">
              <span><strong>Automatic updates</strong><small>Times use {Intl.DateTimeFormat().resolvedOptions().timeZone}.</small></span>
              <SelectField bind:value={schedule} label="Automatic updates" options={[
                { value: "manual", label: "Manual only" },
                { value: "hourly", label: "Every hour" },
                { value: "daily", label: "Daily at 3:00 AM" },
                { value: "weekly", label: "Mondays at 3:00 AM" },
              ]} />
            </div>
            {#if mode !== "virtual"}
              {#if materializationMode === "reconcile"}
                <div class="setting-field">
                  <span><strong>When songs leave {sourcePlaylistName}</strong><small>Choose whether {targetPlaylistName} keeps songs that are no longer in {sourcePlaylistName}.</small></span>
                  <SelectField bind:value={syncBehavior} label={`Songs no longer in ${sourcePlaylistName}`} options={[
                    { value: "preserve", label: `Keep them in ${targetPlaylistName}` },
                    { value: "mirror", label: "Remove songs Allstarr previously added" },
                  ]} />
                </div>
              {/if}
              <fieldset class="playlist-sync-fields">
                <legend>{materializationMode === "recreate" ? "Copy these details to the new playlist" : "Keep these details updated"}</legend>
                <label><Checkbox bind:checked={syncName} /> Playlist name</label>
                <label><Checkbox bind:checked={syncDescription} /> Description</label>
                <label><Checkbox bind:checked={syncArtwork} /> Artwork</label>
              </fieldset>
            {/if}
          </section>
        {/if}
      </div>

      <footer class="playlist-add-footer">
        {#if step === 1}<Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>{:else}<Button variant="secondary" onclick={() => step--}>Back</Button>{/if}
        {#if step < 4}
          <Button disabled={!stepReady || loading} onclick={() => void next()}>Continue</Button>
        {:else}
          <Button disabled={!sourcePlaylistId || !targetId || (needsTargetPlaylist && !targetPlaylistId) || !selectedLibraryScope || saving} onclick={() => void save()}>{saving ? "Linking…" : "Link playlist"}</Button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
