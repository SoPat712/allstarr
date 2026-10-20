<script lang="ts">
  import { tick } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Download, Music2, Play, ScrollText, X } from "@lucide/svelte";
  import { adminUrl } from "$lib/admin-url";
  import {
    ApiError,
    sources,
    type ProviderAccount,
    type ProviderDefinition,
    type ProviderTryCapability,
    type ProviderTryCatalog,
    type ProviderTryRequest,
    type ProviderTryResult,
    type ProviderTrySearchKind,
    type ProviderTryTrack,
  } from "$lib/api";
  import { diagnosticFailureMessage } from "$lib/diagnostics";
  import { humanize } from "$lib/activity";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SegmentedNav from "$lib/components/SegmentedNav.svelte";

  let {
    open = $bindable(false),
    provider,
    account = null,
    capability = "metadata",
  }: {
    open?: boolean;
    provider: ProviderDefinition;
    account?: ProviderAccount | null;
    capability?: string;
  } = $props();

  type Tab = ProviderTryCapability;
  const order: Tab[] = ["metadata", "lyrics", "playlist", "streaming", "download", "scrobbling"];
  const tabLabels: Record<Tab, string> = {
    metadata: "Search",
    lyrics: "Lyrics",
    playlist: "Playlists",
    streaming: "Playback",
    download: "Download",
    scrobbling: "Scrobbling",
  };
  const kindLabels: Record<ProviderTrySearchKind, string> = { tracks: "Songs", albums: "Albums", artists: "Artists" };

  let catalog = $state<ProviderTryCatalog | null>(null);
  let catalogError = $state("");
  let tab = $state<Tab>("metadata");
  let running = $state<Tab | "">("");
  let results = $state<Partial<Record<Tab, ProviderTryResult>>>({});
  let errors = $state<Partial<Record<Tab, string>>>({});
  let request: AbortController | undefined;

  // Form state, kept per tab so switching tabs never loses what was typed.
  let lookup = $state<"search" | "track" | "isrc">("search");
  let query = $state("");
  let kind = $state<ProviderTrySearchKind>("tracks");
  let lyricTitle = $state("");
  let lyricArtist = $state("");
  let lyricAlbum = $state("");
  let lyricDuration = $state("");
  let lyricIsrc = $state("");
  let lyricTrackId = $state("");
  let playlistUrl = $state("");
  let pickQuery = $state<Partial<Record<Tab, string>>>({});
  let picks = $state<Partial<Record<Tab, ProviderTryTrack[]>>>({});
  let picking = $state<Tab | "">("");
  let quality = $state<NonNullable<ProviderTryRequest["quality"]>>("lossless");

  const capabilities = $derived(catalog?.capabilities.toSorted((a, b) => order.indexOf(a.capability) - order.indexOf(b.capability)) ?? []);
  const tabs = $derived(capabilities.map((item) => ({ id: item.capability, label: tabLabels[item.capability] })));
  const current = $derived(capabilities.find((item) => item.capability === tab));
  const operation = (capability: Tab, id: string) => capabilities.find((item) => item.capability === capability)?.operations.find((item) => item.id === id);
  const search = $derived(operation("metadata", "search"));
  const canSearchSongs = $derived(Boolean(search?.kinds?.includes("tracks") ?? search));
  const name = $derived(provider.name ?? provider.id);
  const result = $derived(results[tab]);
  const accountLine = $derived(account
    ? `Uses ${account.displayName}. Runs on ${name} only and saves nothing to your library.`
    : `Runs on ${name} only and saves nothing to your library.`);

  $effect(() => {
    if (!open) return;
    const providerId = provider.id;
    const accountId = account?.id ?? null;
    const requested = capability.toLowerCase() as Tab;
    const cancellation = new AbortController();
    catalog = null;
    catalogError = "";
    results = {};
    errors = {};
    picks = {};
    sources.tryOperations(providerId, accountId, cancellation.signal)
      .then((value) => {
        if (cancellation.signal.aborted) return;
        catalog = value;
        const available = value.capabilities.map((item) => item.capability);
        tab = available.includes(requested) ? requested : available.toSorted((a, b) => order.indexOf(a) - order.indexOf(b))[0] ?? "metadata";
      })
      .catch((cause) => {
        if (!cancellation.signal.aborted) catalogError = message(cause, "This source's features could not be loaded. Close and try again.");
      });
    return () => {
      cancellation.abort();
      request?.abort();
    };
  });

  function message(cause: unknown, fallback: string) {
    if (cause instanceof ApiError) {
      const details = cause.details as { reasonCode?: string; retryAfterSeconds?: number } | null;
      if (cause.status === 409 && details?.reasonCode === "sign_in_needed") return `${name} needs a sign-in first. Sign in on this page, then try again.`;
      if (cause.status === 429) return `Another test on ${name} is still running. Wait ${details?.retryAfterSeconds ?? 5} seconds, then try again.`;
      return cause.message;
    }
    return cause instanceof Error && cause.name !== "AbortError" ? cause.message : fallback;
  }

  async function run(target: Tab, body: Omit<ProviderTryRequest, "capability" | "accountId">, into: "result" | "pick" = "result") {
    if (running) return;
    request?.abort();
    request = new AbortController();
    running = target;
    if (into === "pick") picking = target;
    errors = { ...errors, [target]: "" };
    try {
      const capability = into === "pick" ? "metadata" : target;
      // Features that never use an account (Deezer search, public lyrics) refuse one.
      const usesAccount = capabilities.find((item) => item.capability === capability)?.accountRequirement !== "none";
      const value = await sources.try(provider.id, { capability, accountId: usesAccount ? account?.id ?? null : null, ...body }, request.signal);
      if (into === "pick") picks = { ...picks, [target]: value.tracks ?? [] };
      if (into === "pick" && !value.succeeded) errors = { ...errors, [target]: failure(value) };
      if (into === "result") results = { ...results, [target]: value };
      void reveal();
    } catch (cause) {
      if (!request.signal.aborted) errors = { ...errors, [target]: message(cause, "The request did not finish. Try again.") };
    } finally {
      running = "";
      picking = "";
    }
  }

  // On small screens the answer lands below the form; bring it into view.
  async function reveal() {
    await tick();
    const answer = document.querySelector<HTMLElement>(".try-body > [role=status], .try-body > [role=alert]");
    answer?.scrollIntoView({ block: window.matchMedia("(max-width: 760px)").matches ? "start" : "nearest", behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
  }

  function failure(value: ProviderTryResult) {
    if (value.reasonCode === "not_found") return `${name} found nothing for this.`;
    return value.message || diagnosticFailureMessage({ reasonCode: value.reasonCode });
  }

  function submitMetadata(event: SubmitEvent) {
    event.preventDefault();
    const text = query.trim();
    if (!text) return;
    if (lookup === "track") void run("metadata", { operation: "track", id: text });
    else if (lookup === "isrc") void run("metadata", { operation: "isrc", isrc: text.toUpperCase() });
    else void run("metadata", { operation: "search", query: text, kind });
  }

  function seconds(value: string) {
    const parts = value.trim().split(":").map(Number);
    if (!value.trim() || parts.some((part) => !Number.isFinite(part) || part < 0)) return undefined;
    return parts.reduce((total, part) => total * 60 + part, 0) || undefined;
  }

  function submitLyrics(event: SubmitEvent) {
    event.preventDefault();
    if (!lyricTitle.trim() || !lyricArtist.trim()) return;
    void run("lyrics", {
      operation: "lyrics",
      title: lyricTitle.trim(),
      artist: lyricArtist.trim(),
      album: lyricAlbum.trim() || undefined,
      durationSeconds: seconds(lyricDuration),
      isrc: lyricIsrc.trim() || undefined,
      trackId: lyricTrackId || undefined,
    });
  }

  function submitPick(event: SubmitEvent, target: Tab) {
    event.preventDefault();
    const text = pickQuery[target]?.trim();
    if (text) void run(target, { operation: "search", query: text, kind: "tracks" }, "pick");
  }

  function use(track: ProviderTryTrack, target: Tab) {
    tab = target;
    if (target === "lyrics") {
      lyricTitle = track.title;
      lyricArtist = track.artists.join(", ");
      lyricAlbum = track.album ?? "";
      lyricDuration = track.durationSeconds ? clock(track.durationSeconds) : "";
      lyricIsrc = track.isrc ?? "";
      lyricTrackId = track.id;
      void run("lyrics", { operation: "lyrics", title: track.title, artist: lyricArtist, album: track.album ?? undefined,
        durationSeconds: track.durationSeconds ?? undefined, isrc: track.isrc ?? undefined, trackId: track.id });
    } else {
      picks = { ...picks, [target]: [track] };
      void run(target, { operation: target === "streaming" ? "stream" : "download", trackId: track.id, quality });
    }
  }

  function clock(total: number) {
    const value = Math.max(0, Math.round(total));
    const hours = Math.floor(value / 3600);
    const rest = `${Math.floor(value % 3600 / 60)}`.padStart(hours ? 2 : 1, "0") + ":" + `${value % 60}`.padStart(2, "0");
    return hours ? `${hours}:${rest}` : rest;
  }

  function stamp(ms: number | null) {
    if (ms == null) return "";
    return `${Math.floor(ms / 60000)}:${`${Math.floor(ms / 1000) % 60}`.padStart(2, "0")}.${`${Math.floor(ms % 1000 / 10)}`.padStart(2, "0")}`;
  }

  function size(bytes: number | null) {
    if (bytes == null) return null;
    return bytes >= 1_048_576 ? `${(bytes / 1_048_576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

  function elapsed(ms: number) {
    return ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`;
  }

  function audioFacts(media: NonNullable<ProviderTryResult["media"]>) {
    const format = [
      media.codec?.toUpperCase(),
      media.bitDepth && media.sampleRateHz ? `${media.bitDepth}-bit / ${(media.sampleRateHz / 1000).toLocaleString(undefined, { maximumFractionDigits: 1 })} kHz` : null,
      !media.bitDepth && media.bitrateKbps ? `${media.bitrateKbps} kbps` : null,
    ].filter(Boolean).join(" ");
    return [
      media.startMs != null ? ["Started in", elapsed(media.startMs)] : null,
      media.seekMode ? ["Seeking", humanize(media.seekMode)] : null,
      format ? ["Format", format] : null,
      media.sizeBytes != null ? ["Size", size(media.sizeBytes)] : null,
      media.durationSeconds ? ["Length", clock(media.durationSeconds)] : null,
    ].filter((item): item is [string, string] => Boolean(item?.[1]));
  }

  const countLabel = (value: ProviderTryResult) => {
    const count = value.tracks?.length ?? value.albums?.length ?? value.artists?.length ?? value.playlists?.length ?? 0;
    const noun = value.albums ? "album" : value.artists ? "artist" : value.playlists ? "playlist" : "song";
    return `${count} ${noun}${count === 1 ? "" : "s"}`;
  };
</script>

{#snippet trackRow(track: ProviderTryTrack, actions: Tab[])}
  <li class="try-row">
    <MediaArtwork class="try-art" url={track.artworkUrl} fallback="♪" />
    <span class="try-row-copy">
      <strong><span>{track.title}</span>{#if track.explicit}<abbr class="try-explicit" title="Explicit">E</abbr>{/if}</strong>
      <small>{track.artists.join(", ") || "Unknown artist"}{track.album ? ` · ${track.album}` : ""}{track.year ? ` · ${track.year}` : ""}</small>
      <small class="try-ids">{#if track.isrc}<span>ISRC {track.isrc}</span>{/if}<span>ID {track.id}</span></small>
    </span>
    <span class="try-row-end">
      {#if track.durationSeconds}<span class="try-duration">{clock(track.durationSeconds)}</span>{/if}
      {#if actions.length}
        <span class="try-row-actions">
          {#each actions as action}
            {#if action === "lyrics"}<Button variant="ghost" size="icon-sm" aria-label={`Get lyrics for ${track.title}`} title="Get lyrics" disabled={Boolean(running)} onclick={() => use(track, "lyrics")}><ScrollText /></Button>{/if}
            {#if action === "streaming"}<Button variant="ghost" size="icon-sm" aria-label={`Play ${track.title}`} title="Play" disabled={Boolean(running)} onclick={() => use(track, "streaming")}><Play /></Button>{/if}
            {#if action === "download"}<Button variant="ghost" size="icon-sm" aria-label={`Download ${track.title}`} title="Download" disabled={Boolean(running)} onclick={() => use(track, "download")}><Download /></Button>{/if}
          {/each}
        </span>
      {/if}
    </span>
  </li>
{/snippet}

{#snippet status(target: Tab, value: ProviderTryResult | undefined, success: string)}
  {#if running === target && picking !== target}
    <p class="try-status" role="status"><span class="try-spinner" aria-hidden="true"></span>{target === "download" ? `Downloading from ${name}… This can take a minute.` : target === "streaming" ? `Starting playback on ${name}…` : `Asking ${name}…`}</p>
  {:else if errors[target]}
    <p class="notice-error try-notice" role="alert">{errors[target]}</p>
  {:else if value && !value.succeeded}
    <p class="notice-error try-notice" role="alert">{failure(value)}<small>{elapsed(value.elapsedMs)}{value.reasonCode ? ` · ${value.reasonCode}` : ""}</small></p>
  {:else if value}
    <p class="try-status try-status--ok" role="status">{success} in {elapsed(value.elapsedMs)}</p>
  {/if}
{/snippet}

{#snippet picker(target: Tab)}
  {#if canSearchSongs}
    <form class="try-form try-form--inline" onsubmit={(event) => submitPick(event, target)}>
      <label class="field try-grow"><span>Song</span><input type="search" placeholder={`Search ${name} for a song`} value={pickQuery[target] ?? ""} oninput={(event) => pickQuery = { ...pickQuery, [target]: event.currentTarget.value }} disabled={Boolean(running)} /></label>
      <Button type="submit" variant="secondary" disabled={Boolean(running) || !pickQuery[target]?.trim()}>{picking === target ? "Searching…" : "Find"}</Button>
    </form>
    {#if picking === target}<p class="try-status" role="status"><span class="try-spinner" aria-hidden="true"></span>Searching {name}…</p>{/if}
    {#if picks[target]?.length}
      <ul class="try-list" aria-label="Songs to test">{#each picks[target]!.slice(0, 5) as track}{@render trackRow(track, [target])}{/each}</ul>
    {:else if picks[target] && !errors[target]}
      <p class="try-empty">{name} found no songs for that search.</p>
    {/if}
  {/if}
{/snippet}

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog try-dialog" preventScroll={false}>
      <header>
        <div class="try-heading">
          <span class="try-mark"><ProviderMark id={provider.id} definition={provider} /></span>
          <div>
            <Dialog.Title>Test {name}</Dialog.Title>
            <Dialog.Description>{accountLine}</Dialog.Description>
          </div>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>

      {#if catalogError}
        <p class="notice-error try-notice try-notice--page" role="alert">{catalogError}</p>
      {:else if !catalog}
        <p class="try-status try-status--page" role="status"><span class="try-spinner" aria-hidden="true"></span>Loading what {name} can do…</p>
      {:else if !tabs.length}
        <p class="try-empty try-empty--page">{name} has nothing to try yet. Turn on one of its features first.</p>
      {:else}
        {#if tabs.length > 1}<SegmentedNav items={tabs} active={tab} label={`${name} features`} class="route-tabs try-tabs" onchange={(id) => tab = id as Tab} />{/if}

        <div class="try-body" role="tabpanel" aria-label={tabLabels[tab]}>
          {#if tab === "metadata" && current}
            <form class="try-form" onsubmit={submitMetadata}>
              <div class="try-form--inline">
                <label class="field try-grow">
                  <span>{lookup === "track" ? `${name} song ID` : lookup === "isrc" ? "ISRC" : "Search"}</span>
                  <input type="search" bind:value={query} disabled={Boolean(running)}
                    placeholder={lookup === "track" ? "Paste a song ID" : lookup === "isrc" ? "For example USQX91300108" : "Song, album or artist"} />
                </label>
                <Button type="submit" disabled={Boolean(running) || !query.trim()}>{running === "metadata" ? "Searching…" : lookup === "search" ? "Search" : "Look up"}</Button>
              </div>
              <div class="try-options">
                {#if operation("metadata", "track") || operation("metadata", "isrc")}
                  <span class="try-chips" role="radiogroup" aria-label="Look up by">
                    {#if search}<label class="try-chip"><input type="radio" bind:group={lookup} value="search" />Text</label>{/if}
                    {#if operation("metadata", "track")}<label class="try-chip"><input type="radio" bind:group={lookup} value="track" />Song ID</label>{/if}
                    {#if operation("metadata", "isrc")}<label class="try-chip"><input type="radio" bind:group={lookup} value="isrc" />ISRC</label>{/if}
                  </span>
                {/if}
                {#if lookup === "search" && (search?.kinds?.length ?? 0) > 1}
                  <span class="try-chips" role="radiogroup" aria-label="Show">
                    {#each search!.kinds! as item}<label class="try-chip"><input type="radio" bind:group={kind} value={item} />{kindLabels[item]}</label>{/each}
                  </span>
                {/if}
              </div>
            </form>
            {@render status("metadata", result, result?.succeeded ? `${countLabel(result)} found` : "")}
            {#if result?.succeeded}
              {#if result.tracks?.length}
                <ul class="try-list" aria-label="Results">
                  {#each result.tracks as track}{@render trackRow(track, (["lyrics", "streaming", "download"] as Tab[]).filter((item) => capabilities.some((entry) => entry.capability === item)))}{/each}
                </ul>
              {:else if result.albums?.length}
                <ul class="try-list" aria-label="Results">
                  {#each result.albums as album}
                    <li class="try-row"><MediaArtwork class="try-art" url={album.artworkUrl} fallback="◎" /><span class="try-row-copy"><strong>{album.title}</strong><small>{album.artists.join(", ")}{album.year ? ` · ${album.year}` : ""}{album.trackCount ? ` · ${album.trackCount} songs` : ""}</small><small class="try-ids"><span>ID {album.id}</span></small></span></li>
                  {/each}
                </ul>
              {:else if result.artists?.length}
                <ul class="try-list" aria-label="Results">
                  {#each result.artists as artist}
                    <li class="try-row"><MediaArtwork class="try-art try-art--round" url={artist.artworkUrl} fallback="♫" /><span class="try-row-copy"><strong>{artist.name}</strong><small class="try-ids"><span>ID {artist.id}</span></small></span></li>
                  {/each}
                </ul>
              {:else}
                <p class="try-empty">{name} returned no results. Try other words.</p>
              {/if}
            {/if}

          {:else if tab === "lyrics"}
            <form class="try-form" onsubmit={submitLyrics}>
              <div class="try-grid">
                <label class="field"><span>Title</span><input bind:value={lyricTitle} oninput={() => lyricTrackId = ""} disabled={Boolean(running)} placeholder="Get Lucky" required /></label>
                <label class="field"><span>Artist</span><input bind:value={lyricArtist} oninput={() => lyricTrackId = ""} disabled={Boolean(running)} placeholder="Daft Punk" required /></label>
                <label class="field"><span>Album <em>optional</em></span><input bind:value={lyricAlbum} disabled={Boolean(running)} /></label>
                <div class="try-grid try-grid--pair">
                  <label class="field"><span>Length <em>optional</em></span><input bind:value={lyricDuration} inputmode="numeric" disabled={Boolean(running)} placeholder="6:09" /></label>
                  <label class="field"><span>ISRC <em>optional</em></span><input bind:value={lyricIsrc} disabled={Boolean(running)} /></label>
                </div>
              </div>
              <div class="try-form-end">
                {#if canSearchSongs}<small>Or pick a song on the Search tab to fill these in.</small>{/if}
                <Button type="submit" disabled={Boolean(running) || !lyricTitle.trim() || !lyricArtist.trim()}>{running === "lyrics" ? "Getting lyrics…" : "Get lyrics"}</Button>
              </div>
            </form>
            {@render status("lyrics", result, result?.lyrics ? `${result.lyrics.lineCount} lines found` : "")}
            {#if result?.succeeded && result.lyrics}
              <div class="try-lyrics">
                <p class="try-facts">
                  <span class="try-pill">{result.lyrics.format === "word" ? "Word-timed" : result.lyrics.format === "line" ? "Line-timed" : "Plain text"}</span>
                  {#if result.lyrics.source}<span>From {result.lyrics.source}</span>{/if}
                  {#if result.lyrics.language}<span>Language: {result.lyrics.language}</span>{/if}
                </p>
                <ol class="try-lyric-lines" class:untimed={result.lyrics.format === "plain"}>
                  {#each result.lyrics.lines as line}
                    <li>{#if result.lyrics.format !== "plain"}<time>{stamp(line.startMs)}</time>{/if}<span>{line.text || " "}</span></li>
                  {/each}
                </ol>
              </div>
            {/if}

          {:else if tab === "playlist"}
            <form class="try-form try-form--inline" onsubmit={(event) => { event.preventDefault(); if (playlistUrl.trim()) void run("playlist", { operation: "playlist", url: playlistUrl.trim() }); }}>
              <label class="field try-grow"><span>Playlist link or ID</span><input inputmode="url" autocomplete="off" bind:value={playlistUrl} disabled={Boolean(running)} placeholder={`Paste a ${name} playlist link`} /></label>
              <Button type="submit" disabled={Boolean(running) || !playlistUrl.trim()}>{running === "playlist" ? "Opening…" : "Open"}</Button>
              {#if operation("playlist", "mine")}<Button variant="secondary" disabled={Boolean(running)} onclick={() => void run("playlist", { operation: "mine" })}>My playlists</Button>{/if}
            </form>
            {@render status("playlist", result, result?.playlist ? `${result.tracks?.length ?? 0} songs loaded` : result ? `${countLabel(result)} found` : "")}
            {#if result?.succeeded && result.playlist}
              <div class="try-playlist-head">
                <MediaArtwork class="try-art try-art--large" url={result.playlist.artworkUrl} fallback="♫" />
                <span><strong>{result.playlist.title}</strong><small>{result.playlist.owner ? `By ${result.playlist.owner} · ` : ""}{result.playlist.trackCount ?? result.tracks?.length ?? 0} songs{(result.playlist.trackCount ?? 0) > (result.tracks?.length ?? 0) ? `, first ${result.tracks?.length ?? 0} shown` : ""}</small></span>
              </div>
              {#if result.tracks?.length}<ul class="try-list" aria-label="Playlist songs">{#each result.tracks as track}{@render trackRow(track, [])}{/each}</ul>{/if}
            {:else if result?.succeeded && result.playlists}
              {#if result.playlists.length}
                <ul class="try-list" aria-label="Your playlists">
                  {#each result.playlists as playlist}
                    <li class="try-row"><MediaArtwork class="try-art" url={playlist.artworkUrl} fallback="♫" /><span class="try-row-copy"><strong>{playlist.title}</strong><small>{playlist.owner ? `${playlist.owner} · ` : ""}{playlist.trackCount ?? "?"} songs</small><small class="try-ids"><span>ID {playlist.id}</span></small></span>
                      <span class="try-row-end"><Button variant="ghost" size="sm" disabled={Boolean(running)} onclick={() => { playlistUrl = playlist.id; void run("playlist", { operation: "playlist", url: playlist.id }); }}>Open</Button></span></li>
                  {/each}
                </ul>
              {:else}<p class="try-empty">This account has no playlists on {name}.</p>{/if}
            {/if}

          {:else if tab === "streaming" || tab === "download"}
            <div class="try-quality">
              <label class="field"><span>Quality</span>
                <select bind:value={quality} disabled={Boolean(running)}>
                  <option value="high-resolution">Hi-res</option>
                  <option value="lossless">Lossless</option>
                  <option value="lossy">Lossy</option>
                  <option value="any">Whatever is available</option>
                </select>
              </label>
              {#if tab === "streaming"}<Button variant="secondary" disabled={Boolean(running)} onclick={() => void run("streaming", { operation: "stream", quality })}>Play a sample song</Button>{/if}
            </div>
            {@render picker(tab)}
            {#if !canSearchSongs && tab === "download"}
              <form class="try-form try-form--inline" onsubmit={(event) => { event.preventDefault(); const id = pickQuery.download?.trim(); if (id) void run("download", { operation: "download", trackId: id, quality }); }}>
                <label class="field try-grow"><span>{name} song ID</span><input value={pickQuery.download ?? ""} oninput={(event) => pickQuery = { ...pickQuery, download: event.currentTarget.value }} disabled={Boolean(running)} /></label>
                <Button type="submit" disabled={Boolean(running) || !pickQuery.download?.trim()}>Download</Button>
              </form>
            {/if}
            {@render status(tab, result, tab === "streaming" ? "Playback started" : "Downloaded")}
            {#if result?.succeeded && result.media}
              {@const media = result.media}
              <div class="try-media">
                <div class="try-media-title">
                  <span class="try-media-icon" aria-hidden="true">{#if media.kind === "download"}<Download size={18} />{:else}<Music2 size={18} />{/if}</span>
                  <strong>{media.fileName ?? picks[tab]?.[0]?.title ?? "Sample song"}</strong>
                </div>
                <!-- svelte-ignore a11y_media_has_caption -->
                <audio controls preload="metadata" src={adminUrl(media.url)}></audio>
                <dl class="try-media-facts">
                  {#each audioFacts(media) as [label, value]}<div><dt>{label}</dt><dd>{value}</dd></div>{/each}
                </dl>
                {#if media.kind === "download"}
                  <div class="try-media-actions">
                    <small>The file is kept for 15 minutes, then deleted.</small>
                    <a class={buttonVariants({ variant: "default" })} href={adminUrl(`${media.url}?download=1`)} download={media.fileName ?? ""}><Download aria-hidden="true" />Save file</a>
                  </div>
                {/if}
              </div>
            {/if}

          {:else if tab === "scrobbling"}
            <div class="try-form try-form--inline">
              <p class="try-explain">Checks that {account ? account.displayName : "this account"} is accepted. No listen is sent.</p>
              <Button disabled={Boolean(running)} onclick={() => void run("scrobbling", { operation: "check" })}>{running === "scrobbling" ? "Checking…" : "Check account"}</Button>
            </div>
            {@render status("scrobbling", result, "Account accepted")}
          {/if}
        </div>
      {/if}

      <footer class="dialog-actions">
        <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Done</Dialog.Close>
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  :global {
    @layer components {
      .try-dialog {
        --dialog-width: 46rem;
        --dialog-max-height: 52rem;

        display: grid;
        grid-template-rows: auto auto minmax(0, 1fr) auto;
        overflow: hidden;
      }

      .try-heading {
        display: flex;
        align-items: center;
        gap: var(--space-3);
      }

      .try-mark {
        display: grid;
        flex-shrink: 0;
        width: 2.75rem;
        height: 2.75rem;
        place-items: center;
        overflow: hidden;
        border-radius: var(--radius-md);
        background: var(--color-panel-raised);
      }

      .try-mark > :is(img, svg, span) {
        width: 1.75rem;
        height: 1.75rem;
      }

      .try-dialog > .segmented-nav {
        padding: var(--space-3) var(--space-4) 0;
      }

      .try-body {
        display: grid;
        align-content: start;
        gap: var(--space-4);
        min-height: 18rem;
        overflow: auto;
        padding: var(--space-4);
      }

      .try-form {
        display: grid;
        gap: var(--space-3);
      }

      .try-form--inline {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: var(--space-3);
      }

      .try-form--inline > :is(button, a),
      .try-quality > button,
      .try-form-end > button {
        min-height: 3rem;
      }

      .try-grow {
        flex: 1 1 16rem;
        min-width: 0;
      }

      .try-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: var(--space-3);
      }

      .try-grid--pair {
        grid-template-columns: minmax(0, 0.8fr) minmax(0, 1.2fr);
      }

      .try-dialog .field {
        min-width: 0;
      }

      .try-dialog .field :is(input, select) {
        width: 100%;
        min-width: 0;
      }

      .field > span > em {
        color: var(--color-ink-muted);
        font-style: normal;
        font-weight: 400;
      }

      .try-form-end {
        display: flex;
        align-items: center;
        justify-content: flex-end;
        gap: var(--space-3);
      }

      .try-form-end > small {
        margin-right: auto;
        color: var(--color-ink-muted);
        font-size: 0.8125rem;
      }

      .try-options {
        display: flex;
        flex-wrap: wrap;
        justify-content: space-between;
        gap: var(--space-2) var(--space-4);
      }

      .try-chips {
        display: inline-flex;
        flex-wrap: wrap;
        gap: var(--space-2);
      }

      .try-chip {
        display: inline-flex;
        align-items: center;
        min-height: 2rem;
        border: 1px solid var(--color-edge-strong);
        border-radius: var(--radius-sm);
        color: var(--color-ink);
        cursor: pointer;
        padding: 0 var(--space-3);
        font-size: var(--text-sm);
        font-weight: 600;
        transition: background-color 120ms ease, border-color 120ms ease;
      }

      .try-chip > input {
        position: absolute;
        opacity: 0;
        pointer-events: none;
      }

      .try-chip:has(input:checked) {
        border-color: transparent;
        background: var(--md-sys-color-secondary-container);
        color: var(--md-sys-color-on-secondary-container);
      }

      .try-chip:has(input:focus-visible) {
        outline: 2px solid var(--color-signal);
        outline-offset: 2px;
      }

      .try-quality {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        justify-content: space-between;
        gap: var(--space-3);
      }

      .try-quality > .field {
        flex: 0 1 14rem;
      }

      .try-status {
        display: flex;
        align-items: center;
        gap: var(--space-2);
        margin: 0;
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
      }

      .try-status--ok {
        color: var(--color-ink);
        font-weight: 600;
      }

      .try-status--page,
      .try-notice--page,
      .try-empty--page {
        margin: var(--space-6) var(--space-4);
      }

      .try-spinner {
        width: 1rem;
        height: 1rem;
        flex-shrink: 0;
        border: 2px solid var(--color-edge-strong);
        border-top-color: var(--color-signal);
        border-radius: 50%;
        animation: try-spin 800ms linear infinite;
      }

      @media (prefers-reduced-motion: reduce) {
        .try-spinner {
          animation-duration: 2400ms;
        }
      }

      @keyframes try-spin {
        to {
          transform: rotate(360deg);
        }
      }

      .try-notice {
        display: grid;
        gap: var(--space-1);
        margin: 0;
        font-size: var(--text-sm);
        line-height: 1.5;
      }

      .try-notice > small {
        opacity: 0.8;
        font-size: 0.8125rem;
      }

      .try-empty,
      .try-explain {
        margin: 0;
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
      }

      .try-explain {
        flex: 1 1 16rem;
        align-self: center;
      }

      .try-list {
        margin: 0;
        padding: 0;
        list-style: none;
        border: 1px solid var(--color-edge);
        border-radius: var(--radius-md);
      }

      .try-row {
        display: grid;
        grid-template-columns: 2.75rem minmax(0, 1fr) auto;
        align-items: center;
        gap: var(--space-3);
        padding: var(--space-2) var(--space-3);
      }

      .try-row + .try-row {
        border-top: 1px solid var(--color-edge);
      }

      .try-art {
        width: 2.75rem;
        height: 2.75rem;
        overflow: hidden;
        border-radius: var(--radius-sm);
        background: var(--color-panel-raised);
      }

      .try-art--round {
        border-radius: 50%;
      }

      .try-art--large {
        width: 4.5rem;
        height: 4.5rem;
        border-radius: var(--radius-md);
      }

      .try-row-copy {
        display: grid;
        min-width: 0;
        gap: 1px;
      }

      .try-row-copy > strong {
        display: flex;
        align-items: center;
        gap: var(--space-2);
        min-width: 0;
        color: var(--color-ink);
        font-size: var(--text-sm);
        font-weight: 650;
      }

      .try-row-copy > strong > span {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .try-row-copy > small {
        overflow: hidden;
        color: var(--color-ink-muted);
        font-size: 0.8125rem;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .try-ids {
        display: flex;
        gap: var(--space-3);
        font-variant-numeric: tabular-nums;
      }

      .try-explicit {
        display: inline-grid;
        width: 1.1rem;
        height: 1.1rem;
        flex-shrink: 0;
        place-items: center;
        border-radius: 3px;
        background: var(--color-ink-muted);
        color: var(--color-panel);
        font-size: 0.6875rem;
        font-weight: 800;
        text-decoration: none;
      }

      .try-row-end {
        display: flex;
        align-items: center;
        gap: var(--space-2);
      }

      .try-duration {
        color: var(--color-ink-muted);
        font-size: 0.8125rem;
        font-variant-numeric: tabular-nums;
      }

      .try-row-actions {
        display: flex;
        gap: 2px;
      }

      .try-facts {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--space-2) var(--space-4);
        margin: 0 0 var(--space-3);
        color: var(--color-ink-muted);
        font-size: 0.8125rem;
      }

      .try-pill {
        border-radius: 999px;
        background: var(--md-sys-color-secondary-container);
        color: var(--md-sys-color-on-secondary-container);
        padding: 0.2rem 0.65rem;
        font-weight: 650;
      }

      .try-lyric-lines {
        display: grid;
        max-height: 22rem;
        gap: 0.35rem;
        margin: 0;
        overflow: auto;
        border: 1px solid var(--color-edge);
        border-radius: var(--radius-md);
        padding: var(--space-3) var(--space-4);
        list-style: none;
      }

      .try-lyric-lines > li {
        display: grid;
        grid-template-columns: 4.5rem minmax(0, 1fr);
        gap: var(--space-3);
        color: var(--color-ink);
        font-size: var(--text-sm);
        line-height: 1.55;
      }

      .try-lyric-lines.untimed > li {
        grid-template-columns: minmax(0, 1fr);
      }

      .try-lyric-lines time {
        color: var(--color-ink-muted);
        font-size: 0.8125rem;
        font-variant-numeric: tabular-nums;
        line-height: 1.7;
      }

      .try-playlist-head {
        display: flex;
        align-items: center;
        gap: var(--space-4);
      }

      .try-playlist-head > span {
        display: grid;
        gap: var(--space-1);
        min-width: 0;
      }

      .try-playlist-head strong {
        font-family: var(--font-display);
        font-size: 1.125rem;
        overflow-wrap: anywhere;
      }

      .try-playlist-head small {
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
      }

      .try-media {
        display: grid;
        gap: var(--space-3);
        border: 1px solid var(--color-edge);
        border-radius: var(--radius-md);
        background: var(--color-panel-raised);
        padding: var(--space-4);
      }

      .try-media-title {
        display: flex;
        align-items: center;
        gap: var(--space-3);
        min-width: 0;
      }

      .try-media-title > strong {
        overflow: hidden;
        font-size: var(--text-sm);
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .try-media-icon {
        display: grid;
        width: 2.25rem;
        height: 2.25rem;
        flex-shrink: 0;
        place-items: center;
        border-radius: 50%;
        background: var(--md-sys-color-primary-container);
        color: var(--md-sys-color-on-primary-container);
      }

      .try-media audio {
        width: 100%;
      }

      .try-media-facts {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(8.5rem, 1fr));
        gap: var(--space-3);
        margin: 0;
      }

      .try-media-facts dt {
        color: var(--color-ink-muted);
        font-size: 0.8125rem;
      }

      .try-media-facts dd {
        margin: 0;
        color: var(--color-ink);
        font-size: var(--text-sm);
        font-variant-numeric: tabular-nums;
        font-weight: 650;
      }

      .try-media-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--space-3);
        border-top: 1px solid var(--color-edge);
        padding-top: var(--space-3);
      }

      .try-media-actions > small {
        color: var(--color-ink-muted);
        font-size: 0.8125rem;
      }

    }

    @media (max-width: 760px) {
      .try-dialog {
        grid-template-rows: auto auto minmax(0, 1fr) auto;
      }

      .try-body {
        min-height: 0;
      }

      .try-grid,
      .try-grid--pair {
        grid-template-columns: minmax(0, 1fr);
      }

      .try-form--inline > :is(button, a) {
        flex: 1 1 auto;
      }

      .try-row {
        grid-template-columns: 2.5rem minmax(0, 1fr);
        row-gap: var(--space-1);
      }

      .try-row-end {
        grid-column: 2;
        justify-content: space-between;
      }

      .try-art {
        width: 2.5rem;
        height: 2.5rem;
      }

      .try-lyric-lines > li {
        grid-template-columns: 3.75rem minmax(0, 1fr);
      }
    }
  }
</style>
