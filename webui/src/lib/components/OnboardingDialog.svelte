<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { X } from "lucide-svelte";
  import type { OnboardingState } from "$lib/api";

  let {
    open = $bindable(false),
    state: onboardingState,
    onComplete,
    onChanged,
  }: {
    open: boolean;
    state: OnboardingState;
    onComplete: () => Promise<OnboardingState>;
    onChanged: (state: OnboardingState) => void;
  } = $props();

  let saving = $state(false);
  let error = $state("");
  const backendRecorded = $derived(onboardingState.completedSteps.includes("backend-identity"));

  function preventForcedClose(event: Event) {
    if (onboardingState.shouldRedirectToSetup) event.preventDefault();
  }

  async function complete() {
    if (saving) return;
    saving = true;
    error = "";
    try {
      const next = await onComplete();
      onChanged(next);
      open = false;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Setup could not be completed.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content
      class="source-dialog onboarding-dialog"
      onEscapeKeydown={preventForcedClose}
      onInteractOutside={preventForcedClose}
    >
      <header class="dialog-heading">
        <div>
          <p class="eyebrow">Durable onboarding</p>
          <Dialog.Title>Set up Allstarr</Dialog.Title>
          <Dialog.Description>Your progress is saved to PostgreSQL for this account, not this browser.</Dialog.Description>
        </div>
        {#if !onboardingState.shouldRedirectToSetup}<Dialog.Close class="icon-button" aria-label="Close setup guide"><X size={18} aria-hidden="true" /></Dialog.Close>{/if}
      </header>

      <div class="setup-checklist">
        <section>
          <span class={`status-pill ${backendRecorded ? "healthy" : "suggested"}`}>{backendRecorded ? "Recorded" : "Required"}</span>
          <div><strong>Media server identity</strong><p>Your authenticated Jellyfin or Subsonic identity anchors account and library ownership.</p></div>
        </section>
        <section>
          <span class={`status-pill ${onboardingState.migration.completed ? "healthy" : "suggested"}`}>{onboardingState.migration.completed ? "Imported" : "Optional"}</span>
          <div><strong>Legacy v2 settings</strong><p>Preview a prior <code>.env</code> from Settings → Maintenance when you need it.</p></div>
        </section>
        <section>
          <span class="status-pill suggested">Next</span>
          <div><strong>Connect Sources</strong><p>Add provider accounts only when their capabilities are useful to you.</p></div>
        </section>
        <section>
          <span class="status-pill suggested">Next</span>
          <div><strong>Add a playlist</strong><p>Allstarr will prefer playable local tracks and retain configured provider fallbacks.</p></div>
        </section>
      </div>

      {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
      <footer class="dialog-actions">
        {#if !onboardingState.shouldRedirectToSetup}<Dialog.Close class="button-secondary">Close</Dialog.Close>{/if}
        <button class="button-primary" type="button" disabled={saving} onclick={() => void complete()}>{saving ? "Saving…" : "Finish setup"}</button>
      </footer>
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
