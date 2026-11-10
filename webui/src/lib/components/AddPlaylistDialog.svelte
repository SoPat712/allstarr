<script lang="ts">
  import { countLabel } from "$lib/count-label";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Check, Info, Link, X } from "@lucide/svelte";
  import {
    playlistLinks,
    json,
    type MediaTarget,
    type PlaylistLink,
    type PlaylistDiscoveryItem,
    type PlaylistSourceAccount,
    type ProviderDefinition,
    type TargetPlaylist,
  } from "$lib/api";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { lastFmPeriods } from "$lib/intelligence";
  import {
    orderPlaylistSources,
    playlistBehaviorSummary,
    playlistSkippedSongsMessage,
  } from "$lib/playlists";

  type ImportMode = "oneTime" | "linked";
  type LinkPreview = {
    source: { id: string; displayName: string };
    kind: "playlist";
    id: string;
    providerAccountId: string | null;
    title: string;
    owner: string;
    trackCount: number | null;
    artworkUrl: string | null;
  };
  type TargetMode = "new" | "existing";
  type LastfmPeriod = "7day" | "1month" | "3month" | "12month" | "overall";
  type InitialSource = {
    accountId: string;
    playlistId: string;
    title: string;
    followNewestKind?: string;
  };

  let {
    open = $bindable(false),
    providers,
    onSaved,
    initialSource = null,
    existingPlaylists = [],
  }: {
    open: boolean;
    providers: ProviderDefinition[];
    onSaved: (message: string) => void | Promise<void>;
    initialSource?: InitialSource | null;
    existingPlaylists?: PlaylistLink[];
  } = $props();

  type PlaylistChoice = { account: PlaylistSourceAccount; playlist: PlaylistDiscoveryItem };
  let groupCursors = $state<Record<string, string | null>>({});
  let groupedPlaylists = $state<Record<string, PlaylistDiscoveryItem[]>>({});
  let chosenPlaylists = $state<PlaylistChoice[]>([]);
  let groupErrors = $state<string[]>([]);
  function togglePlaylist(account: PlaylistSourceAccount, playlist: PlaylistDiscoveryItem, checked: boolean) {
    clearLink(); publicProviderId = ""; clearFollowNewest();
    chosenPlaylists = chosenPlaylists.filter(item => item.account.id !== account.id || item.playlist.id !== playlist.id);
    if (checked) chosenPlaylists = [...chosenPlaylists, { account, playlist }];
    if (chosenPlaylists.length > 1) targetMode = "new";
    const first = chosenPlaylists[0];
    accountId = first?.account.id ?? "";
    sourcePlaylistId = first?.playlist.id ?? "";
    sourcePlaylists = first ? [first.playlist] : [];
  }
  let pastedUrl = $state("");
  let linkPreview = $state<LinkPreview | null>(null);
  let resolvingLink = $state(false);
  let linkRequest = 0;
  let prepared = $state(false);
  let step = $state(1);
  let sourceTab = $state("accounts");
  let accounts = $state<PlaylistSourceAccount[]>([]);
  let providerOrder = $state<string[]>([]);
  let providerNames = $state<Record<string, string>>({});
  let publicProviders = $state<Array<{ id: string; displayName: string }>>([]);
  let publicProviderId = $state("");
  let sourceReference = $state("");
  let publicUserReference = $state("");
  let listedPublicUser = $state("");
  let publicListTruncated = $state(false);
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
  let lastfmPeriod = $state<LastfmPeriod>("overall");
  let followNewestKind = $state("");
  let followNewest = $state(false);
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
  const isLastfmAccount = $derived(selectedAccount?.providerId.toLowerCase() === "lastfm");
  const hasLastfmAccount = $derived(accounts.some((item) => item.providerId.toLowerCase() === "lastfm"));
  const selectedTarget = $derived(targets.find((item) => item.id === targetId));
  const sourceName = $derived(chosenPlaylists.length > 1 ? [...new Set(chosenPlaylists.map(item => accountTitle(item.account)))].join(" and ") : linkPreview?.source.displayName ?? selectedAccount?.displayName ?? providerNames[publicProviderId] ?? "the source service");
  const sourcePlaylistName = $derived(
    (chosenPlaylists.length > 1 ? countLabel(chosenPlaylists.length, "playlist") : null) ?? linkPreview?.title ?? sourcePlaylists.find((item) => item.id === sourcePlaylistId)?.name ??
      (isLastfmAccount ? lastfmSourceName(sourcePlaylistId) : "this playlist"),
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
  const sourceCount = $derived(chosenPlaylists.length ? chosenPlaylists.every(item => item.playlist.trackCount != null) ? chosenPlaylists.reduce((sum, item) => sum + (item.playlist.trackCount ?? 0), 0) : null : linkPreview?.trackCount ?? sourcePlaylists.find(item => item.id === sourcePlaylistId)?.trackCount ?? null);
  const stepReady = $derived(
    step === 1
      ? Boolean(sourcePlaylistId) && !resolvingLink
      : step === 2
        ? Boolean(targetId) && (targetMode === "new" || Boolean(targetPlaylistId))
        : true,
  );

  $effect(() => {
    step;
    queueMicrotask(() => addBody?.scrollTo({ top: 0 }));
  });

  function changeSourceTab(value: string) {
    sourceTab = value;
    clearLink(); chosenPlaylists = []; sourcePlaylistId = ""; accountId = ""; publicProviderId = "";
  }

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

  function lastfmSourceName(id: string) {
    return id === "loved" ? "Loved tracks" : id === "similar" ? "Similar tracks" : "Top tracks";
  }

  function changeLastfmPeriod(value: string) {
    lastfmPeriod = value as LastfmPeriod;
    if (sourcePlaylistId.startsWith("top:")) sourcePlaylistId = `top:${lastfmPeriod}:50`;
  }

  function clearFollowNewest() {
    followNewestKind = "";
    followNewest = false;
  }

  $effect(() => {
    if (!open) {
      browseRequest++;
      linkRequest++;
      prepared = false;
      return;
    }
    if (prepared) return;
    prepared = true;
    void load();
  });

  async function load() {
    const request = ++browseRequest;
    loading = true;
    error = "";
    step = 1;
    pastedUrl = "";
    clearLink();
    accounts = []; groupedPlaylists = {}; chosenPlaylists = []; groupErrors = []; groupCursors = {};
    providerOrder = [];
    providerNames = {};
    publicProviders = [];
    publicProviderId = "";
    sourceReference = "";
    publicUserReference = "";
    listedPublicUser = "";
    publicListTruncated = false;
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
    lastfmPeriod = "overall";
    clearFollowNewest();
    schedule = "daily";
    targetMode = "new";
    trackRetention = "onDemand";
    try {
      const [sourceResponse, targetResponse] = await Promise.all([
        playlistLinks.sources(), playlistLinks.targets(),
      ]);
      if (!open || request !== browseRequest) return;
      accounts = sourceResponse.accounts;
      publicProviders = sourceResponse.providers.filter((item) => item.supportsPublicRead);
      providerOrder = sourceResponse.providers.map((item) => item.id);
      providerNames = Object.fromEntries(sourceResponse.providers.map((item) => [item.id, item.displayName]));
      blocked = sourceResponse.blockedAccounts.length;
      targets = targetResponse.targets;
      targetId = targets[0]?.id ?? "";
      for (const account of orderedAccounts.filter(item => item.providerId.toLowerCase() !== "lastfm")) {
        try {
          const response = await playlistLinks.sourcePlaylists(account.id);
          if (!open || request !== browseRequest) return;
          groupedPlaylists = { ...groupedPlaylists, [account.id]: [...new Map([...(account.providerId === "spotify" ? [{ id: "liked-songs", providerId: "spotify", name: "Liked Songs", trackCount: null }] : []), ...response.items].map(item => [item.id, item])).values()] };
          groupCursors = { ...groupCursors, [account.id]: response.nextCursor ?? null };
        } catch { groupErrors = [...groupErrors, account.id]; }
      }
      if (initialSource) {
        const account = accounts.find((item) => item.id === initialSource.accountId);
        if (account) {
          accountId = account.id;
          sourcePlaylistId = initialSource.playlistId;
          sourcePlaylists = [{ id: initialSource.playlistId, providerId: account.providerId, name: initialSource.title }];
          followNewestKind = initialSource.followNewestKind ?? "";
          followNewest = Boolean(followNewestKind);
          if (followNewest) importMode = "linked";
          step = 2;
        } else {
          error = "Connect the source account again to import this playlist.";
        }
      }
    } catch (cause) {
      if (request !== browseRequest || !open) return;
      error = cause instanceof Error ? cause.message : "Playlist setup is unavailable.";
    } finally {
      if (request === browseRequest) loading = false;
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

  async function moreAccountPlaylists(account: PlaylistSourceAccount) {
    const cursor = groupCursors[account.id];
    if (!cursor || loading) return;
    const request = browseRequest;
    loading = true;
    try {
      const response = await playlistLinks.sourcePlaylists(account.id, "", cursor);
      if (!open || request !== browseRequest) return;
      groupedPlaylists = { ...groupedPlaylists, [account.id]: [...new Map([...(groupedPlaylists[account.id] ?? []), ...response.items].map(item => [item.id, item])).values()] };
      groupCursors = { ...groupCursors, [account.id]: response.nextCursor ?? null };
    } catch { error = "More playlists could not be loaded. Try again."; }
    finally { if (request === browseRequest) loading = false; }
  }

  async function chooseAccount(id: string) {
    chosenPlaylists = [];
    browseRequest++;
    loading = false;
    clearLink();
    clearFollowNewest();
    publicProviderId = "";
    sourceReference = "";
    publicUserReference = "";
    listedPublicUser = "";
    publicListTruncated = false;
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
    if (selectedAccount?.providerId.toLowerCase() !== "lastfm") await browseSources();
  }

  function choosePublicProvider(id: string) {
    chosenPlaylists = [];
    clearLink();
    clearFollowNewest();
    browseRequest++;
    loading = false;
    accountId = "";
    publicProviderId = id;
    sourcePlaylistId = "";
    sourceReference = "";
    publicUserReference = "";
    listedPublicUser = "";
    publicListTruncated = false;
    sourcePlaylists = [];
    sourceCursor = "";
    error = "";
  }

  function clearLink() {
    linkRequest++;
    if (linkPreview) sourcePlaylistId = "";
    linkPreview = null;
    resolvingLink = false;
  }

  async function resolveLink() {
    chosenPlaylists = [];
    clearLink();
    clearFollowNewest();
    accountId = "";
    publicProviderId = "";
    sourcePlaylists = [];
    listedPublicUser = "";
    sourcePlaylistId = "";
    const url = pastedUrl.trim();
    if (!url) return;
    const request = ++linkRequest;
    resolvingLink = true;
    error = "";
    try {
      const preview = await json<LinkPreview>("/api/admin/playlist-links/resolve-url", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url }),
      });
      if (request !== linkRequest || !open) return;
      linkPreview = preview;
      sourcePlaylistId = preview.id;
      accountId = "";
      publicProviderId = "";
      sourcePlaylists = [];
      listedPublicUser = "";
    } catch (cause) {
      if (request !== linkRequest) return;
      error = cause instanceof Error ? cause.message : "No source can read this link.";
    } finally {
      if (request === linkRequest) resolvingLink = false;
    }
  }

  function useReference() {
    browseRequest++;
    loading = false;
    clearFollowNewest();
    sourcePlaylistId = sourceReference.trim();
    error = "";
  }

  function clearPublicProfile() {
    browseRequest++;
    loading = false;
    listedPublicUser = "";
    sourcePlaylistId = "";
    sourcePlaylists = [];
    sourceCursor = "";
    publicListTruncated = false;
    error = "";
  }

  async function browseSources(cursor = "") {
    const requestedAccount = accountId;
    const requestedProvider = publicProviderId;
    const requestedUser = publicUserReference.trim();
    if (!requestedAccount && (!requestedProvider || !requestedUser)) return;
    const request = ++browseRequest;
    if (!cursor) {
      clearFollowNewest();
      sourcePlaylistId = "";
      sourcePlaylists = [];
    }
    loading = true;
    error = "";
    try {
      const response = requestedProvider
        ? await playlistLinks.publicUserPlaylists(requestedProvider, requestedUser, cursor)
        : await playlistLinks.sourcePlaylists(requestedAccount, sourceQuery.trim(), cursor);
      if (request !== browseRequest || requestedAccount !== accountId || requestedProvider !== publicProviderId ||
        (requestedProvider && requestedUser !== publicUserReference.trim())) return;
      sourcePlaylists = cursor ? [...sourcePlaylists, ...response.items] : response.items;
      sourceCursor = response.nextCursor ?? "";
      listedPublicUser = requestedProvider ? requestedUser : "";
      publicListTruncated = !!requestedProvider && !!response.isPartial && !response.nextCursor;
    } catch (cause) {
      if (request !== browseRequest) return;
      error = cause instanceof Error ? cause.message : "Source playlists could not be loaded.";
    } finally {
      if (request === browseRequest) loading = false;
    }
  }

  async function next() {
    if (!stepReady) return;
    step = 2;
    if (step === 2 && targetMode === "existing" && targetId && !targetPlaylists.length)
      await browseTargets();
  }

  async function save() {
    if ((!linkPreview && !selectedAccount && !publicProviderId) || !selectedTarget || !sourcePlaylistId ||
        (targetMode === "existing" && !targetPlaylistId) || saving) return;
    if (followNewest) importMode = "linked";
    saving = true;
    error = "";
    const completedSummary = behaviorSummary;
    const selections = chosenPlaylists.length ? [...chosenPlaylists] : [null];
    let completed = 0;
    let schedulesFailed = 0;
    let skippedItemCount = 0;
    let restored = 0;
    try {
      for (const choice of selections) {
        const link = await playlistLinks.create({
          providerAccountId: choice?.account.id ?? linkPreview?.providerAccountId ?? selectedAccount?.id ?? null,
          sourceProviderId: choice?.account.providerId ?? linkPreview?.source.id ?? selectedAccount?.providerId ?? publicProviderId,
          sourcePlaylistId: choice?.playlist.id ?? sourcePlaylistId,
          targetProtocol: selectedTarget.protocol,
          targetBackendInstanceId: selectedTarget.backendInstanceId,
          targetCredentialReferenceId: selectedTarget.credentialReferenceId,
          targetPlaylistId: targetMode === "existing" ? targetPlaylistId : null,
          mode: "hybrid",
          projectionMode: "resolved",
          materializationMode: "reconcile",
          importMode,
          ...(followNewest && followNewestKind ? { followNewest: true } : {}),
          trackRetention,
          mirrorStaleEntries: true,
          preserveManualEntries: true,
          syncName: true,
          syncDescription: true,
          syncArtwork: true,
        });
        skippedItemCount += link.skippedItemCount ?? 0;
        if (link.restoredToApps) restored++;
        if (importMode === "linked" && !link.scheduleId && !link.restoredToApps) {
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
            schedulesFailed++;
          }
        }
        completed++;
        if (choice) {
          chosenPlaylists = chosenPlaylists.filter(item => item.account.id !== choice.account.id || item.playlist.id !== choice.playlist.id);
          const remaining = chosenPlaylists[0];
          if (remaining) { accountId = remaining.account.id; sourcePlaylistId = remaining.playlist.id; sourcePlaylists = [remaining.playlist]; }
        }
      }
      open = false;
      const message = restored === completed
        ? `${completed > 1 ? countLabel(completed, "playlist") : "Playlist"} added back to your apps.`
        : `${completed > 1 ? `${countLabel(completed, "playlist")} started` : importMode === "oneTime" ? "Playlist import started" : "Playlist linked and initial import started"}. ${completedSummary}`;
      await onSaved(`${message}${skippedItemCount ? ` ${playlistSkippedSongsMessage(skippedItemCount)}` : ""}${schedulesFailed ? ` ${countLabel(schedulesFailed, "automatic schedule could not", "automatic schedules could not")} be saved. Open the playlist’s sync settings to try again.` : ""}`);
    } catch (cause) {
      error = `${completed ? `${countLabel(completed, "playlist")} started. ` : ""}${cause instanceof Error ? cause.message : "Playlist could not be linked."}`;
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
          <Dialog.Description>Bring a playlist into your apps. Nothing changes on the service.</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close playlist setup"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>

      <nav class="playlist-add-steps" aria-label="Playlist setup progress">
        {#each ["Choose", "Options and review"] as label, index}
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
        {#if step === 1}
          <SegmentedNav variant="segments" label="Choose playlist source" items={[{ id: "link", label: "Paste a link" }, { id: "accounts", label: "From my accounts" }]} active={sourceTab} onchange={changeSourceTab} />
          {#if sourceTab === "link"}<section class="pl-section">
            <form class="url-import-form" onsubmit={(event) => { event.preventDefault(); void resolveLink(); }}>
              <label class="url-import-field">
                <span>Paste a link</span>
                <span class="url-import-input">
                  <Link size={18} aria-hidden="true" />
                  <input bind:value={pastedUrl} type="text" placeholder="Paste a playlist or album link"
                    oninput={() => { clearLink(); sourcePlaylistId = ""; error = ""; }}
                    onpaste={(event) => {
                      const text = event.clipboardData?.getData("text");
                      if (!text) return;
                      event.preventDefault();
                      pastedUrl = text;
                      void resolveLink();
                    }} />
                </span>
              </label>
            </form>
            <p class="pl-help">ListenBrainz playlist links and bare playlist MBIDs are supported.</p>
            {#if resolvingLink}<p class="pl-help" role="status">Reading link…</p>{/if}
            {#if linkPreview}
              <div class="url-import-preview" role="status">
                <MediaArtwork class="url-import-art" url={linkPreview.artworkUrl} fallback="♫" />
                <div class="url-import-details">
                  <strong>{linkPreview.title}</strong>
                  <span>Playlist · {linkPreview.trackCount == null ? "? songs" : countLabel(linkPreview.trackCount, "song")} · from {linkPreview.source.displayName}</span>
                  <small>{linkPreview.owner}</small>
                </div>
                <ProviderMark id={linkPreview.source.id} definition={definition(linkPreview.source.id)} label={linkPreview.source.displayName} />
              </div>
            {/if}
          </section>{/if}
        {/if}
        {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

        {#if loading && step === 1 && !accounts.length && !publicProviders.length}
          <RowsSkeleton count={4} />
        {:else if error && step === 1 && !accounts.length && !publicProviders.length}
          <div class="compact-empty">
            <strong>Playlist setup is unavailable</strong>
            <Button variant="secondary" onclick={() => void load()}>Try again</Button>
          </div>
        {:else if step === 1 && sourceTab === "accounts"}
          {#if !accounts.length && !publicProviders.length && !loading}
            {#if !linkPreview}
              <div class="compact-empty"><strong>Connect your music account first</strong><p>Your Spotify or other playlist account is encrypted, owned by you, and kept separate from other listeners.</p><Button href="#/sources/accounts?source=spotify&connect=1">Connect Spotify</Button></div>
            {/if}
          {:else}
            {#if sourceTab === "accounts"}
              <section class="account-playlists" aria-label="Playlists from your accounts">
                <SearchField bind:value={sourceQuery} label="Filter account playlists" placeholder="Search playlists" hiddenLabel />
                {#each orderedAccounts.filter(item => item.providerId.toLowerCase() !== "lastfm") as account}
                  <section class="account-playlist-group" aria-label={`${accountTitle(account)} · ${account.displayName}`}>
                    <h3>{accountTitle(account)} · {accountDetail(account) || account.displayName}</h3>
                    {#each (groupedPlaylists[account.id] ?? []).filter(item => !sourceQuery || item.name.toLocaleLowerCase().includes(sourceQuery.toLocaleLowerCase())) as playlist}
                      {@const added = existingPlaylists.some(item => item.enabled && item.providerAccountId === account.id && item.sourcePlaylistId === playlist.id)}
                      <label class="account-playlist-choice">
                        <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                        <span><strong>{playlist.name}</strong><small>{playlist.trackCount == null ? "Song count unavailable" : countLabel(playlist.trackCount, "song")}{added ? " · Already in your apps" : ""}</small></span>
                        <Checkbox checked={chosenPlaylists.some(item => item.account.id === account.id && item.playlist.id === playlist.id)} disabled={added} aria-label={`${playlist.name}${added ? ", already in your apps" : ""}`} onCheckedChange={checked => togglePlaylist(account, playlist, checked === true)} />
                      </label>
                    {:else}<p class="pl-help">{loading ? "Loading playlists…" : groupErrors.includes(account.id) ? "Playlists could not be loaded. Reopen to try again." : "No playlists found."}</p>{/each}
                    {#if groupCursors[account.id]}<Button variant="text" size="sm" disabled={loading} onclick={() => void moreAccountPlaylists(account)}>Show more playlists</Button>{/if}
                  </section>
                {/each}
                {#if chosenPlaylists.length}<p class="pl-help" role="status">{countLabel(chosenPlaylists.length, "playlist")} selected{sourceCount == null ? "" : ` · ${countLabel(sourceCount, "song")}`}</p>{/if}
              </section>
            {/if}
            <details class="other-sources"><summary>Other playlist sources</summary>
            <section class="pl-section">
              <h3 id="playlist-add-account">Import from</h3>
              <div class="pl-options pl-options-two" role="radiogroup" aria-labelledby="playlist-add-account">
                {#each publicProviders as provider}
                  <label class="pl-option pl-option-media" class:active={publicProviderId === provider.id}>
                    <input type="radio" name="playlist-source-account" value={`public:${provider.id}`} checked={publicProviderId === provider.id} onchange={() => choosePublicProvider(provider.id)} />
                    <ProviderMark id={provider.id} definition={definition(provider.id)} />
                    <span class="pl-option-text"><strong>Public {provider.displayName} playlist</strong><small>Paste a playlist link. No account needed.</small></span>
                  </label>
                {/each}
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
            {#if publicProviderId || selectedAccount?.providerId === "spotify"}
              <section class="pl-section">
                <h3>Use a playlist link</h3>
                <p class="pl-help">{publicProviderId ? "Only public playlists can be read without an account." : "Private playlists use only the selected account."}</p>
                <form class="pl-search" onsubmit={(event) => { event.preventDefault(); useReference(); }}>
                  <SearchField bind:value={sourceReference} label="Playlist link, URI or ID" placeholder="Playlist link, URI or ID" hiddenLabel />
                  <Button variant="secondary" type="submit" disabled={!sourceReference.trim()}>Use playlist</Button>
                </form>
                {#if sourcePlaylistId && sourcePlaylistId === sourceReference.trim()}<p class="pl-help">Playlist selected. Access will be checked when the import runs.</p>{/if}
              </section>
            {/if}
            {#if publicProviderId === "spotify"}
              <section class="pl-section">
                <div><h3>Browse a Spotify user</h3><p class="pl-help">Choose a public playlist from a user you follow or another public profile.</p></div>
                <form class="pl-search" oninput={clearPublicProfile} onsubmit={(event) => { event.preventDefault(); void browseSources(); }}>
                  <SearchField bind:value={publicUserReference} label="Spotify user link, URI or ID" placeholder="Spotify user link, URI or ID" hiddenLabel />
                  <Button variant="secondary" type="submit" disabled={loading || !publicUserReference.trim()}>Browse playlists</Button>
                </form>
              </section>
            {/if}
            {#if isLastfmAccount}
              <section class="pl-section">
                <div><h3 id="playlist-add-lastfm">Choose your Last.fm tracks</h3><p class="pl-help">Use music from your connected personal account.</p></div>
                <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-lastfm">
                  <label class="pl-option" class:active={sourcePlaylistId === "loved"}>
                    <input bind:group={sourcePlaylistId} type="radio" name="playlist-source" value="loved" />
                    <span class="pl-option-text"><strong>Loved tracks</strong><small>Songs you have loved on Last.fm.</small></span>
                  </label>
                  <label class="pl-option" class:active={sourcePlaylistId.startsWith("top:")}>
                    <input type="radio" name="playlist-source" value={`top:${lastfmPeriod}:50`} checked={sourcePlaylistId.startsWith("top:")} onchange={() => sourcePlaylistId = `top:${lastfmPeriod}:50`} />
                    <span class="pl-option-text"><strong>Top tracks</strong><small>Your 50 most played songs for the selected period.</small></span>
                  </label>
                  <label class="pl-option" class:active={sourcePlaylistId === "similar"}>
                    <input bind:group={sourcePlaylistId} type="radio" name="playlist-source" value="similar" />
                    <span class="pl-option-text"><strong>Similar tracks</strong><small>Recommendations based on your listening.</small></span>
                  </label>
                </div>
                {#if sourcePlaylistId.startsWith("top:")}
                  <SelectField value={lastfmPeriod} onchange={changeLastfmPeriod} label="Top tracks period" options={lastFmPeriods} />
                {/if}
              </section>
            {:else if accountId || listedPublicUser}
              <section class="pl-section">
                <div><h3 id="playlist-add-source">Choose a playlist</h3><p class="pl-help">{listedPublicUser ? "Showing public playlists from this Spotify profile." : `Showing playlists from ${accountTitle(selectedAccount!)}.`}</p></div>
                {#if accountId}<form class="pl-search" onsubmit={(event) => { event.preventDefault(); void browseSources(); }}>
                  <SearchField bind:value={sourceQuery} label="Find a source playlist" placeholder="Search playlists" hiddenLabel />
                  <Button variant="secondary" type="submit" disabled={loading}>Search</Button>
                </form>{/if}
                <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-source">
                  {#if selectedAccount?.providerId === "spotify"}
                    <label class="pl-option" class:active={sourcePlaylistId === "liked-songs"}>
                      <input bind:group={sourcePlaylistId} type="radio" name="playlist-source" value="liked-songs" />
                      <span class="pl-option-text"><strong>Liked Songs</strong><small>Saved songs from this Spotify account.</small></span>
                    </label>
                  {/if}
                  {#each sourcePlaylists as playlist}
                    {@const alreadyAdded = existingPlaylists.some(item => item.enabled && item.providerAccountId === accountId && item.sourcePlaylistId === playlist.id && item.sourceProviderId === selectedAccount?.providerId)}
                    <label class="pl-option pl-option-media" class:active={sourcePlaylistId === playlist.id}>
                      <input bind:group={sourcePlaylistId} type="radio" name="playlist-source" value={playlist.id} disabled={alreadyAdded} />
                      <MediaArtwork class="playlist-art" url={playlist.artworkUrl} fallback="♫" />
                      <span class="pl-option-text"><strong>{playlist.name}</strong><small>{playlist.owner || "Unknown owner"} · {playlist.trackCount == null ? "? songs" : countLabel(playlist.trackCount, "song")}{alreadyAdded ? " · Already in your apps" : ""}</small></span>
                    </label>
                  {:else}{#if !loading}<p class="pl-help">No playlists found.</p>{/if}{/each}
                </div>
                {#if publicListTruncated}<p class="pl-help">Only the first 1,000 public playlists can be listed. You can paste another playlist link above.</p>{/if}
                {#if sourceCursor}<Button class="pl-more" variant="secondary" disabled={loading} onclick={() => void browseSources(sourceCursor)}>Load more</Button>{/if}
              </section>
            {/if}
            </details>
          {/if}
          {#if !hasLastfmAccount}
            <section class="pl-section"><p class="pl-help">Import your loved, top, or similar tracks from Last.fm.</p><Button variant="secondary" href="#/sources/accounts?source=lastfm&connect=1">Connect Last.fm</Button></section>
          {/if}
        {:else if step === 2}
          <section class="pl-section">
            <div><h3 id="playlist-add-import">Import behavior</h3><p class="pl-help">Allstarr keeps the original playlist unchanged and can show every song through its resolved view.</p></div>
            {#if initialSource}<p class="pl-help">Importing {sourcePlaylistName} from {sourceName}.</p>{/if}
            <div class="pl-options" role="radiogroup" aria-labelledby="playlist-add-import">
              <label class="pl-option" class:active={importMode === "oneTime"}>
                <input bind:group={importMode} type="radio" name="playlist-import-mode" value="oneTime" disabled={followNewest} />
                <span class="pl-option-text"><strong>Import once</strong><small>Take a snapshot now and ignore later changes from {sourceName}.</small></span>
              </label>
              <label class="pl-option" class:active={importMode === "linked"}>
                <input bind:group={importMode} type="radio" name="playlist-import-mode" value="linked" />
                <span class="pl-option-text"><strong>Keep in sync</strong><small>Check {sourceName} for changes on the schedule you choose.</small></span>
              </label>
            </div>
            {#if followNewestKind}
              <label class="pl-retention-toggle">
                <Checkbox checked={followNewest} onCheckedChange={(checked) => { followNewest = checked; if (checked) importMode = "linked"; }} />
                <span><strong>Follow newest edition</strong><small>Keep this linked playlist up to date with new editions from {sourceName}.</small></span>
              </label>
            {/if}
          </section>
        {/if}
        {#if step === 2}
          <section class="pl-section">
            <div><h3 id="playlist-add-destination">Destination</h3><p class="pl-help">Find this playlist and its playable songs in your apps through Allstarr.</p></div>
            <div class="pl-options pl-options-two" role="radiogroup" aria-labelledby="playlist-add-destination">
              <label class="pl-option" class:active={targetMode === "new"}>
                <input bind:group={targetMode} type="radio" name="playlist-target-mode" value="new" onchange={() => void chooseTargetMode("new")} />
                <span class="pl-option-text"><strong>New playlist</strong><small>Shows in your apps using the source name.</small></span>
              </label>
              <label class="pl-option" class:active={targetMode === "existing"}>
                <input bind:group={targetMode} type="radio" name="playlist-target-mode" value="existing" disabled={chosenPlaylists.length > 1} onchange={() => void chooseTargetMode("existing")} />
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
                    <span class="pl-option-text"><strong>{playlist.name}</strong><small>{playlist.trackCount == null ? "? songs" : countLabel(playlist.trackCount, "song")}{playlist.description ? ` · ${playlist.description}` : ""}</small></span>
                  </label>
                {:else}{#if !loading}<p class="pl-help">No playlists you can change were found.</p>{/if}{/each}
              </div>
            </section>
          {/if}
        {/if}
        {#if step === 2}
          <section class="pl-section import-preview"><h3>What comes over</h3><strong>{sourceCount == null ? "Song count is checked during import" : `${countLabel(sourceCount, "song")} from ${sourceName}`}</strong><p class="pl-help">Allstarr checks songs against your library and connected services after you start. Unsure songs go to Matches for you to review.</p></section>
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
        {#if step > 1}<Button variant="text" onclick={() => step = 1}>Back</Button>{/if}<Dialog.Close class={buttonVariants({ variant: "text" })}>Cancel</Dialog.Close>
        {#if step < 2}
          <Button disabled={!stepReady || loading} onclick={() => void next()}>Next</Button>
        {:else}
          <Button disabled={!sourcePlaylistId || !targetId || (targetMode === "existing" && !targetPlaylistId) || saving} onclick={() => void save()}>{saving ? "Starting import…" : sourceCount == null ? "Import playlist" : `Import ${countLabel(sourceCount, "song")}`}</Button>
        {/if}
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  .account-playlists { display: grid; gap: var(--space-3); }
  .account-playlist-group { border: 1px solid var(--color-edge); border-radius: var(--radius-lg); overflow: hidden; }
  .account-playlist-group h3 { padding: var(--space-3); background: var(--color-panel-raised); font-size: var(--text-sm); font-weight: 600; }
  .account-playlist-choice { display: flex; align-items: center; gap: var(--space-3); min-height: 64px; padding: var(--space-3); border-top: 1px solid var(--color-edge); cursor: pointer; }
  .account-playlist-choice :global([data-slot="checkbox"]) { flex: none; min-height: 20px; width: 20px; height: 20px; }
  .account-playlist-choice > span { min-width: 0; flex: 1; }
  .account-playlist-choice strong, .account-playlist-choice small { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--text-sm); }
  .account-playlist-choice small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .account-playlist-choice :global(.media-art) { flex: none; width: 36px; height: 36px; }
  .other-sources > summary { color: var(--color-signal-text); cursor: pointer; min-height: 44px; display: flex; align-items: center; font-size: var(--text-sm); }
  .import-preview > strong { font-size: var(--text-sm); }

  .url-import-form, .url-import-field { width: 100%; }
  .url-import-field { display: grid; gap: var(--space-2); }
  .url-import-input { position: relative; display: block; }
  .url-import-input :global(svg) {
    position: absolute;
    left: 0.875rem;
    top: 50%;
    transform: translateY(-50%);
    color: var(--color-ink-muted);
    pointer-events: none;
  }
  .url-import-input input {
    width: 100%;
    height: var(--control-md);
    border: 1px solid var(--color-edge);
    border-radius: var(--radius-md);
    background: var(--color-panel-raised);
    color: var(--color-ink);
    padding-inline: 2.5rem 0.875rem;
    font-size: 16px;
  }
  .url-import-input input:focus-visible {
    border-color: var(--color-signal);
    outline: 1px solid var(--color-signal);
  }
  .url-import-preview {
    display: flex;
    align-items: center;
    gap: var(--space-3);
    padding: var(--space-3);
    border: 1px solid var(--color-edge);
    border-radius: var(--radius-md);
    background: var(--color-panel-raised);
  }
  .url-import-preview :global(.url-import-art) {
    width: 56px;
    height: 56px;
    flex: 0 0 56px;
  }
  .url-import-details { display: grid; gap: var(--space-1); min-width: 0; flex: 1; overflow-wrap: anywhere; }
  .url-import-details span, .url-import-details small { color: var(--color-ink-muted); font-size: var(--text-sm); }
</style>
