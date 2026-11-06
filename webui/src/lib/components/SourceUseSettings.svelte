<script lang="ts">
  import { providerUse, type ProviderUseMode, type ProviderUseSettings } from "$lib/api";
  import ThreeDetentSwitch from "$lib/components/ThreeDetentSwitch.svelte";
  import SourceUseSwitch from "$lib/components/SourceUseSwitch.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import { toast } from "$lib/toasts";

  let { providerId, administrator, onSaved, onUndo }: {
    providerId: string; administrator: boolean; onSaved: () => void | Promise<void>;
    onUndo: (providerId: string, restore: () => Promise<void>) => void;
  } = $props();
  let data = $state<ProviderUseSettings | null>(null), busy = $state(false), failed = $state(false);
  let generation = 0;
  const source = $derived(data?.providers?.find(item => item.providerId === providerId));
  const modeLabel = (mode: ProviderUseMode) => mode === "manualOnly" ? "Manual only" : mode === "off" ? "Off" : "On";
  const personalOptions = [{ value: "follow", label: "Follow" }, { value: "automatic", label: "On" }, { value: "manualOnly", label: "Manual only" }, { value: "off", label: "Off" }] as const;
  $effect(() => {
    const id = providerId;
    let current = true;
    generation++; data = null; failed = false;
    providerUse.get().then(result => { if (current && id === providerId) data = result; }).catch(() => { if (current) failed = true; });
    return () => { current = false; };
  });
  async function reload() {
    failed = false;
    try { data = await providerUse.get(); } catch { failed = true; }
  }
  async function save(mode: ProviderUseMode | null, scope: "personal" | "household") {
    if (!data || !source || busy) return;
    const id = providerId, current = generation;
    const previous = scope === "household" ? source.householdMode : source.personalMode;
    busy = true;
    try {
      const result = await providerUse.save(id, mode, scope, scope === "household" ? data.householdRevision : data.personalRevision);
      if (id === providerId && current === generation) data = result;
      onUndo(id, async () => {
        const latest = await providerUse.get();
        const restored = await providerUse.save(id, previous, scope, scope === "household" ? latest.householdRevision : latest.personalRevision);
        if (id === providerId) data = restored;
        await onSaved();
      });
      await onSaved();
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "The source setting could not be saved.");
      if (id === providerId) await reload();
    } finally { busy = false; }
  }
</script>

<section class="source-use-settings" aria-labelledby="source-use-title" aria-busy={busy}>
  <h3 id="source-use-title">Use this source</h3>
  {#if source}
    {#if administrator}
      <div class="use-setting"><div><strong>Everyone</strong><small>The default for every listener</small></div><SourceUseSwitch label="Use this source for everyone" value={source.householdMode} disabled={busy} onchange={mode => void save(mode, "household")} /></div>
    {/if}
    <div class="use-setting"><div><strong>Just you</strong><small>{!source.canChoosePersonal ? "An administrator turned this source off for everyone." : source.personalMode === null ? `Follows the setting for everyone. Current setting: ${modeLabel(source.householdMode)}.` : source.personalMode === "off" ? "Off: not used or shown anywhere." : "Your own setting."}</small></div>
      <ThreeDetentSwitch label="Use this source for you" options={personalOptions} value={source.personalMode ?? "follow"} disabled={busy || !source.canChoosePersonal} onchange={mode => void save(mode === "follow" ? null : mode, "personal")} />
    </div>
  {:else if failed}<ErrorState title="This setting could not be loaded" onretry={reload} />
  {:else if !data}<RowsSkeleton count={1} />{/if}
</section>

<style>
  .source-use-settings { display: grid; gap: var(--space-3); }
  h3 { margin: 0; color: var(--color-ink-muted); font-size: var(--text-xs); font-weight: 600; }
  .use-setting { display: grid; min-width: 0; gap: var(--space-2); border-bottom: 1px solid var(--color-edge); padding-bottom: var(--space-3); }
  .use-setting:last-child { border: 0; padding-bottom: 0; }
  strong, small { display: block; } strong { font-size: var(--text-sm); } small { margin-top: var(--space-1); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .use-setting :global(.use-switch) { width: 100%; }
</style>
