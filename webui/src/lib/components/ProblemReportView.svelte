<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, home, type ProviderDefinition } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import PageStatus from "$lib/components/patterns/PageStatus.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import ProblemContextCard from "$lib/components/ProblemContextCard.svelte";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { listenerSupport, newReportRequestId, reportKinds, reportReturnHref, reportSelection,
    supportError, type ProblemContext, type ReportKind, type ReportReceipt, type ReportSelector } from "$lib/support";

  const reasonLabels: Record<ReportKind, string> = { playback: "Won't play", seek: "Can't seek", "wrong-match": "Wrong song", "wrong-lyrics": "Wrong lyrics", metadata: "Wrong details", other: "Something else" };
  let { query }: { query: URLSearchParams } = $props();
  let selector = $state<ReportSelector>();
  let selectionError = $state("");
  let context = $state<ProblemContext | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let contextError = $state("");
  let kind = $state<ReportKind>("other");
  let attachedSelector = $state<ReportSelector>();
  let note = $state("");
  let receipt = $state<ReportReceipt | null>(null);
  let routeKey: string | null = null;
  let requestId = "";
  let submittedKey = "";
  let generation = 0;
  const back = $derived(reportReturnHref(query.get("from") ?? "/"));
  const blocked = $derived(loading || busy || Boolean(selectionError || contextError) || Boolean(selector && !context));

  $effect(() => {
    const key = query.toString();
    if (key === routeKey) return;
    routeKey = key;
    const selected = reportSelection(query);
    selector = selected.selector;
    attachedSelector = selected.selector;
    selectionError = selected.error ?? "";
    context = null; receipt = null; kind = "other"; note = ""; error = ""; requestId = ""; submittedKey = "";
    void preview();
  });
  async function preview() {
    const current = ++generation;
    contextError = "";
    if (selectionError || !selector) { context = null; loading = false; return; }
    loading = true;
    try {
      const result = await listenerSupport.preview(selector);
      if (current === generation) context = result;
    } catch (cause) { if (current === generation) contextError = supportError(cause, "Report details could not be loaded. Try again."); }
    finally { if (current === generation) loading = false; }
  }
  async function general() {
    selector = undefined; attachedSelector = undefined; selectionError = ""; error = ""; context = null;
    await preview();
  }
  async function includePlaying(include: boolean) {
    if (!attachedSelector) return;
    const { playback, ...song } = attachedSelector;
    selector = include ? attachedSelector : Object.keys(song).length ? song : undefined;
    await preview();
  }
  async function withoutPlayback() {
    if (!selector?.itemId) return;
    selector = { itemId: selector.itemId }; error = ""; context = null;
    await preview();
  }
  async function submit(event: SubmitEvent) {
    event.preventDefault();
    if (blocked || receipt || !note.trim()) return;
    busy = true; error = "";
    const normalized = { kind, note: note.trim(), ...(selector ? { selector } : {}) };
    const key = JSON.stringify(normalized);
    if (!requestId || key !== submittedKey) { requestId = newReportRequestId(); submittedKey = key; }
    try { receipt = await listenerSupport.create({ requestId, ...normalized }); }
    catch (cause) {
      error = supportError(cause, "The report could not be saved. Try again.");
      if (cause instanceof ApiError && cause.status === 404) contextError = "The song or playback details are no longer available. Refresh them or continue with fewer details.";
    } finally { busy = false; }
  }
  onMount(() => {
    void home.schema().then(schema => { providers = schema.providers ?? []; }).catch(() => {});
    return () => { generation++; };
  });
</script>

<section class="settings-content report-view" aria-label="Report a problem">
  <header class="panel-heading"><div><h2>{receipt ? "Report saved" : "Tell us what went wrong"}</h2><p>Reports stay inside Allstarr for your administrator to review.</p></div><div class="panel-heading-actions"><Button variant="secondary" href={back}>{receipt ? "Back to music" : "Cancel"}</Button></div></header>
  <PageStatus label="Report status" items={[
    { label: receipt ? "Saved for your administrator" : selector ? "Song report" : "General report" },
    { label: receipt ? "Available in Activity → Reports" : selector ? loading ? "Loading song details…" : contextError ? "Song details need attention" : "Song details are included" : "Add a note about what happened" },
  ]} />
  {#if receipt}
    <article class="settings-card"><header><h3>Your report was saved.</h3></header><p class="settings-impact" role="status">Your administrator can review it in Activity → Reports.</p><footer><Button href="#/connect">Getting started</Button></footer></article>
  {:else}
    {#if selectionError || contextError}
      <div class="degraded-banner" role="alert"><p>{selectionError || contextError}</p>
        <div class="panel-heading-actions">{#if !selectionError}<Button variant="secondary" disabled={loading || busy} onclick={() => void preview()}>Refresh details</Button>{/if}
          {#if selector?.playback}<Button variant="secondary" disabled={loading || busy} onclick={() => void withoutPlayback()}>Continue without playback details</Button>{/if}
          <Button variant="secondary" disabled={loading || busy} onclick={() => void general()}>Continue without song details</Button></div></div>
    {/if}
    {#if loading}<RowsSkeleton count={2} />
    {:else if context && !selectionError && !contextError}<ProblemContextCard {context} {providers} />{/if}
    <form class="settings-card" aria-label="Problem report" onsubmit={(event) => void submit(event)}>
      <fieldset class="settings-fields" disabled={busy}>
        {#if selector}
          <fieldset class="report-reasons" disabled={busy}>
            <legend>Problem</legend><p>Choose the closest description.</p>
            <div class="reason-scroll"><ThreeDetentSwitch label="Problem" options={reportKinds.map(reason => ({ value: reason.value, label: reasonLabels[reason.value] }))} value={kind} disabled={busy} onchange={value => kind = value} /></div>
          </fieldset>
        {:else}
          <div class="setting-field settings-row"><div><strong>Problem</strong><small>Choose the closest description.</small></div><SelectField label="Problem" value={kind} options={reportKinds} onchange={(value) => kind = value as ReportKind} /></div>
        {/if}
        <label class="report-note"><strong>What's wrong?</strong><textarea aria-label="What's wrong?" required maxlength="2000" rows="5" bind:value={note}></textarea><small>Include the app and what you tried. Leave out passwords, tokens and private links.</small></label>
        <label class="report-include"><Checkbox aria-label="Include what was playing" checked={Boolean(selector?.playback)} disabled={busy || !attachedSelector?.playback} onCheckedChange={value => void includePlaying(value === true)} /><span>Include what was playing{#if !attachedSelector?.playback}<small>No playback details are available. Report a song from its menu to attach them.</small>{/if}</span></label>
        {#if error}<ErrorState title="The report could not be saved." detail={error} />{/if}
        <footer><span class="report-note-hint">{#if !selector}Reporting one song? Use its report action to include the song details.<br />{/if}{note.length}/2000</span><Button type="submit" disabled={blocked || !note.trim()}>{busy ? "Saving…" : "Send report"}</Button></footer>
      </fieldset>
    </form>
  {/if}
</section>


<style>
  .report-view { min-width: 0; }
  .report-view > :global(.panel-heading) { padding-inline: 0; border: 0; }
  .report-view form { min-width: 0; }
  .report-note { display: grid; gap: var(--space-2); padding: var(--surface-gutter); font-size: var(--text-sm); }
  .report-note textarea { width: 100%; min-width: 0; min-height: 8rem; padding: var(--space-3); border: 1px solid var(--color-edge); border-radius: var(--radius-md); background: transparent; color: var(--color-ink); font: inherit; resize: vertical; }
  .report-note small, .report-include small { display: block; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .report-include { display: flex; align-items: center; gap: var(--space-2); padding-inline: var(--surface-gutter); font-size: var(--text-sm); }
  .reason-scroll { min-width: 0; overflow-x: auto; }
  .reason-scroll :global(.seg) { width: max-content; flex-wrap: nowrap; }
  .report-reasons { display: grid; gap: var(--space-2); margin: 0; border: 0; padding: var(--surface-gutter); border-bottom: 1px solid var(--color-edge); }
  .report-reasons legend { float: left; width: 100%; padding: 0; font-size: var(--text-sm); font-weight: 600; }
  .report-reasons p { margin: 0; color: var(--color-ink-muted); font-size: var(--text-sm); }
  .report-note-hint { color: var(--color-ink-muted); font-size: var(--text-xs); line-height: 1.5; }
  .settings-fields > footer { gap: var(--space-3); }
  @media (max-width: 599px) { .settings-fields > footer { align-items: stretch; flex-direction: column; } }
</style>
