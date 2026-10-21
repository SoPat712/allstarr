<script lang="ts">
  import { Dialog } from "$lib/components/ui/dialog";
  import { Button, buttonVariants } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { X } from "@lucide/svelte";
  import { sources, type ProviderAccount, type ProviderDefinition } from "$lib/api";
  import ProviderMark from "$lib/components/ProviderMark.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { supportedAudiences, sharingDescription, accountSubtitle, accountSettings, secretFromForm, settingDefault } from "$lib/sources";

  let {
    open = $bindable(false),
    providers,
    administrator,
    currentUserId = "",
    testConnection = false,
    account = null,
    users = [],
    initialProviderId = "",
    onSaved,
  }: {
    open: boolean;
    providers: ProviderDefinition[];
    administrator: boolean;
    currentUserId?: string;
    testConnection?: boolean;
    account?: ProviderAccount | null;
    users?: { id: string; displayName: string }[];
    initialProviderId?: string;
    onSaved: (message: string) => void | Promise<void>;
  } = $props();

  let providerId = $state("");
  let scope = $state<"Personal" | "Shared">("Personal");
  let ownerUserId = $state("");
  let saving = $state(false);
  let error = $state("");
  let initialized = $state(false);

  let choices = $state<ProviderDefinition[]>([]);
  const selected = $derived(
    choices.find((provider) => provider.id === (account?.providerId ?? providerId)) ?? choices[0],
  );

  const currentSetting = (key: string, fallback: unknown) =>
    account?.configuration?.[key] ?? fallback;

  $effect(() => {
    if (!open) {
      choices = providers.filter((provider) => accountSettings(provider).length);
      initialized = false; error = ""; scope = "Personal"; ownerUserId = "";
    } else if (!initialized) {
      choices = providers.filter((provider) => accountSettings(provider).length);
      if (choices[0]) {
        initialized = true;
        providerId = account?.providerId || initialProviderId || choices[0].id;
        ownerUserId = currentUserId;
      }
    }
  });

  $effect(() => {
    if (!open || account || !selected) return;
    const allowed = supportedAudiences(selected).filter((value) => administrator || value === "Personal");
    if (!allowed.includes(scope) && allowed[0]) scope = allowed[0];
  });

  async function create(event: SubmitEvent) {
    event.preventDefault();
    if (!selected || saving) return;
    saving = true;
    error = "";
    const form = event.currentTarget as HTMLFormElement;
    const data = new FormData(form);
    const secret = Object.fromEntries(Object.entries(secretFromForm(selected, data))
      .filter(([key, value]) => value !== "" || !accountSettings(selected).some(field => field.key === key && field.sensitive)));
    try {
      if (!account && scope === "Shared" && !data.has("confirmShare"))
        throw new Error("Confirm that household users may use this connection.");
      let saved: ProviderAccount;
      if (account) {
        const replacement = await sources.replaceSecret(account, secret);
        saved = account.enabled
          ? { ...account, revision: replacement.revision }
          : await sources.setEnabled({ ...account, revision: replacement.revision }, true);
      } else {
        saved = await sources.create({
          providerId: selected.id,
          displayName: String(data.get("displayName") || "").trim(),
          scope,
          ...(administrator && scope === "Personal" && ownerUserId ? { ownerUserId } : {}),
          enabled: true,
          secret,
        });
      }
      if (selected.id === "lastfm")
        await sources.authenticateLastFm(
          saved.id,
          String(data.get("username") || ""),
          String(data.get("password") || ""),
        );
      const savedState = !account ? "connected" : account.enabled ? "configuration saved" : "connection enabled";
      if (!testConnection) {
        await onSaved(`${selected.name} ${savedState}.`);
      } else try {
        await sources.test(saved);
        await onSaved(`${selected.name} ${savedState} and tested.`);
      } catch {
        await onSaved(`${selected.name} ${savedState}. Its connection test needs attention.`);
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
              <span><strong>{selected.name}</strong><small>{accountSubtitle(account)}</small></span>
            </div>
          {:else}
            <label class="field">
              <span>Source</span>
              <span class="provider-select">
                <ProviderMark id={selected.id} definition={selected} />
                <SelectField bind:value={providerId} label="Source" options={choices.map((provider) => ({ value: provider.id, label: provider.name }))} />
              </span>
            </label>
            {#if administrator && scope === "Personal" && users.length > 0}
              <label class="field"><span>Account owner</span><SelectField bind:value={ownerUserId} label="Account owner" options={users.map((user) => ({ value: user.id, label: user.displayName + (user.id === currentUserId ? " (you)" : "") }))} /></label>
              {#if ownerUserId && ownerUserId !== currentUserId}<p class="credential-safety">Only that user's listening will use this account.</p>{/if}
            {/if}
            <label class="field"><span>Connection name</span><input name="displayName" placeholder={selected.name} /></label>
            <label class="field">
              <span>Who can use it?</span>
              <SelectField name="scope" label="Who can use it?" bind:value={scope} options={[
                ...supportedAudiences(selected).filter((value) => administrator || value === "Personal")
                  .map((value) => ({ value, label: value })),
              ]} />
              <small>{scope === "Shared" ? sharingDescription(selected) : administrator ? "Only the selected owner can use this provider connection." : "Only you can use this provider connection."}</small>
            </label>
            {#if scope === "Shared"}
              <label class="toggle-line"><Checkbox name="confirmShare" required /><span>I agree to share provider access with household users.<small>Other users may consume this account’s provider limits. Credentials stay hidden. Supported capabilities may include playlists and scrobbling.</small></span></label>
            {/if}
          {/if}

          <div class="source-setting-grid">
            {#each accountSettings(selected) as field}
              <label class="field" class:wide={accountSettings(selected).length === 1}>
                <span>{field.label}</span>
                {#if field.type === "select"}
                  <SelectField name={field.key} label={field.label} value={String(currentSetting(field.key, settingDefault(field)))} options={field.options ?? []} required={field.required} />
                {:else if field.type === "toggle"}
                  <Checkbox name={field.key} checked={currentSetting(field.key, settingDefault(field)) === true} />
                {:else if field.sensitive && "multiline" in field && field.multiline === true}
                  <textarea
                    class="account-input"
                    name={field.key}
                    rows={6}
                    required={field.required && !account?.configuredFields?.includes(field.key)}
                    autocomplete="off"
                    autocapitalize="off"
                    spellcheck="false"
                    wrap="off"
                    placeholder={"placeholder" in field && typeof field.placeholder === "string" ? field.placeholder : "Paste the Debug Info here"}
                  ></textarea>
                {:else}
                  <input
                    class="account-input"
                    name={field.key}
                    type={field.sensitive ? "password" : field.type === "number" ? "number" : field.type === "url" ? "url" : "text"}
                    value={field.sensitive ? "" : String(currentSetting(field.key, settingDefault(field)))}
                    required={field.required && (!field.sensitive || !account?.configuredFields?.includes(field.key))}
                    autocomplete={field.key === "username" ? "username" : field.key === "password" ? "current-password" : "off"}
                  />
                {/if}
                {#if field.sensitive && account?.configuredFields?.includes(field.key)}<small>Configured · replace</small>{/if}
                {#if field.helpText}<small>{field.helpText}</small>{/if}
              </label>
            {/each}
          </div>
          {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
          <footer class="dialog-actions">
            <Dialog.Close class={buttonVariants({ variant: "secondary" })}>Cancel</Dialog.Close>
            <Button type="submit" disabled={saving}>{saving ? "Saving…" : testConnection ? "Save and test" : "Save connection"}</Button>
          </footer>
        </form>
      {:else}
        <div class="compact-empty"><strong>No account-based Sources available</strong><p>Install or enable a provider that declares account settings.</p></div>
      {/if}
    </Dialog.Content>
  </Dialog.Portal>
</Dialog.Root>

<style>
  .account-input {
    min-width: 0;
    border: 1px solid var(--color-edge);
    border-radius: var(--radius-sm);
    background: var(--color-panel-raised);
    color: var(--color-ink);
    padding: 0 0.9rem;
    font-weight: 400;
  }

  .account-input:hover:not(:disabled) {
    border-color: var(--color-ink-muted);
  }

  .account-input:focus-visible {
    border-color: var(--color-signal);
    outline: 1px solid var(--color-signal);
    outline-offset: 0;
  }

  textarea.account-input {
    width: 100%;
    min-height: 9rem;
    max-height: 18rem;
    resize: vertical;
    padding: 0.75rem 0.9rem;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-size: 0.875rem;
    line-height: 1.4;
  }
</style>
