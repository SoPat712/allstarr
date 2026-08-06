<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { X } from "lucide-svelte";
  import { sources, type ProviderAccount, type ProviderDefinition } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { accountSettings, secretFromForm, settingDefault } from "$lib/sources";

  let {
    open = $bindable(false),
    providers,
    administrator,
    account = null,
    onSaved,
  }: {
    open: boolean;
    providers: ProviderDefinition[];
    administrator: boolean;
    account?: ProviderAccount | null;
    onSaved: (message: string) => void | Promise<void>;
  } = $props();

  let providerId = $state("");
  let saving = $state(false);
  let error = $state("");

  const choices = $derived(providers.filter((provider) => accountSettings(provider).length));
  const selected = $derived(
    choices.find((provider) => provider.id === (account?.providerId ?? providerId)) ?? choices[0],
  );

  $effect(() => {
    if (open && !providerId && choices[0]) providerId = account?.providerId ?? choices[0].id;
    if (!open) error = "";
  });

  async function create(event: SubmitEvent) {
    event.preventDefault();
    if (!selected || saving) return;
    saving = true;
    error = "";
    const form = event.currentTarget as HTMLFormElement;
    const data = new FormData(form);
    try {
      const saved = account ?? await sources.create({
          providerId: selected.id,
          displayName: String(data.get("displayName") || "").trim() || `My ${selected.name} connection`,
          scope: String(data.get("scope") || "User"),
          libraryScopeId: String(data.get("libraryScopeId") || "").trim() || null,
          enabled: true,
          secret: secretFromForm(selected, data),
        });
      if (account) await sources.replaceSecret(account, secretFromForm(selected, data));
      if (selected.id === "lastfm")
        await sources.authenticateLastFm(
          saved.id,
          String(data.get("username") || ""),
          String(data.get("password") || ""),
        );
      try {
        await sources.test(saved);
        await onSaved(`${selected.name} ${account ? "configuration saved" : "connected"} and tested.`);
      } catch {
        await onSaved(`${selected.name} ${account ? "configuration saved" : "connected"}. Its connection test needs attention.`);
      }
      form.reset();
      open = false;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The Source could not be connected.";
    } finally {
      saving = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Portal>
    <Dialog.Overlay class="dialog-overlay" />
    <Dialog.Content class="source-dialog connect-source-dialog">
      <header>
        <div>
          <p class="eyebrow">{account ? "Source setup" : "Source connection"}</p>
          <Dialog.Title>{account ? `Configure ${selected?.name ?? "Source"}` : "Connect a Source"}</Dialog.Title>
          <Dialog.Description>{account
            ? "Replace the encrypted account details. Existing credentials are never displayed."
            : "Credentials are encrypted before this connection is enabled."}</Dialog.Description>
        </div>
        <Dialog.Close class="icon-button" aria-label="Close source connection dialog"><X size={18} aria-hidden="true" /></Dialog.Close>
      </header>

      {#if selected}
        <form onsubmit={create}>
          {#if account}
            <div class="provider-select source-dialog-provider">
              <ProviderMark id={selected.id} definition={selected} />
              <strong>{account.sourceDisplayName || account.displayName}</strong>
            </div>
          {:else}
            <label class="field">
              <span>Source</span>
              <span class="provider-select">
                <ProviderMark id={selected.id} definition={selected} />
                <SelectField bind:value={providerId} label="Source" options={choices.map((provider) => ({ value: provider.id, label: provider.name }))} />
              </span>
            </label>
            <label class="field"><span>Connection name</span><input name="displayName" placeholder={`My ${selected.name} connection`} /></label>
            <label class="field">
              <span>Who can use it?</span>
              <SelectField name="scope" label="Who can use it?" value="User" options={[
                { value: "User", label: "Only me" },
                ...(administrator ? [{ value: "Global", label: "Everyone" }, { value: "Library", label: "One library" }] : []),
              ]} />
            </label>
            {#if administrator}<label class="field"><span>Library ID (only for one library)</span><input name="libraryScopeId" /></label>{/if}
          {/if}

          <div class="source-setting-grid">
            {#each accountSettings(selected) as field}
              <label class="field" class:wide={accountSettings(selected).length === 1}>
                <span>{field.label}</span>
                {#if field.type === "select"}
                  <SelectField name={field.key} label={field.label} value={String(settingDefault(field))} options={field.options ?? []} required={field.required} />
                {:else if field.type === "toggle"}
                  <input name={field.key} type="checkbox" checked={settingDefault(field) === true} />
                {:else}
                  <input
                    name={field.key}
                    type={field.sensitive ? "password" : field.type === "number" ? "number" : field.type === "url" ? "url" : "text"}
                    value={String(settingDefault(field))}
                    required={field.required}
                    autocomplete={field.key === "username" ? "username" : field.key === "password" ? "current-password" : "off"}
                  />
                {/if}
                {#if field.helpText}<small>{field.helpText}</small>{/if}
              </label>
            {/each}
          </div>
          {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
          <footer>
            <Dialog.Close class="button-secondary">Cancel</Dialog.Close>
            <button class="button-primary" type="submit" disabled={saving}>{saving ? "Saving…" : "Save and test"}</button>
          </footer>
        </form>
      {:else}
        <div class="compact-empty"><strong>No account-based Sources available</strong><p>Install or enable a provider that declares account settings.</p></div>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>
