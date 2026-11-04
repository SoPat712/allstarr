<script lang="ts">
  import { tick } from "svelte";
  import { toast } from "$lib/toasts";
  import { onboarding, type OnboardingState } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import EnvMigrationCard from "$lib/components/EnvMigrationCard.svelte";
  import StorageVerificationCard from "$lib/components/StorageVerificationCard.svelte";
  let { state: guideState, onChanged, embedded = false }: { state: OnboardingState; onChanged: (value: OnboardingState) => void; embedded?: boolean } = $props();
  let selected = $state(""), busy = $state(false), error = $state("");
  let showAll = $state(false);
  let heading: HTMLHeadingElement | undefined = $state();
  const steps = $derived(guideState.steps ?? []);
  const current = $derived(steps.find((step) => step.id === selected) ?? steps.find((step) => step.state === "pending") ?? steps[0]);
  async function save(action: string) {
    if (!current || busy) return;
    const previous = current;
    busy = true; error = "";
    try {
      const next = await onboarding.step(current.id, action, guideState.revision);
      onChanged(next);
      toast.success("Setup progress saved.", { label: "Undo", run: async () => {
        try { onChanged(await onboarding.step(previous.id, previous.state === "pending" ? "reset" : previous.state === "skipped" ? "skip" : "review", guideState.revision)); selected = previous.id; }
        catch (cause) { toast.error(cause instanceof Error ? cause.message : "Progress could not be restored."); }
      } });
      selected = next.steps?.find((step) => step.state === "pending")?.id ?? current.id;
      await tick();
      heading?.focus();
    } catch (cause) { error = cause instanceof Error ? cause.message : "This step could not be saved. Reload the guide and retry."; }
    finally { busy = false; }
  }
  async function finish() {
    busy = true; error = "";
    try { onChanged(await onboarding.complete()); window.location.hash = "#/"; }
    catch (cause) { error = cause instanceof Error ? cause.message : "Setup could not be completed."; }
    finally { busy = false; }
  }
</script>
<section class="setup-guide" aria-labelledby="setup-title" class:embedded>
  {#if !embedded}<header><h2 id="setup-title">Set up Allstarr</h2><p class="settings-muted">Your progress is saved. You can leave and come back.</p>{#await import("./patterns/PageStatus.svelte") then { default: PageStatus }}<PageStatus items={[{ label: `${steps.filter(step => step.state !== "pending").length} of ${steps.length} steps reviewed or skipped` }]} />{/await}</header>{:else}<h3 id="setup-title" class="sr-only">Set up Allstarr</h3>{/if}
  {#if embedded && !selected}
    <ol class="setup-overview settings-card" aria-label="Setup steps">
      {#each (showAll ? steps : steps.slice(0, 7)) as step, index (step.id)}
        <li class:current={current?.id === step.id}><span class="step-number">{step.state === "reviewed" ? "✓" : index + 1}</span><div><button type="button" class="step-title" onclick={() => selected = step.id}>{step.title}</button>{#if current?.id === step.id}<small>{step.explanation}</small>{/if}</div>
          {#if current?.id === step.id}<Button size="sm" onclick={() => selected = step.id}>Open</Button>{:else if step.state !== "pending"}<Badge tone={step.state === "reviewed" ? "success" : "neutral"}>{step.state === "reviewed" ? "Reviewed" : "Skipped"}</Badge>{:else}<small>{step.required ? "Required" : "Optional"}</small>{/if}
        </li>
      {/each}
      {#if steps.length > 7}<li class="more-steps"><Button variant="text" onclick={() => showAll = !showAll}>{showAll ? "Show fewer steps" : `Show ${steps.length - 7} more steps`}</Button></li>{/if}
    </ol>
  {:else}
    {#if embedded}<Button variant="text" onclick={() => selected = ""}>Back to all steps</Button>{/if}
    <div class="setup-layout">
      <nav class="settings-card setup-steps" aria-label="Setup steps">
        {#each steps as step, index (step.id)}<button type="button" class:active={current?.id === step.id} aria-current={current?.id === step.id ? "step" : undefined} onclick={() => selected = step.id}><span class="step-number">{step.state === "reviewed" ? "✓" : index + 1}</span><span>{step.title}<small>{step.state === "reviewed" ? "Reviewed" : step.state === "skipped" ? "Skipped" : step.required ? "Required" : "Optional"}</small></span></button>{/each}
      </nav>
      <div class="setup-content settings-stack">
        {#if current}
          <header><p class="step-count">Step {steps.findIndex(step => step.id === current.id) + 1} of {steps.length}</p><h3 bind:this={heading} tabindex="-1">{current.title}</h3><p class="settings-muted">{current.id === "backend-identity" ? "Your apps reach your media server through Allstarr. Nothing in your library is moved." : current.explanation}</p></header>
          {#if current.id === "legacy-environment"}<EnvMigrationCard />
          {:else if current.id === "backend-identity"}
            {#await import("./SetupMediaServerStep.svelte") then { default: MediaServer }}<MediaServer />{/await}
          {:else if current.id === "original-media"}<StorageVerificationCard />
          {:else if current.href}<Button variant="outline" href={current.href}>Open {current.title.toLowerCase()}</Button>{/if}
          <div class="step-actions"><span class="settings-muted">Progress saves for your account.</span><div>{#if !current.required}<Button variant="text" disabled={busy} onclick={() => save("skip")}>Skip for now</Button>{/if}<Button disabled={busy} onclick={() => save("review")}>Reviewed, continue</Button>{#if current.state !== "pending" && !current.required}<Button variant="text" disabled={busy} onclick={() => save("reset")}>Review again later</Button>{/if}</div></div>
        {/if}
      </div>
    </div>
  {/if}
  {#if error}{#await import("./patterns/ErrorState.svelte") then { default: ErrorState }}<ErrorState title="This step could not be saved" detail={error} onretry={async () => { try { onChanged(await onboarding.status()); error = ""; } catch { error = "Setup is unavailable. Try again."; } }} />{/await}{/if}
  <footer class="setup-finish"><p>Finishing marks the optional steps as skipped. You can come back any time.</p><Button variant="outline" disabled={busy} onclick={finish}>Finish setup</Button>{#if !embedded}<Button variant="text" href="#/">Return to Home</Button>{/if}</footer>
</section>
<style>
  .setup-guide { display: grid; min-width: 0; gap: var(--space-4); }
  .setup-guide > header { display: grid; gap: var(--space-3); }
  .setup-guide h2, .setup-content h3 { margin: 0; color: var(--color-ink); font-size: var(--text-xl); font-weight: 600; }
  .setup-overview { margin: 0; padding: 0; list-style: none; }
  .setup-overview li { display: flex; min-width: 0; align-items: center; gap: var(--space-3); padding: var(--space-2) var(--space-4); border-bottom: 1px solid var(--color-edge); }
  .setup-overview li:last-child { border-bottom: 0; }
  .setup-overview li.current { background: var(--color-panel-raised); }
  .setup-overview li > div { flex: 1; min-width: 0; }
  .setup-overview small { display: block; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .step-title { min-height: 44px; border: 0; padding: 0; background: transparent; color: var(--color-ink); font-size: var(--text-sm); font-weight: 500; text-align: left; cursor: pointer; }
  .step-number { display: grid; width: 28px; height: 28px; flex: 0 0 auto; place-items: center; border-radius: var(--radius-sm); background: var(--color-panel-raised); color: var(--color-ink-muted); font-size: var(--text-xs); }
  .setup-layout { display: grid; grid-template-columns: minmax(170px, 1fr) minmax(0, 3fr); gap: var(--space-5); align-items: start; }
  .setup-steps { padding: var(--space-2); }
  .setup-steps button { display: flex; align-items: center; width: 100%; min-height: 48px; gap: var(--space-2); border: 0; padding: var(--space-2); border-radius: var(--radius-md); text-align: left; background: transparent; color: var(--color-ink); font-size: var(--text-sm); cursor: pointer; }
  .setup-steps button.active { background: var(--color-secondary); color: var(--color-secondary-foreground); }
  .setup-steps small { display: block; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .setup-content { min-width: 0; }
  .setup-content header { display: grid; gap: var(--space-2); }
  .setup-content p { margin: 0; font-size: var(--text-sm); line-height: 1.5; }
  .step-count { color: var(--color-ink-muted); font-size: var(--text-xs) !important; }
  .step-actions, .setup-finish { display: flex; flex-wrap: wrap; gap: var(--space-3); align-items: center; justify-content: space-between; }
  .step-actions > div { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .step-actions > span, .setup-finish p { flex: 1; margin: 0; color: var(--color-ink-muted); font-size: var(--text-xs); }
  @media (max-width: 900px) { .setup-layout { grid-template-columns: minmax(0, 1fr); } .setup-steps { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
