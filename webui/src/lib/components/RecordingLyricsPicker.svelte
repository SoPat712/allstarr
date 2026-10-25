<script lang="ts">
  import { onDestroy } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { ApiError, recordingLyrics, type MatchAuthorityScope, type ProviderDefinition, type RecordingLyricsReview } from "$lib/api";
  import { lyricsSourceDisplayName } from "$lib/sources";
  import { toast } from "$lib/toasts";
  import LyricsAttribution from "$lib/components/LyricsAttribution.svelte";

  let { recordingId, providers, allowHousehold = false }: {
    recordingId: string;
    providers: ProviderDefinition[];
    allowHousehold?: boolean;
  } = $props();
  let scope = $state<MatchAuthorityScope>("personal");
  let snapshot = $state<RecordingLyricsReview | null>(null);
  let selectedId = $state("");
  let loading = $state(true);
  let saving = $state(false);
  let loadError = $state("");
  let actionError = $state("");
  let refresh = $state(0);
  let alive = true;
  let saveCancellation: AbortController | undefined;
  const selected = $derived(snapshot?.sources.find(source => source.sourceId === selectedId));
  const savedChoice = $derived(scope === "personal" ? snapshot?.choices.personal : snapshot?.choices.household);
  const sourceName = (id: string) => lyricsSourceDisplayName(providers, id);

  $effect(() => {
    const id = recordingId;
    const targetScope = allowHousehold ? scope : "personal";
    void refresh;
    const cancellation = new AbortController();
    void load(id, targetScope, cancellation.signal);
    return () => cancellation.abort();
  });
  onDestroy(() => { alive = false; saveCancellation?.abort(); });

  async function load(id: string, targetScope: MatchAuthorityScope, signal: AbortSignal) {
    loading = true;
    loadError = "";
    snapshot = null;
    try {
      const result = await recordingLyrics.get(id, targetScope, signal);
      if (signal.aborted || !alive) return;
      snapshot = result;
      const choice = targetScope === "personal" ? result.choices.effective : result.choices.household;
      selectedId = result.sources.find(source => source.sourceId === choice?.sourceId && source.available)?.sourceId
        ?? result.sources.find(source => source.available)?.sourceId ?? "";
    } catch (error) {
      if (!signal.aborted && alive) loadError = error instanceof Error ? error.message : "Lyrics could not be loaded. Reload the sources and try again.";
    } finally { if (!signal.aborted && alive) loading = false; }
  }

  async function save(clear = false) {
    if (!snapshot || loading || saving || (!clear && !selected?.previewRevision)) return;
    const id = recordingId;
    const targetScope = scope;
    const source = selected;
    const expected = snapshot.choices.revision;
    saving = true;
    actionError = "";
    saveCancellation = new AbortController();
    try {
      const choices = clear
        ? await recordingLyrics.clear(id, targetScope, expected, saveCancellation.signal)
        : await recordingLyrics.choose(id, source!.sourceId, targetScope, expected, source!.previewRevision!, saveCancellation.signal);
      if (!alive) return;
      snapshot = { ...snapshot, choices };
      toast.success(clear ? (targetScope === "personal" ? "Following the household lyrics default." : "Following automatic lyrics selection.")
        : targetScope === "personal" ? "Lyrics source saved for you." : "Lyrics source saved for the household.");
    } catch (error) {
      if (!alive) return;
      actionError = error instanceof Error ? error.message : "The lyrics choice could not be saved. Try again.";
      toast.error(actionError);
      if (error instanceof ApiError && error.status === 409) refresh++;
    } finally { if (alive) saving = false; }
  }
</script>

<section class="lyrics-picker" aria-label="Lyrics sources" aria-busy={loading || saving}>
  <header><h3>Lyrics sources</h3><p>Preview the available words, then choose a source for this recording. This saves a source preference; its words may change later.</p></header>
  {#if allowHousehold}
    <fieldset disabled={saving}>
      <legend>Save lyrics choice for</legend>
      <label><input type="radio" bind:group={scope} value="personal" onchange={() => actionError = ""} /> Only me</label>
      <label><input type="radio" bind:group={scope} value="household" onchange={() => actionError = ""} /> Household</label>
    </fieldset>
  {/if}
  <p class="lyrics-help">{scope === "household" ? "Household previews use shared or public sources. Listeners with a personal choice keep their own preference." : "Your choice overrides the household default for you. An unavailable source falls back to your automatic source order."}</p>
  {#if actionError}<p class="lyrics-error">{actionError}</p>{/if}
  {#if loading}
    <div class="skeleton" aria-hidden="true" style="height: 6rem"></div><span role="status">Loading lyric sources…</span>
  {:else if loadError}
    <p class="lyrics-error" role="alert">{loadError}</p>
    <Button variant="secondary" onclick={() => refresh++}>Reload sources</Button>
  {:else if snapshot}
    <p class="lyrics-help">{#if savedChoice}Saved source: {sourceName(savedChoice.sourceId)}.{:else if scope === "personal" && snapshot.choices.household}Following household source: {sourceName(snapshot.choices.household.sourceId)}.{:else}Following automatic lyrics selection.{/if} A saved source falls back automatically when unavailable.</p>
    <div class="lyrics-comparison">
      <div class="lyrics-source-list" aria-label="Available lyric sources">
        {#each snapshot.sources as source (source.sourceId)}
          <Button variant={selectedId === source.sourceId ? "secondary" : "outline"} class="lyrics-source"
            aria-pressed={selectedId === source.sourceId} disabled={!source.available || saving}
            onclick={() => { selectedId = source.sourceId; actionError = ""; }}>
            <span>{sourceName(source.sourceId)}</span><small>{!source.available ? "Unavailable" : source.format === "synced" ? "Synced" : "Plain"}</small>
          </Button>
        {/each}
      </div>
      <div class="lyrics-preview">
        {#if selected?.available}
          <h4>{sourceName(selected.sourceId)}</h4>
          <LyricsAttribution sourceId={selected.sourceId} attribution={selected.attribution} />
          <textarea readonly aria-label="Lyrics preview" rows={Math.min(12, Math.max(3, selected.preview?.split("\n").length ?? 3))} value={selected.preview ?? ""}></textarea>
          {#if selected.truncated}<p class="lyrics-help">Preview shortened. The choice applies to the complete source result.</p>{/if}
        {:else}
          <p>No source has lyrics available for this recording. Connect a source or reload to try again.</p>
        {/if}
      </div>
    </div>
    <div class="lyrics-actions">
      <Button disabled={loading || saving || !selected?.available} onclick={() => save()}>Use this source for this recording</Button>
      {#if savedChoice}<Button variant="secondary" disabled={saving} onclick={() => save(true)}>{scope === "personal" ? "Use household default" : "Use automatic selection"}</Button>{/if}
      <Button variant="outline" disabled={saving} onclick={() => refresh++}>Reload sources</Button>
    </div>
  {/if}
</section>

<style>
  .lyrics-picker { min-width: 0; padding: var(--surface-gutter); border: 1px solid var(--color-border); border-radius: var(--radius-lg); background: var(--color-surface); display: grid; gap: var(--space-3); }
  .lyrics-picker header, .lyrics-preview { min-width: 0; }
  h3, h4, p { margin: 0; overflow-wrap: anywhere; }
  h3 { font-size: var(--text-base); }
  h4 { font-size: var(--text-sm); }
  header p, .lyrics-help { color: var(--color-ink-muted); font-size: var(--text-sm); line-height: 1.5; }
  header p { margin-top: var(--space-2); }
  fieldset { display: flex; flex-wrap: wrap; gap: var(--space-2); padding: 0; border: 0; min-width: 0; }
  legend { margin-bottom: var(--space-2); font-size: var(--text-sm); font-weight: 700; }
  label { min-height: var(--control-md); display: flex; align-items: center; gap: var(--space-2); padding-inline: var(--space-3); border: 1px solid var(--color-border); border-radius: var(--radius-md); cursor: pointer; }
  input { accent-color: var(--color-signal); }
  .lyrics-comparison { display: grid; grid-template-columns: minmax(140px, 1fr) minmax(0, 2fr); gap: var(--space-3); align-items: start; }
  .lyrics-source-list { display: grid; gap: var(--space-2); min-width: 0; }
  .lyrics-source-list :global(.lyrics-source) { width: 100%; min-height: var(--control-md); height: auto; padding-block: var(--space-2); display: flex; flex-direction: column; gap: 0; white-space: normal; overflow-wrap: anywhere; }
  .lyrics-source-list small { font-weight: 400; }
  textarea { width: 100%; min-width: 0; max-height: 18rem; resize: vertical; background: transparent; color: inherit; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; font-size: var(--text-sm); line-height: 1.65; border: 1px solid var(--color-border); padding: var(--space-3); border-radius: var(--radius-md); }
  .lyrics-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .lyrics-actions :global(button) { white-space: normal; height: auto; min-height: var(--control-md); padding-block: var(--space-2); }
  .lyrics-error { color: var(--color-danger); font-size: var(--text-sm); line-height: 1.5; }
  @media (max-width: 650px) {
    .lyrics-comparison { grid-template-columns: minmax(0, 1fr); }
    .lyrics-actions { display: grid; grid-template-columns: minmax(0, 1fr); }
  }
</style>
