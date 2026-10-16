<script lang="ts">
  import { tick } from "svelte";
  import { onboarding, type OnboardingState } from "$lib/api";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import EnvMigrationCard from "$lib/components/EnvMigrationCard.svelte";
  import AccessSettingsCard from "$lib/components/AccessSettingsCard.svelte";
  import StorageVerificationCard from "$lib/components/StorageVerificationCard.svelte";
  let { state: guideState, onChanged }: { state: OnboardingState; onChanged: (value: OnboardingState) => void } = $props();
  let selected = $state(""), busy = $state(false), error = $state("");
  let heading: HTMLHeadingElement | undefined = $state();
  const steps = $derived(guideState.steps ?? []);
  const current = $derived(steps.find((step) => step.id === selected) ?? steps.find((step) => step.state === "pending") ?? steps[0]);
  async function save(action: string) {
    if (!current || busy) return;
    busy = true; error = "";
    try {
      const next = await onboarding.step(current.id, action, guideState.revision);
      onChanged(next);
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
<section class="setup-guide" aria-labelledby="setup-title">
  <header class="panel p-5 space-y-3">
    <h2 id="setup-title" class="text-xl font-semibold">Set up Allstarr</h2>
    <p>Your progress is saved for this account across browsers. Review each change at your pace. Native media-server playback continues while setup is pending.</p>
    <p class="text-ink-muted">Previous files and environment settings are read-only. Valkey-only favorites and manual fixes cannot be imported. SquidWTF is retired; you choose its replacement.</p>
    <p role="status">{steps.filter((step) => step.state !== "pending").length} of {steps.length} steps reviewed or skipped</p>
  </header>
  <div class="setup-layout">
    <nav class="panel setup-steps" aria-label="Setup steps">
      {#each steps as step, index}
        <button type="button" class:active={current?.id === step.id} aria-current={current?.id === step.id ? "step" : undefined} onclick={() => selected = step.id}>
          <span>{index + 1}. {step.title}</span><small>{step.state === "reviewed" ? "Reviewed" : step.state === "skipped" ? "Skipped" : step.required ? "Required" : "Optional"}</small>
        </button>
      {/each}
    </nav>
    <div class="setup-content space-y-4">
      {#if current}
        <article class="settings-card p-5 space-y-4">
          <Badge state={current.state === "reviewed" ? "healthy" : "suggested"}>{current.state === "pending" ? current.required ? "Required" : "Optional" : current.state}</Badge>
          <h3 bind:this={heading} tabindex="-1" class="text-lg font-semibold">{current.title}</h3><p>{current.explanation}</p>
          {#if current.href && current.id !== "legacy-environment"}<a class="button secondary" href={current.href}>Open {current.title.toLowerCase()}</a>{/if}
          <div class="flex flex-wrap gap-2">
            <Button disabled={busy} onclick={() => void save("review")}>Reviewed, continue</Button>
            {#if !current.required}<Button variant="secondary" disabled={busy} onclick={() => void save("skip")}>Skip for now</Button>{/if}
            {#if current.state !== "pending" && !current.required}<Button variant="ghost" disabled={busy} onclick={() => void save("reset")}>Review again later</Button>{/if}
          </div>
        </article>
        {#if current.id === "legacy-environment"}<EnvMigrationCard />
        {:else if current.id === "backend-identity"}<AccessSettingsCard kind="backend" />
        {:else if current.id === "original-media"}<StorageVerificationCard />{/if}
      {/if}
      {#if error}<p role="alert" class="notice-error">{error}</p><Button variant="secondary" onclick={async () => { try { onChanged(await onboarding.status()); error = ""; } catch { error = "Setup is unavailable. Reload and retry."; } }}>Reload guide</Button>{/if}
      <article class="settings-card p-5 space-y-4">
        <p>Finish for now marks remaining optional steps as skipped. You can reopen this guide from Settings → Setup without resetting your choices.</p>
        <div class="flex flex-wrap gap-2"><Button disabled={busy} onclick={() => void finish()}>Finish setup</Button><a class="button secondary" href="#/">Return to Home</a></div>
      </article>
    </div>
  </div>
</section>
<style>
  .setup-guide { display: grid; gap: var(--section-gap); min-width: 0; }
  .setup-layout { display: grid; grid-template-columns: minmax(200px, 1fr) minmax(0, 3fr); gap: var(--section-gap); align-items: start; }
  .setup-steps { display: grid; padding: 8px; }
  .setup-steps button { text-align: left; padding: 12px; border-radius: 12px; min-height: 56px; display: grid; gap: 4px; overflow-wrap: anywhere; }
  .setup-steps button.active { background: var(--md-sys-color-secondary-container); color: var(--md-sys-color-on-secondary-container); }
  .setup-steps small { color: var(--md-sys-color-on-surface-variant); }
  .setup-content { min-width: 0; }
  @media (max-width: 900px) { .setup-layout { grid-template-columns: minmax(0, 1fr); } .setup-steps { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
