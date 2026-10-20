<script lang="ts">
  import { providerUse, type ProviderUseMode, type ProviderUseSettings } from "$lib/api";
  import SelectField from "$lib/components/SelectField.svelte";
  import { Button } from "$lib/components/ui/button";

  let { providerId, administrator, onSaved }: { providerId: string; administrator: boolean; onSaved: () => void | Promise<void> } = $props();
  let data = $state<ProviderUseSettings | null>(null);
  let busy = $state(false), error = $state(""), saved = $state(false);
  const source = $derived(data?.providers?.find(item => item.providerId === providerId));
  const options = $derived([
    ...(source?.supportsAutomaticMatching ? [{ value: "automatic", label: "Automatic" }] : []),
    { value: "manualOnly", label: "Manual only" }, { value: "off", label: "Off" },
  ]);
  $effect(() => {
    const id = providerId;
    let current = true;
    data = null; error = ""; saved = false;
    providerUse.get().then(result => { if (current && providerId === id) data = result; })
      .catch(() => { if (current) error = "Source use preferences could not be loaded."; });
    return () => { current = false; };
  });
  async function save(mode: string | null, scope: "personal" | "household") {
    if (!data || busy) return;
    busy = true; error = ""; saved = false;
    try {
      data = await providerUse.save(providerId, mode as ProviderUseMode | null, scope,
        scope === "household" ? data.householdRevision : data.personalRevision);
      saved = true;
      await onSaved();
    } catch (cause) { error = cause instanceof Error ? cause.message : "Source use preferences could not be saved."; }
    finally { busy = false; }
  }
  async function reload() {
    busy = true; error = "";
    try { data = await providerUse.get(); }
    catch { error = "Source use preferences could not be loaded."; }
    finally { busy = false; }
  }
</script>

{#if source || !data || error}
<section class="settings-card p-5 space-y-4" aria-label="Source use">
  <h3 class="font-semibold">Use source</h3>
  <p>Manual only keeps this source out of automatic matching, identity linking and fallback. Search it explicitly to choose an exact song.</p>
  {#if source}
    <div class="field"><span>For you</span>
      <SelectField label="Use source for you" value={source.mode} {options} disabled={busy || source.householdDisabled} onchange={value => void save(value, "personal")} />
      <Button variant="ghost" disabled={busy} onclick={() => void save(null, "personal")}>Use household default</Button>
    </div>
    {#if administrator}
      <div class="field"><span>Household default</span>
        <SelectField label="Household source use" value={source.householdMode} {options} disabled={busy} onchange={value => void save(value, "household")} />
      </div>
    {/if}
    {#if !source.supportsAutomaticMatching}<p>This source supplies manual selections only.</p>{/if}
    {#if source.householdDisabled}<p>An administrator has disabled this source for the household.</p>{/if}
    {#if saved}<p role="status">Source use saved.</p>{/if}
  {:else if !error}<p role="status">Loading source use…</p>{/if}
  {#if error}<p class="notice-error" role="alert">{error}</p><Button variant="secondary" disabled={busy} onclick={() => void reload()}>Reload source use</Button>{/if}
</section>
{/if}
