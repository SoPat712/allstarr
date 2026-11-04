<script lang="ts">
  import { onDestroy } from "svelte";
  import { goto } from "$app/navigation";
  import { mixes, type MixPreview, type MixSeed } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import MixSongs from "$lib/components/MixSongs.svelte";
  let { onsaved, onclose, initialQuery = "", appsAllowed = false }: { onsaved: () => Promise<void>; onclose: () => void; initialQuery?: string; appsAllowed?: boolean } = $props();
  let query = $state(""), results = $state<MixSeed[]>([]), searching = $state(false), searchError = $state(""), servicesPending = $state(false);
  let seed = $state<MixSeed | null>(null), preview = $state<MixPreview | null>(null), open = $state(true), busy = $state(false), saving = $state(false), error = $state("");
  let addToApps = $state(true);
  let length = $state("25"), name = $state(""), request = 0, previewRequest = 0, disposed = false;
  // A song or artist handed over from Recap starts the search.
  $effect(() => { if (initialQuery) query = initialQuery; });
  $effect(() => {
    const value = query.trim(), version = ++request;
    results = []; searchError = ""; searching = value.length >= 2; servicesPending = false;
    if (value.length < 2) return;
    const current = () => !disposed && version === request;
    const timer = setTimeout(async () => {
      try {
        // Library songs show as soon as they arrive; service songs are added below them later.
        const next = await mixes.seeds(value, rows => { if (current() && rows.length) { results = rows; searching = false; servicesPending = true; } });
        if (current()) { results = next; if (!next.length) searchError = "No songs found. Try another search."; }
      }
      catch { if (current()) searchError = "Search is unavailable. Try again."; }
      finally { if (current()) { searching = false; servicesPending = false; } }
    }, 300);
    return () => clearTimeout(timer);
  });
  async function loadPreview() {
    if (!seed) return;
    const version = ++previewRequest; busy = true; error = ""; preview = null;
    try { const next = await mixes.start(seed, Number(length)); if (!disposed && version === previewRequest) preview = next; }
    catch { if (!disposed && version === previewRequest) error = "The preview is unavailable. Try again."; }
    finally { if (!disposed && version === previewRequest) busy = false; }
  }
  // A song can be picked before services answer, so their late rows are dropped instead of reopening the list.
  function choose(value: MixSeed) { request++; seed = value; name = `${value.title} Mix`.slice(0, 200); open = true; results = []; servicesPending = false; void loadPreview(); }
  async function save() {
    if (busy || saving || !preview?.readiness.ready || !name.trim()) return;
    busy = true; saving = true; error = "";
    try {
      const saved = await mixes.save(preview.previewId, name.trim());
      if (disposed) return;
      if (addToApps && appsAllowed) {
        try { await mixes.add(saved.id); }
        catch { toast.error("Your mix was saved, but could not be added to your apps. Open it to try again."); }
      }
      open = false; query = "";
      toast.success("Saved — it's in Your mixes", { label: "Open", run: () => { void goto(`#/for-you?mix=${encodeURIComponent(saved.id)}`, { keepFocus: true, noScroll: true }); } });
      await onsaved();
      onclose();
    } catch { if (!disposed) error = "The mix could not be saved. Preview it again and retry."; }
    finally { if (!disposed) { busy = false; saving = false; } }
  }
  onDestroy(() => { disposed = true; request++; previewRequest++; });
</script>
<Dialog.Root bind:open={() => open, value => { if (!saving) { open = value; if (!value) onclose(); } }}><Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog start-mix-dialog">
  <header><div><Dialog.Title>Start a mix</Dialog.Title><Dialog.Description>Search for a song or an artist, then pick a song to build around.</Dialog.Description></div><Dialog.Close type="button" class="icon-button" aria-label="Close preview" disabled={saving}>×</Dialog.Close></header>
  <div class="start-body">
    <section aria-label="Start a mix" inert={saving}>
      <SearchField bind:value={query} label="Start a mix from a song or artist" placeholder="Song or artist" />
      {#if searching}<p role="status">Searching…</p>{/if}{#if searchError}<p role="status">{searchError}</p>{/if}
      {#if results.length}<div class="seed-results">{#each results.slice(0, 8) as row, index (`${index}:${row.id}`)}<button type="button" onclick={() => choose(row)} aria-label={`${row.title} by ${row.artist}`}><MediaArtwork url={row.artworkUrl} /><span><strong>{row.title}</strong><small>{row.kind === "artist" ? "Artist" : row.artist}</small></span></button>{/each}
        {#if servicesPending && results.length < 8}<p class="seed-pending" role="status">Still searching services…</p>{/if}</div>{/if}
    </section>
    {#if seed}
      <div class="selected-seed"><MediaArtwork url={seed.artworkUrl} /><div><strong>{seed.title}</strong><small>{seed.artist}</small></div><span>Selected</span></div>
      <div class="preview-heading"><h3>First songs in the mix</h3><SelectField bind:value={length} label="Mix length" disabled={saving} options={[{ value: "25", label: "25 songs" }, { value: "50", label: "50 songs" }]} onchange={() => void loadPreview()} /></div>
      {#if busy && !preview}<RowsSkeleton count={3} />{:else if preview}<MixSongs songs={preview.songs} />{#if !preview.readiness.ready}<p class="muted">{preview.readiness.message}</p>{/if}{/if}
      {#if error}<ErrorState title="The preview could not be loaded" detail={error} onretry={loadPreview} />{/if}
      <label class="name-field">Mix name<input bind:value={name} disabled={saving} maxlength="200" /></label>
      <label class="add-option"><Checkbox class="mix-apps-option" bind:checked={addToApps} disabled={saving || !appsAllowed} aria-label="Add to my apps" /><span>Add to my apps</span></label>
      {#if !appsAllowed}<p class="muted">Your admin hasn't allowed adding mixes.</p>{/if}
    {/if}
  </div>
  <footer class="dialog-actions"><Dialog.Close type="button" disabled={saving} class={buttonVariants({ variant: "outline" })}>Cancel</Dialog.Close><Button disabled={busy || saving || !preview?.readiness.ready || !name.trim()} onclick={save}>{saving ? "Starting…" : "Start mix"}</Button></footer>
</Dialog.Content></Dialog.Portal></Dialog.Root>
<style>
  :global(.start-mix-dialog) { display: flex; width: min(640px, calc(100vw - 32px)); flex-direction: column; overflow: hidden; }
  .start-body { display: grid; min-height: 0; gap: var(--space-4); overflow-y: auto; padding: var(--surface-gutter); }
  .seed-results { border: 1px solid var(--color-edge); border-radius: var(--radius-md); margin-top: var(--space-3); }
  .seed-results button, .selected-seed { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-2) var(--space-3); width: 100%; min-height: 60px; text-align: left; }
  .seed-results button + button { border-top: 1px solid var(--color-edge); }
  .seed-results button:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: -2px; }
  .seed-results :global(.media-art), .selected-seed :global(.media-art) { width: 40px; height: 40px; flex: 0 0 40px; }
  .selected-seed { border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: var(--color-signal-muted); }
  .selected-seed > div { flex: 1; min-width: 0; }
  .selected-seed > span { font-size: var(--text-xs); color: var(--color-signal-text); }
  strong, small { display: block; overflow-wrap: anywhere; }
  small, .seed-pending, .muted { color: var(--color-ink-muted); font-size: var(--text-sm); }
  .seed-pending { padding: var(--space-3); }
  .preview-heading { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  h3 { font-size: var(--text-sm); font-weight: 600; }
  .name-field { display: grid; gap: var(--space-2); font-size: var(--text-sm); font-weight: 600; }
  .name-field input { min-width: 0; width: 100%; }
  .add-option { display: flex; align-items: center; min-height: 44px; gap: var(--space-3); font-size: var(--text-sm); cursor: pointer; }
  .add-option :global(.mix-apps-option) { width: 44px; height: 44px; }
  @media (max-width: 599px) { :global(.start-mix-dialog) { width: calc(100vw - 16px); } }
</style>
