<script lang="ts">
  import { tick } from "svelte";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Check, CircleAlert, CircleCheck, Download, Music2, Play, ScrollText, Search, SearchX, X } from "@lucide/svelte";
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

  const hints: Partial<Record<Tab, string>> = {
    metadata: "Try fewer words, or search by artist and title.",
    lyrics: "Check the title and artist spelling, or add the album and length.",
    playlist: "Check that the link is public and belongs to this service.",
  };

  const finds = $derived([
    ...(search ? (search.kinds?.length ? search.kinds : ["tracks" as ProviderTrySearchKind]).map((item) => ({ id: item as string, label: kindLabels[item] })) : []),
    ...(operation("metadata", "track") ? [{ id: "track", label: "Song ID" }] : []),
    ...(operation("metadata", "isrc") ? [{ id: "isrc", label: "ISRC" }] : []),
  ]);
  const find = $derived(lookup === "search" ? kind : lookup);
  function choose(id: string) {
    if (id === "track" || id === "isrc") lookup = id;
    else { lookup = "search"; kind = id as ProviderTrySearchKind; }
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
            {#if action === "lyrics"}<Button variant="ghost" size="icon" aria-label={`Get lyrics for ${track.title}`} title="Get lyrics" disabled={Boolean(running)} onclick={() => use(track, "lyrics")}><ScrollText /></Button>{/if}
            {#if action === "streaming"}<Button variant="ghost" size="icon" aria-label={`Play ${track.title}`} title="Play" disabled={Boolean(running)} onclick={() => use(track, "streaming")}><Play /></Button>{/if}
            {#if action === "download"}<Button variant="ghost" size="icon" aria-label={`Download ${track.title}`} title="Download" disabled={Boolean(running)} onclick={() => use(track, "download")}><Download /></Button>{/if}
          {/each}
        </span>
      {/if}
    </span>
  </li>
{/snippet}

{#snippet status(target: Tab, value: ProviderTryResult | undefined, success: string)}
  {#if running === target && picking !== target}
    <div class="try-outcome" data-tone="busy" role="status">
      <span class="try-spinner" aria-hidden="true"></span>
      <div><strong>{target === "download" ? `Downloading from ${name}…` : target === "streaming" ? `Starting playback on ${name}…` : `Asking ${name}…`}</strong>{#if target === "download"}<small>This can take a minute.</small>{/if}</div>
    </div>
  {:else if errors[target]}
    <div class="try-outcome" data-tone="error" role="alert"><CircleAlert size={20} aria-hidden="true" /><div><strong>{errors[target]}</strong></div></div>
  {:else if value && !value.succeeded}
    {@const missing = value.reasonCode === "not_found"}
    <div class="try-outcome" data-tone={missing ? "neutral" : "error"} role="alert">
      {#if missing}<SearchX size={20} aria-hidden="true" />{:else}<CircleAlert size={20} aria-hidden="true" />{/if}
      <div>
        <strong>{failure(value)}</strong>
        {#if missing && hints[target]}<small>{hints[target]}</small>{/if}
        <small class="try-outcome-meta">{elapsed(value.elapsedMs)}{value.reasonCode ? ` · ${value.reasonCode}` : ""}</small>
      </div>
    </div>
  {:else if value}
    <p class="try-summary" role="status"><CircleCheck size={18} aria-hidden="true" /><strong>{success}</strong> <span>in {elapsed(value.elapsedMs)}</span></p>
  {/if}
{/snippet}

{#snippet picker(target: Tab)}
  {#if canSearchSongs}
    <form class="try-searchbar" onsubmit={(event) => submitPick(event, target)}>
      <Search size={20} aria-hidden="true" />
      <label class="sr-only" for={`try-pick-${target}`}>Song</label>
      <input id={`try-pick-${target}`} type="search" placeholder={`Search ${name} for a song`} value={pickQuery[target] ?? ""} oninput={(event) => pickQuery = { ...pickQuery, [target]: event.currentTarget.value }} disabled={Boolean(running)} />
      <Button type="submit" variant="secondary" disabled={Boolean(running) || !pickQuery[target]?.trim()}>{picking === target ? "Searching…" : "Find"}</Button>
    </form>
    {#if picking === target}<div class="try-outcome" data-tone="busy" role="status"><span class="try-spinner" aria-hidden="true"></span><div><strong>Searching {name}…</strong></div></div>{/if}
    {#if picks[target]?.length}
      <ul class="try-list" aria-label="Songs to test">{#each picks[target]!.slice(0, 5) as track}{@render trackRow(track, [target])}{/each}</ul>
    {:else if picks[target] && !errors[target]}
      <div class="try-outcome" data-tone="neutral" role="status"><SearchX size={20} aria-hidden="true" /><div><strong>{name} found no songs for that search.</strong></div></div>
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
        <Dialog.Close class="icon-button" aria-label="Close"><X size={20} aria-hidden="true" /></Dialog.Close>
      </header>

      {#if catalogError}
        <div class="try-body"><div class="try-outcome" data-tone="error" role="alert"><CircleAlert size={20} aria-hidden="true" /><div><strong>{catalogError}</strong></div></div></div>
      {:else if !catalog}
        <div class="try-body"><div class="try-outcome" data-tone="busy" role="status"><span class="try-spinner" aria-hidden="true"></span><div><strong>Loading what {name} can do…</strong></div></div></div>
      {:else if !tabs.length}
        <div class="try-body"><div class="try-outcome" data-tone="neutral" role="status"><SearchX size={20} aria-hidden="true" /><div><strong>{name} has nothing to try yet.</strong><small>Turn on one of its features first.</small></div></div></div>
      {:else}
        {#if tabs.length > 1}<SegmentedNav items={tabs} active={tab} label={`${name} features`} class="route-tabs try-tabs" onchange={(id) => tab = id as Tab} />{/if}

        <div class="try-body" role="tabpanel" aria-label={tabLabels[tab]}>
          {#if tab === "metadata" && current}
            <form class="try-search" onsubmit={submitMetadata}>
              <div class="try-searchbar">
                <Search size={20} aria-hidden="true" />
                <input type="search" bind:value={query} disabled={Boolean(running)} aria-label={lookup === "track" ? `${name} song ID` : lookup === "isrc" ? "ISRC" : "Search"}
                  placeholder={lookup === "track" ? `Paste a ${name} song ID` : lookup === "isrc" ? "ISRC, for example USQX91300108" : `Search ${name}`} />
                <Button type="submit" disabled={Boolean(running) || !query.trim()}>{running === "metadata" ? "Searching…" : lookup === "search" ? "Search" : "Look up"}</Button>
              </div>
              {#if finds.length > 1}
                <div class="try-chips" role="radiogroup" aria-label="Find">
                  {#each finds as item, index (item.id)}
                    {#if index > 0 && (item.id === "track" || item.id === "isrc") && !["track", "isrc"].includes(finds[index - 1].id)}<span class="try-chip-divider" aria-hidden="true"></span>{/if}
                    <button type="button" role="radio" class="try-chip" aria-checked={find === item.id} disabled={Boolean(running)} onclick={() => choose(item.id)}>
                      {#if find === item.id}<Check size={16} strokeWidth={2.5} aria-hidden="true" />{/if}{item.label}
                    </button>
                  {/each}
                </div>
              {/if}
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
                    <li class="try-row"><MediaArtwork class="try-art" url={album.artworkUrl} fallback="◎" /><span class="try-row-copy"><strong><span>{album.title}</span></strong><small>{album.artists.join(", ")}{album.year ? ` · ${album.year}` : ""}{album.trackCount ? ` · ${album.trackCount} songs` : ""}</small><small class="try-ids"><span>ID {album.id}</span></small></span></li>
                  {/each}
                </ul>
              {:else if result.artists?.length}
                <ul class="try-list" aria-label="Results">
                  {#each result.artists as artist}
                    <li class="try-row"><MediaArtwork class="try-art try-art--round" url={artist.artworkUrl} fallback="♫" /><span class="try-row-copy"><strong><span>{artist.name}</span></strong><small class="try-ids"><span>ID {artist.id}</span></small></span></li>
                  {/each}
                </ul>
              {:else}
                <div class="try-outcome" data-tone="neutral" role="status"><SearchX size={20} aria-hidden="true" /><div><strong>{name} returned no results.</strong><small>{hints.metadata}</small></div></div>
              {/if}
            {/if}

          {:else if tab === "lyrics"}
            <form class="try-form" onsubmit={submitLyrics}>
              <div class="try-fields try-fields--two">
                <label class="try-field"><span>Title</span><input bind:value={lyricTitle} oninput={() => lyricTrackId = ""} disabled={Boolean(running)} placeholder="Get Lucky" required /></label>
                <label class="try-field"><span>Artist</span><input bind:value={lyricArtist} oninput={() => lyricTrackId = ""} disabled={Boolean(running)} placeholder="Daft Punk" required /></label>
              </div>
              <div class="try-fields try-fields--details">
                <label class="try-field"><span>Album <em>optional</em></span><input bind:value={lyricAlbum} disabled={Boolean(running)} placeholder="Random Access Memories" /></label>
                <label class="try-field"><span>Length <em>optional</em></span><input bind:value={lyricDuration} inputmode="numeric" disabled={Boolean(running)} placeholder="6:09" /></label>
                <label class="try-field"><span>ISRC <em>optional</em></span><input bind:value={lyricIsrc} disabled={Boolean(running)} placeholder="USQX91300108" /></label>
              </div>
              <div class="try-form-end">
                {#if canSearchSongs}<small>Or pick a song on the Search tab to fill these in.</small>{/if}
                <Button type="submit" disabled={Boolean(running) || !lyricTitle.trim() || !lyricArtist.trim()}><ScrollText aria-hidden="true" />{running === "lyrics" ? "Getting lyrics…" : "Get lyrics"}</Button>
              </div>
            </form>
            {@render status("lyrics", result, result?.lyrics ? `${result.lyrics.lineCount} lines found` : "")}
            {#if result?.succeeded && result.lyrics}
              <section class="try-card try-lyrics" aria-label="Lyrics">
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
              </section>
            {/if}

          {:else if tab === "playlist"}
            <form class="try-searchbar" onsubmit={(event) => { event.preventDefault(); if (playlistUrl.trim()) void run("playlist", { operation: "playlist", url: playlistUrl.trim() }); }}>
              <Search size={20} aria-hidden="true" />
              <input inputmode="url" autocomplete="off" aria-label="Playlist link or ID" bind:value={playlistUrl} disabled={Boolean(running)} placeholder={`Paste a ${name} playlist link or ID`} />
              <Button type="submit" disabled={Boolean(running) || !playlistUrl.trim()}>{running === "playlist" ? "Opening…" : "Open"}</Button>
            </form>
            {#if operation("playlist", "mine")}<div class="try-actions"><Button variant="secondary" disabled={Boolean(running)} onclick={() => void run("playlist", { operation: "mine" })}>Show my playlists</Button></div>{/if}
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
                    <li class="try-row"><MediaArtwork class="try-art" url={playlist.artworkUrl} fallback="♫" /><span class="try-row-copy"><strong><span>{playlist.title}</span></strong><small>{playlist.owner ? `${playlist.owner} · ` : ""}{playlist.trackCount ?? "?"} songs</small><small class="try-ids"><span>ID {playlist.id}</span></small></span>
                      <span class="try-row-end"><Button variant="secondary" size="sm" disabled={Boolean(running)} onclick={() => { playlistUrl = playlist.id; void run("playlist", { operation: "playlist", url: playlist.id }); }}>Open</Button></span></li>
                  {/each}
                </ul>
              {:else}<div class="try-outcome" data-tone="neutral" role="status"><SearchX size={20} aria-hidden="true" /><div><strong>This account has no playlists on {name}.</strong></div></div>{/if}
            {/if}

          {:else if tab === "streaming" || tab === "download"}
            <div class="try-quality">
              <label class="try-field"><span>Quality</span>
                <select bind:value={quality} disabled={Boolean(running)}>
                  <option value="high-resolution">Hi-res</option>
                  <option value="lossless">Lossless</option>
                  <option value="lossy">Lossy</option>
                  <option value="any">Whatever is available</option>
                </select>
              </label>
              {#if tab === "streaming"}<Button variant="secondary" disabled={Boolean(running)} onclick={() => void run("streaming", { operation: "stream", quality })}><Play aria-hidden="true" />Play a sample song</Button>{/if}
            </div>
            {@render picker(tab)}
            {#if !canSearchSongs && tab === "download"}
              <form class="try-searchbar" onsubmit={(event) => { event.preventDefault(); const id = pickQuery.download?.trim(); if (id) void run("download", { operation: "download", trackId: id, quality }); }}>
                <Download size={20} aria-hidden="true" />
                <input aria-label={`${name} song ID`} placeholder={`Paste a ${name} song ID`} value={pickQuery.download ?? ""} oninput={(event) => pickQuery = { ...pickQuery, download: event.currentTarget.value }} disabled={Boolean(running)} />
                <Button type="submit" disabled={Boolean(running) || !pickQuery.download?.trim()}>Download</Button>
              </form>
            {/if}
            {@render status(tab, result, tab === "streaming" ? "Playback started" : "Downloaded")}
            {#if result?.succeeded && result.media}
              {@const media = result.media}
              <section class="try-card try-media" aria-label={media.kind === "download" ? "Downloaded file" : "Playback"}>
                <div class="try-media-title">
                  <span class="try-media-icon" aria-hidden="true">{#if media.kind === "download"}<Download size={20} />{:else}<Music2 size={20} />{/if}</span>
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
              </section>
            {/if}

          {:else if tab === "scrobbling"}
            <div class="try-card try-explain-card">
              <p>Checks that {account ? account.displayName : "this account"} is accepted. No listen is sent.</p>
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
        --dialog-width: 44rem;
        --dialog-max-height: 54rem;

        display: grid;
        grid-template-rows: auto auto minmax(0, 1fr) auto;
        overflow: hidden;
      }

      .try-heading {
        display: flex;
        min-width: 0;
        align-items: center;
        gap: var(--space-4);
      }

      .try-mark {
        display: grid;
        flex-shrink: 0;
        width: 3rem;
        height: 3rem;
        place-items: center;
        overflow: hidden;
        border-radius: var(--radius-md);
        background: var(--color-surface-raised);
      }

      .try-mark .provider-mark,
      .try-mark .provider-mark > img {
        width: 2.25rem;
        height: 2.25rem;
        border-radius: var(--radius-sm);
      }

      .try-dialog > .segmented-nav {
        padding: var(--space-2) var(--space-6) 0;
      }

      .try-body {
        display: grid;
        align-content: start;
        gap: var(--space-4);
        min-height: 20rem;
        overflow: auto;
        padding: var(--space-5) var(--space-6) var(--space-6);
      }

      /* Search bar: one pill holding the icon, the field and the action. */
      .try-search {
        display: grid;
        gap: var(--space-3);
      }

      .try-searchbar {
        display: flex;
        min-width: 0;
        align-items: center;
        gap: var(--space-3);
        min-height: 3.5rem;
        border-radius: 999px;
        background: var(--color-surface-raised);
        padding: 0.375rem 0.375rem 0.375rem var(--space-5);
        color: var(--color-ink-muted);
        transition: box-shadow 120ms ease;
      }

      .try-searchbar:focus-within {
        box-shadow: inset 0 0 0 2px var(--color-signal);
      }

      .try-searchbar > svg {
        flex-shrink: 0;
      }

      .try-searchbar > input {
        flex: 1 1 auto;
        min-width: 0;
        height: 2.75rem;
        border: 0;
        background: transparent;
        padding: 0;
        color: var(--color-ink);
        font: inherit;
        font-size: var(--text-md);
        outline: none;
        box-shadow: none;
      }

      .try-searchbar > input::placeholder {
        color: var(--color-ink-muted);
      }

      .try-searchbar > input::-webkit-search-cancel-button {
        display: none;
      }

      .try-searchbar > button {
        flex-shrink: 0;
        min-height: 2.75rem;
      }

      /* Filter chips (Material 3): outlined, filled with a check when selected. */
      .try-chips {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--space-2);
      }

      .try-chip {
        display: inline-flex;
        align-items: center;
        gap: var(--space-2);
        height: 2rem;
        border: 1px solid var(--color-edge-strong);
        border-radius: var(--radius-sm);
        background: transparent;
        padding: 0 var(--space-4);
        color: var(--color-ink-muted);
        font: inherit;
        font-size: var(--text-sm);
        font-weight: 500;
        cursor: pointer;
        transition: background-color 120ms ease, color 120ms ease;
      }

      .try-chip:hover:not(:disabled) {
        background: color-mix(in srgb, var(--color-ink) 8%, transparent);
        color: var(--color-ink);
      }

      .try-chip[aria-checked="true"] {
        border-color: transparent;
        background: var(--md-sys-color-secondary-container);
        padding-left: var(--space-2);
        color: var(--md-sys-color-on-secondary-container);
      }

      .try-chip:focus-visible {
        outline: 2px solid var(--color-ring);
        outline-offset: 2px;
      }

      .try-chip-divider {
        width: 1px;
        height: 1.25rem;
        margin: 0 var(--space-1);
        background: var(--color-edge);
      }

      /* Outlined text fields. */
      .try-form {
        display: grid;
        gap: var(--space-4);
      }

      .try-fields {
        display: grid;
        gap: var(--space-4);
      }

      .try-fields--two {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .try-fields--details {
        grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1.5fr);
      }

      .try-field {
        display: grid;
        min-width: 0;
        gap: var(--space-2);
      }

      .try-field > span {
        color: var(--color-ink);
        font-size: var(--text-sm);
        font-weight: 600;
        line-height: 1.25rem;
      }

      .try-field > span > em {
        color: var(--color-ink-muted);
        font-style: normal;
        font-weight: 400;
      }

      .try-field :is(input, select) {
        width: 100%;
        min-width: 0;
        height: 3rem;
        border: 1px solid var(--color-edge-strong);
        border-radius: var(--radius-sm);
        background: transparent;
        padding: 0 var(--space-4);
        color: var(--color-ink);
        font: inherit;
        font-size: var(--text-md);
      }

      .try-field :is(input, select):focus {
        border-color: var(--color-signal);
        outline: none;
        box-shadow: inset 0 0 0 1px var(--color-signal);
      }

      .try-field input::placeholder {
        color: var(--color-ink-muted);
        opacity: 0.7;
      }

      .try-form-end,
      .try-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: flex-end;
        gap: var(--space-3);
      }

      .try-form-end > small {
        margin-right: auto;
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
      }

      .try-quality {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        justify-content: space-between;
        gap: var(--space-3);
      }

      .try-quality > .try-field {
        flex: 0 1 15rem;
      }

      .try-quality > button {
        min-height: 3rem;
      }

      /* Outcomes: busy, nothing found and errors read as tonal cards; success is one line. */
      .try-outcome {
        display: flex;
        align-items: flex-start;
        gap: var(--space-3);
        margin: 0;
        border-radius: var(--radius-md);
        background: var(--color-surface-raised);
        padding: var(--space-4);
        color: var(--color-ink);
      }

      .try-outcome > svg,
      .try-outcome > .try-spinner {
        flex-shrink: 0;
        margin-top: 0.0625rem;
        color: var(--color-ink-muted);
      }

      .try-outcome > div {
        display: grid;
        min-width: 0;
        gap: 0.125rem;
      }

      .try-outcome strong {
        font-size: var(--text-sm);
        font-weight: 600;
        line-height: 1.25rem;
      }

      .try-outcome small {
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
        line-height: 1.25rem;
      }

      .try-outcome .try-outcome-meta {
        font-size: 0.8125rem;
        font-variant-numeric: tabular-nums;
      }

      .try-outcome[data-tone="error"] {
        background: var(--md-sys-color-error-container);
        color: var(--md-sys-color-on-error-container);
      }

      .try-outcome[data-tone="error"] > svg,
      .try-outcome[data-tone="error"] small {
        color: var(--md-sys-color-on-error-container);
      }

      .try-summary {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--space-2);
        margin: 0;
        font-size: var(--text-sm);
      }

      .try-summary > svg {
        color: var(--color-success);
      }

      .try-summary > strong {
        color: var(--color-ink);
        font-weight: 600;
      }

      .try-summary > span {
        color: var(--color-ink-muted);
      }

      .try-spinner {
        width: 1.25rem;
        height: 1.25rem;
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

      /* Lists. */
      .try-list {
        margin: 0;
        overflow: hidden;
        border-radius: var(--radius-lg);
        background: var(--color-panel-raised);
        padding: var(--space-1) 0;
        list-style: none;
      }

      .try-row {
        display: grid;
        grid-template-columns: 3.5rem minmax(0, 1fr) auto;
        align-items: center;
        gap: var(--space-4);
        padding: var(--space-3) var(--space-4);
      }

      .try-row + .try-row {
        border-top: 1px solid var(--color-edge);
      }

      .try-art {
        width: 3.5rem;
        height: 3.5rem;
        overflow: hidden;
        border-radius: var(--radius-sm);
        background: var(--color-surface-raised);
      }

      .try-art--round {
        border-radius: 50%;
      }

      .try-art--large {
        width: 5rem;
        height: 5rem;
        border-radius: var(--radius-md);
      }

      .try-row-copy {
        display: grid;
        min-width: 0;
        gap: 0.125rem;
      }

      .try-row-copy > strong {
        display: flex;
        align-items: center;
        gap: var(--space-2);
        min-width: 0;
        color: var(--color-ink);
        font-size: var(--text-md);
        font-weight: 500;
        line-height: 1.5rem;
      }

      .try-row-copy > strong > span,
      .try-row-copy > small {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .try-row-copy > small {
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
        line-height: 1.25rem;
      }

      .try-row-copy > .try-ids {
        display: flex;
        gap: var(--space-3);
        font-size: 0.8125rem;
        font-variant-numeric: tabular-nums;
        opacity: 0.85;
      }

      .try-explicit {
        display: inline-grid;
        width: 1.125rem;
        height: 1.125rem;
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
        min-width: 2.5rem;
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
        font-variant-numeric: tabular-nums;
        text-align: right;
      }

      .try-row-actions {
        display: flex;
        gap: 0;
      }

      .try-row-actions > button {
        border-radius: 50%;
        color: var(--color-ink-muted);
      }

      .try-row-actions > button:hover:not(:disabled) {
        color: var(--color-ink);
      }

      /* Cards for lyrics, media and explanations. */
      .try-card {
        display: grid;
        gap: var(--space-3);
        border-radius: var(--radius-lg);
        background: var(--color-panel-raised);
        padding: var(--space-4) var(--space-5);
      }

      .try-facts {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--space-2) var(--space-4);
        margin: 0;
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
      }

      .try-pill {
        border-radius: var(--radius-sm);
        background: var(--md-sys-color-secondary-container);
        color: var(--md-sys-color-on-secondary-container);
        padding: 0.25rem 0.75rem;
        font-weight: 600;
      }

      .try-lyric-lines {
        display: grid;
        max-height: 22rem;
        gap: var(--space-2);
        margin: 0;
        overflow: auto;
        padding: 0;
        list-style: none;
      }

      .try-lyric-lines > li {
        display: grid;
        grid-template-columns: 4.75rem minmax(0, 1fr);
        gap: var(--space-3);
        color: var(--color-ink);
        font-size: var(--text-md);
        line-height: 1.5rem;
      }

      .try-lyric-lines.untimed > li {
        grid-template-columns: minmax(0, 1fr);
      }

      .try-lyric-lines time {
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
        font-variant-numeric: tabular-nums;
        line-height: 1.5rem;
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
        font-size: 1.25rem;
        font-weight: 500;
        overflow-wrap: anywhere;
      }

      .try-playlist-head small {
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
      }

      .try-media-title {
        display: flex;
        align-items: center;
        gap: var(--space-3);
        min-width: 0;
      }

      .try-media-title > strong {
        overflow: hidden;
        font-size: var(--text-md);
        font-weight: 500;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .try-media-icon {
        display: grid;
        width: 2.5rem;
        height: 2.5rem;
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
        grid-template-columns: repeat(auto-fit, minmax(9.5rem, 1fr));
        gap: var(--space-3) var(--space-4);
        margin: 0;
      }

      .try-media-facts dt {
        color: var(--color-ink-muted);
        font-size: var(--text-sm);
      }

      .try-media-facts dd {
        margin: 0;
        color: var(--color-ink);
        font-size: var(--text-md);
        font-variant-numeric: tabular-nums;
        font-weight: 500;
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
        font-size: var(--text-sm);
      }

      .try-explain-card {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
      }

      .try-explain-card > p {
        flex: 1 1 16rem;
        margin: 0;
        color: var(--color-ink);
        font-size: var(--text-sm);
      }
    }

    @media (max-width: 760px) {
      .try-dialog > .segmented-nav {
        padding: var(--space-2) var(--space-4) 0;
      }

      .try-body {
        min-height: 0;
        padding: var(--space-4);
      }

      .try-fields--two,
      .try-fields--details {
        grid-template-columns: minmax(0, 1fr);
      }

      .try-searchbar {
        padding-left: var(--space-4);
      }

      .try-chips {
        flex-wrap: nowrap;
        margin: 0 calc(var(--space-4) * -1);
        overflow-x: auto;
        padding: 0 var(--space-4) var(--space-1);
        scrollbar-width: none;
      }

      .try-chips::-webkit-scrollbar {
        display: none;
      }

      .try-chip,
      .try-chip-divider {
        flex-shrink: 0;
      }

      .try-row {
        grid-template-columns: 3rem minmax(0, 1fr);
        row-gap: var(--space-1);
        gap: var(--space-3);
      }

      .try-row-end {
        grid-column: 2;
        justify-content: space-between;
      }

      .try-art {
        width: 3rem;
        height: 3rem;
      }

      .try-lyric-lines > li {
        grid-template-columns: 4rem minmax(0, 1fr);
        font-size: var(--text-sm);
      }

      .try-quality > .try-field,
      .try-quality > button,
      .try-form-end > button {
        flex: 1 1 100%;
      }
    }
  }
</style>
