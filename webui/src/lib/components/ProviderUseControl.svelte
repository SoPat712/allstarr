<script lang="ts">
  import { providerUse, type ProviderUseMode, type ProviderUseSettings } from "$lib/api";
  import SourceUseSwitch from "$lib/components/SourceUseSwitch.svelte";
  import { Button } from "$lib/components/ui/button";
  import { toast } from "$lib/toasts";

  let { providerId, administrator, onSaved }: { providerId: string; administrator: boolean; onSaved: () => void | Promise<void> } = $props();
  let data = $state<ProviderUseSettings | null>(null);
  let busy = $state(false), failed = $state(false);
  const source = $derived(data?.providers?.find(item => item.providerId === providerId));
  const meaning: Record<ProviderUseMode, string> = {
    automatic: "used for matching, playlists, search and playback.",
    manualOnly: "shows up in search and when you fix a match, but is never picked automatically.",
    off: "not used or shown anywhere.",
  };
  const names: Record<ProviderUseMode, string> = { automatic: "On", manualOnly: "Manual only", off: "Off" };

  $effect(() => {
    const id = providerId;
    let current = true;
    data = null; failed = false;
    providerUse.get().then(result => { if (current && providerId === id) data = result; })
      .catch(() => { if (current) failed = true; });
    return () => { current = false; };
  });
  async function save(mode: ProviderUseMode | null, scope: "personal" | "household") {
    if (!data || busy) return;
    busy = true;
    try {
      data = await providerUse.save(providerId, mode, scope, scope === "household" ? data.householdRevision : data.personalRevision);
      await onSaved();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The source setting could not be saved. Refresh and try again.");
      try { data = await providerUse.get(); } catch { /* keep the last known state */ }
    } finally { busy = false; }
  }
  async function reload() {
    busy = true; failed = false;
    try { data = await providerUse.get(); } catch { failed = true; } finally { busy = false; }
  }
</script>

{#if source || !data}
<section class="settings-card source-use" aria-labelledby="source-use-title">
  <header>
    <div>
      <h3 id="source-use-title">Use this source</h3>
      {#if source}<small>{names[source.effectiveMode]}: {meaning[source.effectiveMode]}</small>{/if}
    </div>
  </header>
  {#if source}
    {#if administrator}
      <div class="source-use-row">
        <div class="source-use-copy"><strong>Everyone</strong><small>The default for every listener.</small></div>
        <SourceUseSwitch label="Use this source for everyone" value={source.householdMode} disabled={busy} onchange={mode => void save(mode, "household")} />
      </div>
    {/if}
    <div class="source-use-row">
      <div class="source-use-copy">
        <strong>{administrator ? "Just you" : "For you"}</strong>
        {#if !source.canChoosePersonal}
          <small>An administrator turned this source off for everyone.</small>
        {:else if source.personalMode === null}
          <small>Follows {administrator ? "the setting for everyone" : `the household setting (${names[source.householdMode]})`}.</small>
        {:else}
          <small>Your own setting. <button type="button" class="source-use-reset" disabled={busy} onclick={() => void save(null, "personal")}>Follow {administrator ? "everyone" : "the household"} instead</button></small>
        {/if}
      </div>
      <SourceUseSwitch label="Use this source for you" value={source.canChoosePersonal ? source.effectiveMode : "off"} disabled={busy || !source.canChoosePersonal} onchange={mode => void save(mode, "personal")} />
    </div>
  {:else if failed}
    <div class="source-use-row">
      <div class="source-use-copy"><small role="alert">This setting could not be loaded.</small></div>
      <Button variant="secondary" disabled={busy} onclick={() => void reload()}>Try again</Button>
    </div>
  {:else}
    <div class="source-use-row" aria-busy="true">
      <div class="source-use-copy"><small role="status">Loading…</small></div>
      <span class="source-use-placeholder" aria-hidden="true"></span>
    </div>
  {/if}
</section>
{/if}

<style>
  .source-use-row {
    display: flex;
    min-width: 0;
    align-items: center;
    justify-content: space-between;
    gap: var(--space-3) var(--space-6);
    padding: var(--space-3) var(--space-6);
  }

  .source-use-row:last-child {
    padding-bottom: var(--space-5);
  }

  .source-use-row + .source-use-row {
    position: relative;
    padding-top: var(--space-4);
  }

  .source-use-row + .source-use-row::before {
    position: absolute;
    top: 0;
    right: var(--space-6);
    left: var(--space-6);
    border-top: 1px solid var(--color-edge);
    content: "";
  }

  .source-use-copy {
    display: grid;
    min-width: 0;
    gap: 0.125rem;
  }

  .source-use-copy strong {
    color: var(--color-ink);
    font-size: var(--text-sm);
    font-weight: 600;
    line-height: 1.25rem;
  }

  .source-use-copy small {
    color: var(--color-ink-muted);
    font-size: 0.8125rem;
    line-height: 1.125rem;
  }

  .source-use-reset {
    display: inline;
    min-width: 0;
    min-height: 0;
    border: 0;
    background: none;
    padding: 0;
    color: var(--color-signal-text);
    font: inherit;
    font-weight: 600;
    cursor: pointer;
  }

  .source-use-reset:hover {
    text-decoration: underline;
  }

  .source-use-reset:focus-visible {
    border-radius: 2px;
    outline: 2px solid var(--color-ring);
    outline-offset: 2px;
  }

  .source-use-placeholder {
    width: 23rem;
    max-width: 100%;
    height: var(--control-md);
    border-radius: 999px;
    background: var(--color-surface-raised);
  }

  @media (max-width: 40rem) {
    .source-use-row {
      flex-direction: column;
      align-items: stretch;
    }

    .source-use-placeholder {
      width: 100%;
    }
  }
</style>
