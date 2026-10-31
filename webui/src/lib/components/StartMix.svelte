<script lang="ts">
  import { onDestroy } from "svelte";
  import { goto } from "$app/navigation";
  import { mixes, type MixPreview, type MixSeed } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Dialog } from "$lib/components/ui/dialog";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import SearchField from "$lib/components/SearchField.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import MediaArtwork from "$lib/components/MediaArtwork.svelte";
  import MixSongs from "$lib/components/MixSongs.svelte";
  let { onsaved }: { onsaved: () => Promise<void> } = $props();
  let query = $state(""), results = $state<MixSeed[]>([]), searching = $state(false), searchError = $state("");
  let seed = $state<MixSeed | null>(null), preview = $state<MixPreview | null>(null), open = $state(false), busy = $state(false), saving = $state(false), error = $state("");
  let length = $state("25"), name = $state(""), request = 0, previewRequest = 0, disposed = false;
  $effect(() => {
    const value = query.trim(), version = ++request;
    results = []; searchError = ""; searching = value.length >= 2;
    if (value.length < 2) return;
    const timer = setTimeout(async () => {
      try { const next = await mixes.seeds(value); if (!disposed && version === request) { results = next; if (!next.length) searchError = "No songs found. Try another search."; } }
      catch { if (!disposed && version === request) searchError = "Search is unavailable. Try again."; }
      finally { if (!disposed && version === request) searching = false; }
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
  function choose(value: MixSeed) { seed = value; name = `${value.title} Mix`.slice(0, 200); open = true; results = []; void loadPreview(); }
  async function save() {
    if (busy || !preview?.readiness.ready || !name.trim()) return;
    busy = true; saving = true; error = "";
    try {
      const saved = await mixes.save(preview.previewId, name.trim());
      if (disposed) return;
      open = false; query = "";
      toast.success("Saved — it's in Your mixes", { label: "Open", run: () => { void goto(`#/for-you?mix=${encodeURIComponent(saved.id)}`, { keepFocus: true, noScroll: true }); } });
      await onsaved();
    } catch { if (!disposed) error = "The mix could not be saved. Preview it again and retry."; }
    finally { if (!disposed) { busy = false; saving = false; } }
  }
  onDestroy(() => { disposed = true; request++; previewRequest++; });
</script>
<section aria-label="Start a mix">
  <h2>Start a mix</h2><SearchField bind:value={query} label="Start a mix from a song or artist" placeholder="Start a mix from a song or artist" hiddenLabel />
  {#if searching}<p role="status">Searching…</p>{/if}{#if searchError}<p role="status">{searchError}</p>{/if}
  {#if results.length}<div class="settings-card seed-results">{#each results.slice(0, 8) as row (row.id)}<button type="button" onclick={() => choose(row)} aria-label={`${row.title} by ${row.artist}`}><MediaArtwork url={row.artworkUrl} /><span><strong>{row.title}</strong><small>{row.artist}</small></span></button>{/each}</div>{/if}
</section>
<Dialog.Root bind:open={() => open, value => { if (!saving) open = value; }}><Dialog.Portal><Dialog.Overlay class="dialog-overlay" /><Dialog.Content class="source-dialog mix-preview">
  <header><div><Dialog.Title>Mix from {seed?.title}</Dialog.Title><Dialog.Description>Songs that fit with {seed?.title}</Dialog.Description></div><Dialog.Close type="button" class="icon-button" aria-label="Close preview" disabled={saving}>×</Dialog.Close></header>
  <div class="preview-body"><SelectField bind:value={length} label="Mix length" disabled={saving} options={[{ value: "25", label: "25 songs" }, { value: "50", label: "50 songs" }]} onchange={() => void loadPreview()} />
    {#if busy && !preview}<Skeleton class="skeleton-panel" aria-label="Loading preview" />{:else if preview}<MixSongs songs={preview.songs} />{#if !preview.readiness.ready}<p>{preview.readiness.message}</p>{/if}{/if}
    {#if error}<p class="notice-error" role="alert">{error}</p><Button variant="secondary" disabled={busy} onclick={loadPreview}>Try again</Button>{/if}
  </div>
  <footer class="dialog-actions"><label>Mix name<input bind:value={name} disabled={saving} maxlength="200" /></label><Dialog.Close type="button" disabled={saving} class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close><Button disabled={busy || !preview?.readiness.ready || !name.trim()} onclick={save}>Save to my mixes</Button></footer>
</Dialog.Content></Dialog.Portal></Dialog.Root>
<style>
  h2 { font-size: 1.125rem; font-weight: 600; margin-bottom: var(--space-3); }
  section { position: relative; min-width: 0; max-width: 560px; }
  section :global(.search-field), section :global(input[type="search"]) { width: 100%; }
  section > p { color: var(--color-ink-muted); margin-top: var(--space-2); }
  .seed-results { position: absolute; top: 100%; left: 0; right: 0; z-index: 1; padding: var(--space-2); max-height: 320px; overflow-y: auto; box-shadow: var(--shadow-lg); }
  .seed-results button { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-2); width: 100%; min-height: 60px; text-align: left; }
  .seed-results button:focus-visible { outline: 2px solid var(--color-signal); border-radius: var(--radius-md); }
  .seed-results :global(.media-art) { width: 44px; height: 44px; flex: 0 0 44px; }
  .seed-results span { min-width: 0; } strong, small { display: block; overflow-wrap: anywhere; } small { color: var(--color-ink-muted); }
  :global(.mix-preview) { display: flex; flex-direction: column; overflow: hidden; }
  .preview-body { padding: var(--surface-gutter); overflow-y: auto; min-height: 0; }
  footer label { flex: 1 1 100%; min-width: 0; } footer input { width: 100%; margin-top: var(--space-2); }
  footer { flex-wrap: wrap; }
</style>
