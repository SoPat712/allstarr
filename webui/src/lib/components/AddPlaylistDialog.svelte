<script lang="ts">
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
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
  } from "$lib/playlists";

  type ImportMode = "oneTime" | "linked";
  type TargetMode = "new" | "existing";

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
  let importMode = $state<ImportMode>("oneTime");
  let schedule = $state<"hourly" | "daily" | "weekly">("daily");
  let targetMode = $state<TargetMode>("new");
  let trackRetention = $state<"onDemand" | "keepAll">("onDemand");
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
    targetMode === "new"
      ? `a new ${targetName} playlist`
      : targetPlaylists.find((item) => item.id === targetPlaylistId)?.name ?? `the selected ${targetName} playlist`,
  );
  const updateCadence = $derived(importMode === "linked"
    ? schedule === "hourly"
      ? "every hour"
      : schedule === "daily"
        ? "every day at 3:00 AM"
        : "every Monday at 3:00 AM"
    : undefined);
  const behaviorSummary = $derived(playlistBehaviorSummary(
    importMode,
    targetMode,
    sourcePlaylistName,
    targetName,
    targetPlaylistName,
    updateCadence,
    trackRetention,
  ));
  const stepReady = $derived(
    step === 1
      ? Boolean(sourcePlaylistId)
      : step === 3
        ? Boolean(targetId) && (targetMode === "new" || Boolean(targetPlaylistId))
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
      browseRequest++;
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
    importMode = "oneTime";
    schedule = "daily";
    targetMode = "new";
    trackRetention = "onDemand";
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
    if (targetMode === "existing") await browseTargets();
  }

  async function chooseTargetMode(value: TargetMode) {
    browseRequest++;
    loading = false;
    targetMode = value;
    if (value === "new") {
      targetPlaylistId = "";
      targetPlaylists = [];
      return;
    }
    if (targetId && !targetPlaylists.length) await browseTargets();
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
      if (request !== browseRequest || requestedTarget !== targetId || targetMode !== "existing" || !open) return;
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
    if (step === 3 && targetMode === "existing" && targetId && !targetPlaylists.length)
      await browseTargets();
  }

  async function save() {
    if (!selectedAccount || !selectedTarget || !sourcePlaylistId ||
        (targetMode === "existing" && !targetPlaylistId) || saving) return;
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
        targetPlaylistId: targetMode === "existing" ? targetPlaylistId : null,
        mode: "hybrid",
        projectionMode: "resolved",
        materializationMode: "reconcile",
        importMode,
        trackRetention,
        mirrorStaleEntries: true,
        preserveManualEntries: true,
        syncName: true,
        syncDescription: true,
        syncArtwork: true,
      });
      if (importMode === "linked") {
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
        {#each ["Source", "Import", "Destination", "Updates"] as label, index}
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
            <div><h3 id="playlist-add-import">Import behavior</h3><p class="pl-help">Allstarr keeps the original playlist unchanged and can show every song through its resolved view.</p></div>
            <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-import">
              <label class="pl-option" class:active={importMode === "oneTime"}>
                <input bind:group={importMode} type="radio" name="playlist-import-mode" value="oneTime" />
                <span class="pl-option-text"><strong>Import once</strong><small>Take a snapshot now and ignore later changes from {sourceName}.</small></span>
              </label>
              <label class="pl-option" class:active={importMode === "linked"}>
                <input bind:group={importMode} type="radio" name="playlist-import-mode" value="linked" />
                <span class="pl-option-text"><strong>Keep in sync</strong><small>Check {sourceName} for changes on the schedule you choose.</small></span>
              </label>
            </div>
          </section>
        {:else if step === 3}
          <section class="pl-section">
            <div><h3 id="playlist-add-destination">Destination</h3><p class="pl-help">Listeners can find every source song through Allstarr, and playable songs are also kept in a {targetName} playlist.</p></div>
            <div class="pl-options pl-options-two" role="radiogroup" aria-labelledby="playlist-add-destination">
              <label class="pl-option" class:active={targetMode === "new"}>
                <input bind:group={targetMode} type="radio" name="playlist-target-mode" value="new" onchange={() => void chooseTargetMode("new")} />
                <span class="pl-option-text"><strong>New playlist</strong><small>Create a new playlist in {targetName} using the source name.</small></span>
              </label>
              <label class="pl-option" class:active={targetMode === "existing"}>
                <input bind:group={targetMode} type="radio" name="playlist-target-mode" value="existing" onchange={() => void chooseTargetMode("existing")} />
                <span class="pl-option-text"><strong>Existing playlist</strong><small>Keep the playable songs in a playlist you already manage.</small></span>
              </label>
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
          {#if targetId && targetMode === "existing"}
            <section class="pl-section">
              <div><h3 id="playlist-add-target-playlist">{targetName} playlist</h3><p class="pl-help">Pick the existing playlist that Allstarr should keep updated.</p></div>
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
            <div class="pl-row">
              <div><strong>Update schedule</strong><small>{importMode === "oneTime" ? `Import once. Later changes from ${sourceName} are ignored.` : `Keep checking ${sourceName} for changes. Times use ${Intl.DateTimeFormat().resolvedOptions().timeZone}.`}</small></div>
              {#if importMode === "linked"}
                <SelectField bind:value={schedule} label="Update schedule" options={[
                  { value: "hourly", label: "Every hour" },
                  { value: "daily", label: "Daily at 3:00 AM" },
                  { value: "weekly", label: "Mondays at 3:00 AM" },
                ]} />
              {:else}
                <strong>Import once</strong>
              {/if}
            </div>
            <div class="pl-row pl-row-full">
              <label class="pl-retention-toggle">
                <Checkbox checked={trackRetention === "keepAll"} onCheckedChange={(checked) => { trackRetention = checked ? "keepAll" : "onDemand"; }} />
                <span><strong>Keep downloaded copies</strong><small>Save permanent copies of external songs Allstarr can download. Local songs are already permanent.</small></span>
              </label>
            </div>
          </div>
        {/if}
      </div>

      <footer class="dialog-actions">
        {#if step === 1}<Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>{:else}<Button variant="secondary" onclick={() => step--}>Back</Button>{/if}
        {#if step < 4}
          <Button disabled={!stepReady || loading} onclick={() => void next()}>Continue</Button>
        {:else}
          <Button disabled={!sourcePlaylistId || !targetId || (targetMode === "existing" && !targetPlaylistId) || saving} onclick={() => void save()}>{saving ? "Starting import…" : importMode === "oneTime" ? "Import playlist" : "Import and link"}</Button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
