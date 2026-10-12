<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Check, Info, X } from "@lucide/svelte";
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
  let schedule = $state("once");
  let trackRetention = $state<"onDemand" | "keepAll">("onDemand");
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
  const selectedAccount = $derived(accounts.find((item) => item.id === accountId));
  const selectedTarget = $derived(targets.find((item) => item.id === targetId));
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
  const importMode = $derived<"oneTime" | "linked">(schedule === "once" ? "oneTime" : "linked");
  const behaviorSummary = $derived(playlistBehaviorSummary(
    mode,
    materializationMode,
    sourcePlaylistName,
    targetName,
    targetPlaylistName,
    updateCadence,
    importMode,
    trackRetention,
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

  function accountTitle(account: PlaylistSourceAccount) {
    return definition(account.providerId)?.name ?? providerNames[account.providerId] ?? account.displayName;
  }

  function accountDetail(account: PlaylistSourceAccount) {
    const title = accountTitle(account);
    return [account.displayName === title ? "" : account.displayName, account.ownerDisplayName, account.accessLabel]
      .filter(Boolean).join(" · ");
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
    schedule = "once";
    trackRetention = "onDemand";
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
    if (!targets.some((item) => item.id === targetId)) {
      targetId = targets[0]?.id ?? "";
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
    if (!selectedAccount || !selectedTarget || !sourcePlaylistId ||
        (needsTargetPlaylist && !targetPlaylistId) || saving) return;
    saving = true;
    error = "";
    try {
      const link = await playlistLinks.create({
        providerAccountId: selectedAccount.id,
        sourceProviderId: selectedAccount.providerId,
        sourcePlaylistId,
        targetProtocol: selectedTarget.protocol,
        targetBackendInstanceId: selectedTarget.backendInstanceId,
        targetCredentialReferenceId: selectedTarget.credentialReferenceId,
        targetPlaylistId: needsTargetPlaylist ? targetPlaylistId : null,
        mode,
        projectionMode,
        materializationMode,
        importMode,
        trackRetention,
        mirrorStaleEntries: syncBehavior === "mirror",
        preserveManualEntries: syncBehavior === "preserve",
        syncName,
        syncDescription,
        syncArtwork,
      });
      if (!["once", "manual"].includes(schedule)) {
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
      await onSaved(`${importMode === "oneTime" ? "Playlist import started" : "Playlist linked and initial import started"}. ${behaviorSummary}`);
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
    <Dialog.Content class="source-dialog playlist-add-dialog pl-dialog">
      <header>
        <div>
          <Dialog.Title>Import a playlist</Dialog.Title>
          <Dialog.Description>Bring a playlist from another service into Allstarr.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close playlist setup"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>

      <nav class="playlist-add-steps" aria-label="Playlist setup progress">
        {#each ["Source", "Listeners see", "Destination", "Updates"] as label, index}
          <button
            class:active={step === index + 1}
            class:complete={step > index + 1}
            type="button"
            disabled={index + 1 >= step}
            aria-current={step === index + 1 ? "step" : undefined}
            aria-label={index + 1 < step ? `Back to ${label}` : label}
            onclick={() => step = index + 1}
          >
            <span class="pl-step-dot" aria-hidden="true">{#if step > index + 1}<Check size={14} strokeWidth={3} />{:else}{index + 1}{/if}</span>
            <span class="pl-step-label">{label}</span>
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
        {:else if step === 1}
          {#if !accounts.length && !loading}
            <div class="compact-empty"><strong>Connect your music account first</strong><p>Your Spotify or other playlist account is encrypted, owned by you, and kept separate from other listeners.</p><Button href="#/integrations/accounts?source=spotify&connect=1">Connect Spotify</Button></div>
          {:else}
            <section class="pl-section">
              <h3 id="playlist-add-account">Import from</h3>
              <div class="pl-options pl-options-two" role="radiogroup" aria-labelledby="playlist-add-account">
                {#each orderedAccounts as account}
                  <label class="pl-option pl-option-media" class:active={accountId === account.id}>
                    <input type="radio" name="playlist-source-account" value={account.id} checked={accountId === account.id} onchange={() => void chooseAccount(account.id)} />
                    <ProviderMark id={account.providerId} definition={definition(account.providerId)} label={accountTitle(account)} />
                    <span class="pl-option-text"><strong>{accountTitle(account)}</strong><small>{accountDetail(account)}</small></span>
                  </label>
                {/each}
              </div>
              {#if blocked}<p class="pl-help">{blocked} shared {blocked === 1 ? "account is" : "accounts are"} hidden by deployment policy.</p>{/if}
            </section>
            {#if accountId}
              <section class="pl-section">
                <div><h3 id="playlist-add-source">Choose a playlist</h3><p class="pl-help">Showing playlists from {accountTitle(selectedAccount!)}.</p></div>
                <form class="pl-search" onsubmit={(event) => { event.preventDefault(); void browseSources(); }}>
                  <SearchField bind:value={sourceQuery} label="Find a source playlist" placeholder="Search playlists" hiddenLabel />
                  <Button variant="secondary" type="submit" disabled={loading}>Search</Button>
                </form>
                <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-source">
                  {#each sourcePlaylists as playlist}
                    <label class="pl-option pl-option-media" class:active={sourcePlaylistId === playlist.id}>
                      <input bind:group={sourcePlaylistId} type="radio" name="playlist-source" value={playlist.id} />
                      <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                      <span class="pl-option-text"><strong>{playlist.name}</strong><small>{playlist.owner || "Unknown owner"} · {playlist.trackCount ?? "?"} songs</small></span>
                    </label>
                  {:else}{#if !loading}<p class="pl-help">No playlists found.</p>{/if}{/each}
                </div>
                {#if sourceCursor}<Button class="pl-more" variant="secondary" disabled={loading} onclick={() => void browseSources(sourceCursor)}>Load more</Button>{/if}
              </section>
            {/if}
          {/if}
        {:else if step === 2}
          <section class="pl-section">
            <div><h3 id="playlist-add-view">What should listeners see?</h3><p class="pl-help">This never changes {sourcePlaylistName} in {sourceName}.</p></div>
            <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-view">
              {#each projectionOptions as option}
                <label class="pl-option" class:active={projectionMode === option.id}>
                  <input bind:group={projectionMode} type="radio" name="playlist-view" value={option.id} />
                  <span class="pl-option-text"><strong>{option.label}{#if option.id === "resolved"}<span class="pl-chip">Recommended</span>{/if}</strong><small>{option.description}</small></span>
                </label>
              {/each}
            </div>
          </section>
        {:else if step === 3}
          <section class="pl-section">
            <div><h3 id="playlist-add-destination">Where should it appear?</h3><p class="pl-help">Listeners can find it through Allstarr, in a {targetName} playlist, or both.</p></div>
            <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-destination">
              {#each destinationOptions as option}
                <label class="pl-option" class:active={mode === option.id}>
                  <input type="radio" name="playlist-destination" value={option.id} checked={mode === option.id} onchange={() => void chooseDestination(option.id)} />
                  <span class="pl-option-text"><strong>{option.label}</strong><small>{option.description}</small></span>
                </label>
              {/each}
            </div>
          </section>
          {#if targets.length !== 1}
            <section class="pl-section">
              <h3 id="playlist-add-target">Media server</h3>
              <div class="pl-options pl-options-two" role="radiogroup" aria-labelledby="playlist-add-target">
                {#each targets as target}
                  <label class="pl-option pl-option-media" class:active={targetId === target.id}>
                    <input type="radio" name="playlist-target" value={target.id} checked={targetId === target.id} onchange={() => void chooseTarget(target.id)} />
                    <ProviderMark id={target.protocol} definition={definition(target.protocol)} />
                    <span class="pl-option-text"><strong>{target.displayName}</strong><small>{definition(target.protocol)?.name ?? target.protocol}</small></span>
                  </label>
                {:else}<p class="notice-error">No media server is connected to this music library.</p>{/each}
              </div>
            </section>
          {/if}
          {#if targetId && needsTargetPlaylist}
            <section class="pl-section">
              <div><h3 id="playlist-add-target-playlist">{targetName} playlist</h3><p class="pl-help">Pick an existing playlist. You can create a new one on the next step instead.</p></div>
              <form class="pl-search" onsubmit={(event) => { event.preventDefault(); void browseTargets(); }}>
                <SearchField bind:value={targetQuery} label={`Find a playlist in ${targetName}`} placeholder={`Search ${targetName} playlists`} hiddenLabel />
                <Button variant="secondary" type="submit" disabled={loading}>Search</Button>
              </form>
              <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-target-playlist">
                {#each targetPlaylists as playlist}
                  <label class="pl-option pl-option-media" class:active={targetPlaylistId === playlist.id}>
                    <input bind:group={targetPlaylistId} type="radio" name="playlist-target-playlist" value={playlist.id} />
                    <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                    <span class="pl-option-text"><strong>{playlist.name}</strong><small>{playlist.trackCount ?? "?"} songs{playlist.description ? ` · ${playlist.description}` : ""}</small></span>
                  </label>
                {:else}{#if !loading}<p class="pl-help">No playlists you can change were found.</p>{/if}{/each}
              </div>
            </section>
          {/if}
        {:else}
          <div class="pl-summary" role="status">
            <Info size={20} aria-hidden="true" />
            <p><strong>What will happen</strong>{behaviorSummary}</p>
          </div>
          <div class="pl-rows">
            {#if mode !== "virtual"}
              <div class="pl-row">
                <div><strong>{targetName} playlist</strong><small>Add songs to the playlist you chose, or create a new one.</small></div>
                <SelectField bind:value={materializationMode} label={`${targetName} playlist`} options={[
                  { value: "reconcile", label: `Add to ${targetPlaylistName}` },
                  { value: "recreate", label: "Create a new playlist" },
                ]} />
              </div>
            {/if}
            <div class="pl-row">
              <div><strong>Update schedule</strong><small>Import once, or keep checking {sourceName} for changes. Times use {Intl.DateTimeFormat().resolvedOptions().timeZone}.</small></div>
              <SelectField bind:value={schedule} label="Update schedule" options={[
                { value: "once", label: "Import once" },
                { value: "manual", label: "Update when I ask" },
                { value: "hourly", label: "Every hour" },
                { value: "daily", label: "Daily at 3:00 AM" },
                { value: "weekly", label: "Mondays at 3:00 AM" },
              ]} />
            </div>
            {#if mode !== "virtual" && materializationMode === "reconcile"}
              <div class="pl-row">
                <div><strong>Songs removed from {sourcePlaylistName}</strong><small>What happens to them in {targetPlaylistName}.</small></div>
                <SelectField bind:value={syncBehavior} label={`Songs removed from ${sourcePlaylistName}`} options={[
                  { value: "preserve", label: "Keep them" },
                  { value: "mirror", label: "Remove songs Allstarr added" },
                ]} />
              </div>
            {/if}
            <div class="pl-row pl-row-full">
              <div><strong id="playlist-add-storage">Song storage</strong><small>Choose whether Allstarr saves permanent copies of these songs.</small></div>
              <div class="pl-options pl-options-two" role="radiogroup" aria-labelledby="playlist-add-storage">
                <label class="pl-option" class:active={trackRetention === "onDemand"}>
                  <input bind:group={trackRetention} type="radio" name="playlist-storage" value="onDemand" />
                  <span class="pl-option-text"><strong>Stream when played</strong><small>Songs play from their source and are cached briefly. Nothing is saved permanently.</small></span>
                </label>
                <label class="pl-option" class:active={trackRetention === "keepAll"}>
                  <input bind:group={trackRetention} type="radio" name="playlist-storage" value="keepAll" />
                  <span class="pl-option-text"><strong>Keep a copy of every song</strong><small>After each import, Allstarr downloads every song it can. Uses disk space.</small></span>
                </label>
              </div>
            </div>
            {#if mode !== "virtual"}
              <div class="pl-row pl-row-full" role="group" aria-labelledby="playlist-add-details">
                <div><strong id="playlist-add-details">{materializationMode === "recreate" ? "Copy to the new playlist" : "Keep in sync"}</strong><small>Copy these details from {sourceName} to the {targetName} playlist.</small></div>
                <div class="pl-checks">
                  <label><Checkbox bind:checked={syncName} /> Name</label>
                  <label><Checkbox bind:checked={syncDescription} /> Description</label>
                  <label><Checkbox bind:checked={syncArtwork} /> Artwork</label>
                </div>
              </div>
            {/if}
          </div>
        {/if}
      </div>

      <footer class="dialog-actions">
        {#if step === 1}<Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>{:else}<Button variant="secondary" onclick={() => step--}>Back</Button>{/if}
        {#if step < 4}
          <Button disabled={!stepReady || loading} onclick={() => void next()}>Continue</Button>
        {:else}
          <Button disabled={!sourcePlaylistId || !targetId || (needsTargetPlaylist && !targetPlaylistId) || saving} onclick={() => void save()}>{saving ? "Starting import…" : importMode === "oneTime" ? "Import playlist" : "Import and link"}</Button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
