<script lang="ts">
  import { AlertDialog, Dialog } from "bits-ui";
  import { sources, type ProviderAccount } from "$lib/api";
  import { audienceLabel } from "$lib/sources";

  let {
    open = $bindable(false),
    account,
    onSaved,
  }: {
    open: boolean;
    account: ProviderAccount | null;
    onSaved: (message: string) => void | Promise<void>;
  } = $props();

  let preparedId = $state("");
  let scope = $state<ProviderAccount["scope"]>("User");
  let libraryScopeId = $state("");
  let confirmOpen = $state(false);
  let saving = $state(false);
  let error = $state("");

  $effect(() => {
    if (!open || !account || preparedId === account.id) return;
    preparedId = account.id;
    scope = account.scope;
    libraryScopeId = account.libraryScopeId ?? "";
    error = "";
  });

  function submit() {
    if (!account || saving) return;
    if (account.scope !== "Global" && scope === "Global") {
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
      await sources.setAudience(account, scope, scope === "Library" ? libraryScopeId : null);
      confirmOpen = false;
      open = false;
      await onSaved(`Access changed to ${scope === "Global" ? "Everyone" : scope === "Library" ? `Library ${libraryScopeId}` : "Only me"}.`);
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
          <Dialog.Close class="icon-button" aria-label="Close access editor">×</Dialog.Close>
        </header>
        <form onsubmit={(event) => { event.preventDefault(); submit(); }}>
          <fieldset class="audience-options">
            <legend>Who can use this source connection?</legend>
            <label class:active={scope === "User"}>
              <input bind:group={scope} type="radio" value="User" />
              <span><strong>Only me</strong><small>Only your linked Allstarr user can route through this account.</small></span>
            </label>
            <label class:active={scope === "Global"}>
              <input bind:group={scope} type="radio" value="Global" />
              <span><strong>Everyone</strong><small>Every user may route supported Source capabilities through this account.</small></span>
            </label>
            <label class:active={scope === "Library"}>
              <input bind:group={scope} type="radio" value="Library" />
              <span><strong>One library</strong><small>Only requests in the named backend library may use this account.</small></span>
            </label>
          </fieldset>
          {#if scope === "Library"}
            <label class="field"><span>Library ID</span><input bind:value={libraryScopeId} required /></label>
          {/if}
          <p class="credential-safety">Credentials stay encrypted and are never shown when access changes.</p>
          {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
          <footer>
            <Dialog.Close class="button-secondary">Cancel</Dialog.Close>
            <button class="button-primary" type="submit" disabled={saving}>{saving ? "Saving…" : "Save access"}</button>
          </footer>
        </form>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<AlertDialog.Root bind:open={confirmOpen}>
  <AlertDialog.Portal>
    <AlertDialog.Overlay class="dialog-overlay" />
    <AlertDialog.Content class="confirm-dialog">
      <AlertDialog.Title>Share this connection with everyone?</AlertDialog.Title>
      <AlertDialog.Description>
        Every Allstarr user will be allowed to use this account for its supported Source capabilities. Credentials remain hidden.
      </AlertDialog.Description>
      <footer>
        <AlertDialog.Cancel class="button-secondary">Keep current access</AlertDialog.Cancel>
        <AlertDialog.Action class="button-danger" onclick={() => void save()}>Share with everyone</AlertDialog.Action>
      </footer>
    </AlertDialog.Content>
  </AlertDialog.Portal>
</AlertDialog.Root>
