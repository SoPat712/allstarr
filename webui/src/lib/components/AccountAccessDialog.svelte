<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { X } from "lucide-svelte";
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
  let audience = $state<"owner" | "user" | "global" | "library">("owner");
  let ownerUserId = $state("");
  let libraryScopeId = $state("");
  let confirmOpen = $state(false);
  let saving = $state(false);
  let error = $state("");
  const expansion = $derived(
    account && audience === "global" && account.scope !== "Global"
      ? "everyone"
      : account && audience === "library" &&
          account.scope !== "Library" && account.scope !== "Global"
        ? "library"
        : "",
  );

  $effect(() => {
    const revision = account ? `${account.id}:${account.revision}` : "";
    if (!open || !account || preparedRevision === revision) return;
    preparedRevision = revision;
    audience = account.scope === "Global"
      ? "global"
      : account.scope === "Library"
        ? "library"
        : account.ownerUserId && account.createdByUserId &&
            account.ownerUserId !== account.createdByUserId
          ? "user"
          : "owner";
    ownerUserId = account.ownerUserId ?? account.createdByUserId ?? users[0]?.id ?? "";
    libraryScopeId = account.libraryScopeId ?? "";
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
      const scope = audience === "global" ? "Global" : audience === "library" ? "Library" : "User";
      const owner = audience === "owner"
        ? account.createdByUserId ?? account.ownerUserId
        : audience === "user"
          ? ownerUserId
          : null;
      await sources.setAudience(account, scope, owner, scope === "Library" ? libraryScopeId : null);
      confirmOpen = false;
      open = false;
      const selected = users.find((user) => user.id === owner);
      await onSaved(`Access changed to ${scope === "Global" ? "Everyone" : scope === "Library" ? `Library ${libraryScopeId}` : selected?.displayName ?? account.creatorDisplayName ?? "the owner"}.`);
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
            <label class:active={audience === "owner"}>
              <input bind:group={audience} type="radio" value="owner" />
              <span><strong>Connection owner</strong><small>{account.creatorDisplayName || account.ownerDisplayName || "The user who connected it"} can use this account.</small></span>
            </label>
            <label class:active={audience === "user"}>
              <input bind:group={audience} type="radio" value="user" disabled={!users.length} />
              <span><strong>One user</strong><small>{users.length ? "Choose one active Allstarr user without exposing credentials." : "No active users are available."}</small></span>
            </label>
            <label class:active={audience === "global"}>
              <input bind:group={audience} type="radio" value="global" />
              <span><strong>Everyone</strong><small>Every user may route supported Source capabilities through this account.</small></span>
            </label>
            <label class:active={audience === "library"}>
              <input bind:group={audience} type="radio" value="library" />
              <span><strong>One library</strong><small>Only requests in the selected media library may use this account.</small></span>
            </label>
          </fieldset>
          {#if audience === "user"}
            <label class="field"><span>Allstarr user</span><SelectField bind:value={ownerUserId} label="Allstarr user" options={users.map((user) => ({ value: user.id, label: user.displayName }))} required /></label>
          {:else if audience === "library"}
            <label class="field"><span>Library ID</span><input bind:value={libraryScopeId} required /></label>
          {/if}
          <p class="credential-safety">Credentials stay encrypted and are never shown when access changes.</p>
          {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
          <footer>
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
  title={`Share this connection with ${expansion === "library" ? "a library" : "everyone"}?`}
  description={`${expansion === "library"
    ? `Every user with access to library ${libraryScopeId} may use this account's supported Source capabilities.`
    : "Every Allstarr user will be allowed to use this account for its supported Source capabilities."} Credentials remain hidden.`}
  confirmLabel={`Share with ${expansion === "library" ? "library" : "everyone"}`}
  cancelLabel="Keep current access"
  disabled={saving}
  onConfirm={save}
/>
