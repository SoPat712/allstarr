<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { X } from "@lucide/svelte";
  import { sources, type ProviderAccount } from "$lib/api";
  import { audienceLabel } from "$lib/sources";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import SelectField from "$lib/components/SelectField.svelte";

  let {
    open = $bindable(false),
    account,
    users,
    onSaved,
  }: {
    open: boolean;
    account: ProviderAccount | null;
    users: { id: string; displayName: string }[];
    onSaved: (message: string) => void | Promise<void>;
  } = $props();

  let preparedRevision = $state("");
  let scope = $state<"Personal" | "Shared">("Personal");
  let ownerUserId = $state("");
  let confirmOpen = $state(false);
  let saving = $state(false);
  let error = $state("");
  const expansion = $derived(account && scope === "Shared" && account.scope !== "Shared");

  $effect(() => {
    if (!open) {
      preparedRevision = "";
      confirmOpen = false;
      return;
    }
    const revision = account ? `${account.id}:${account.revision}` : "";
    if (!open || !account || preparedRevision === revision) return;
    preparedRevision = revision;
    scope = account.scope;
    ownerUserId = account.ownerUserId ?? users[0]?.id ?? "";
    error = "";
  });

  function submit() {
    if (!account || saving) return;
    if (expansion) {
      confirmOpen = true;
      return;
    }
    void save();
  }

  async function save() {
    if (!account || saving) return;
    saving = true;
    error = "";
    try {
      await sources.setAudience(account, scope, scope === "Personal" ? ownerUserId : null);
      confirmOpen = false;
      open = false;
      const selected = users.find((user) => user.id === ownerUserId);
      await onSaved(`Access changed to ${scope === "Shared" ? "Shared" : selected?.displayName ?? "Personal"}.`);
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Access could not be updated.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog access-dialog">
      {#if account}
        <header>
          <div>
            <p class="eyebrow">Manage access</p>
            <Dialog.Title>{account.sourceDisplayName || account.displayName}</Dialog.Title>
            <Dialog.Description>Current audience: {audienceLabel(account)}</Dialog.Description>
          </div>
          <Dialog.Close class="icon-button" aria-label="Close access editor"><X size={18} aria-hidden="true" /></Dialog.Close>
        </header>
        <form onsubmit={(event) => { event.preventDefault(); submit(); }}>
          <fieldset class="audience-options">
            <legend>Who can use this source connection?</legend>
            <label class:active={scope === "Personal"}>
              <input bind:group={scope} type="radio" value="Personal" />
              <span><strong>Personal</strong><small>Only the selected Allstarr user can use this account.</small></span>
            </label>
            <label class:active={scope === "Shared"}>
              <input bind:group={scope} type="radio" value="Shared" />
              <span><strong>Shared</strong><small>Household users may use eligible provider capabilities. Administrators manage this account.</small></span>
            </label>
          </fieldset>
          {#if scope === "Personal"}
            <label class="field"><span>Allstarr user</span><SelectField bind:value={ownerUserId} label="Allstarr user" options={users.map((user) => ({ value: user.id, label: user.displayName }))} required /></label>
          {/if}
          <p class="credential-safety">Credentials stay encrypted and hidden. Household use may consume provider limits and use this account’s supported capabilities.</p>
          {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
          <footer class="dialog-actions">
            <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
            <Button type="submit" disabled={saving}>{saving ? "Saving…" : "Save access"}</Button>
          </footer>
        </form>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<ConfirmDialog
  bind:open={confirmOpen}
  title="Share this connection with household users?"
  description="Household users will be allowed to use this account for its supported Source capabilities. Credentials remain hidden."
  confirmLabel="Share with household"
  cancelLabel="Keep current access"
  disabled={saving}
  onConfirm={save}
/>
