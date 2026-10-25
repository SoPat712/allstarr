<script lang="ts">
  import { onMount } from "svelte";
  import { ApiError, home, type ProviderDefinition } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import SelectField from "$lib/components/SelectField.svelte";
  import ProblemContextCard from "$lib/components/ProblemContextCard.svelte";
  import { listenerSupport, newReportRequestId, reportKinds, reportReturnHref, reportSelection,
    supportError, type ProblemContext, type ReportKind, type ReportReceipt, type ReportSelector } from "$lib/support";

  let { query }: { query: URLSearchParams } = $props();
  let selector = $state<ReportSelector>();
  let selectionError = $state("");
  let context = $state<ProblemContext | null>(null);
  let providers = $state<ProviderDefinition[]>([]);
  let loading = $state(true);
  let busy = $state(false);
  let error = $state("");
  let contextError = $state("");
  let kind = $state<ReportKind>("playback");
  let note = $state("");
  let receipt = $state<ReportReceipt | null>(null);
  let routeKey = "";
  let requestId = "";
  let submittedKey = "";
  let generation = 0;
  const back = $derived(reportReturnHref(query.get("from") ?? "/"));
  const blocked = $derived(loading || busy || Boolean(selectionError || contextError) || !context);

  $effect(() => {
    const key = query.toString();
    if (key === routeKey) return;
    routeKey = key;
    const selected = reportSelection(query);
    selector = selected.selector;
    selectionError = selected.error ?? "";
    context = null; receipt = null; note = ""; error = ""; requestId = ""; submittedKey = "";
    void preview();
  });
  async function preview() {
    const current = ++generation;
    contextError = "";
    if (selectionError) { loading = false; return; }
    loading = true;
    try {
      const result = await listenerSupport.preview(selector);
      if (current === generation) context = result;
    } catch (cause) { if (current === generation) contextError = supportError(cause, "Report details could not be loaded. Try again."); }
    finally { if (current === generation) loading = false; }
  }
  async function general() {
    selector = undefined; selectionError = ""; error = ""; context = null;
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
    if (!routeKey) { const selected = reportSelection(query); selector = selected.selector; selectionError = selected.error ?? ""; void preview(); }
    void home.schema().then(schema => { providers = schema.providers ?? []; }).catch(() => {});
    return () => { generation++; };
  });
</script>

<section class="settings-content" aria-label="Report a problem">
  <header class="panel-heading"><div><h2>{receipt ? "Report saved" : "Tell us what went wrong"}</h2><p>Reports stay inside Allstarr for your administrator to review.</p></div><div class="panel-heading-actions"><Button variant="secondary" href={back}>{receipt ? "Back to music" : "Cancel"}</Button></div></header>
  {#if receipt}
    <article class="settings-card"><header><h3>Your report was saved.</h3></header><p class="settings-impact" role="status">Your administrator can review it in Activity → Reports.</p><footer><Button href="#/getting-started">Getting started</Button></footer></article>
  {:else}
    {#if selectionError || contextError}
      <div class="degraded-banner" role="alert"><p>{selectionError || contextError}</p>
        <div class="panel-heading-actions">{#if !selectionError}<Button variant="secondary" disabled={loading || busy} onclick={() => void preview()}>Refresh details</Button>{/if}
          {#if selector?.playback}<Button variant="secondary" disabled={loading || busy} onclick={() => void withoutPlayback()}>Continue without playback details</Button>{/if}
          <Button variant="secondary" disabled={loading || busy} onclick={() => void general()}>Continue without song details</Button></div></div>
    {/if}
    {#if loading}<Skeleton class="settings-card skeleton-panel" aria-label="Loading report details" aria-busy="true" />
    {:else if context && !selectionError && !contextError}<ProblemContextCard {context} {providers} />{/if}
    <form class="settings-card" aria-label="Problem report" onsubmit={(event) => void submit(event)}>
      <header><h3>Your note</h3></header>
      <fieldset class="settings-fields" disabled={busy}>
        <div class="setting-field settings-row"><div><strong>Problem</strong><small>Choose the closest description.</small></div><SelectField label="Problem" value={kind} options={reportKinds} onchange={(value) => kind = value as ReportKind} /></div>
        <label class="setting-field settings-row"><div><strong>What happened?</strong><small>Include the app and what you tried. Leave out passwords, tokens and private links.</small></div><textarea aria-label="What happened?" required maxlength="2000" rows="5" bind:value={note}></textarea></label>
        {#if error}<p class="notice-info" role="alert">{error}</p>{/if}
        <footer><span>{note.length}/2000</span><Button type="submit" disabled={blocked || !note.trim()}>{busy ? "Saving…" : "Send report"}</Button></footer>
      </fieldset>
    </form>
  {/if}
</section>
