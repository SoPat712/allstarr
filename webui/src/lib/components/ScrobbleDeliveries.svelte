<script lang="ts">
  import { onMount } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import SelectField from "$lib/components/SelectField.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import { scrobbling, type ScrobbleDelivery, type ProviderAccount } from "$lib/api";
  import { toast } from "$lib/toasts";

  let { accounts, targetId = "", onConfigure }: {
    accounts: ProviderAccount[];
    targetId?: string;
    onConfigure: (account: ProviderAccount) => void;
  } = $props();
  let items = $state<ScrobbleDelivery[]>([]);
  let loading = $state(true);
  let error = $state("");
  let busy = $state(false);
  let before = $state<string | undefined>();
  let hasMore = $state(false);
  let confirmation = $state(false);
  let selectedDestinations = $state<Record<string, string>>({});
  let chosen = $state<ScrobbleDelivery | null>(null);
  let chosenAction = $state<"retry" | "confirm-delivered">("retry");
  const visible = $derived(items.filter(item => !targetId || item.targetId === targetId));
  const destination = (item: ScrobbleDelivery) => accounts.find(account => account.id === (item.destinationAccountId ?? selectedDestinations[item.id] ?? item.suggestedDestinationAccountId));
  const targetName = (id: string) => ({ lastfm: "Last.fm", listenbrainz: "ListenBrainz", "multi-scrobbler": "multi-scrobbler", koito: "Koito" })[id] ?? id;
  const uncertain = (item: ScrobbleDelivery) => (item.providerCode === "ambiguous" || item.providerCode === "destination-unbound") || Boolean(item.attemptStartedAt);

  async function load(more = false) {
    if (busy) return;
    busy = true;
    error = "";
    try {
      const result = await scrobbling.deliveries(more ? before : undefined, targetId || undefined);
      if (!Array.isArray(result.items)) throw new Error("Delivery status returned an invalid response. Refresh to try again.");
      items = more ? [...items, ...result.items] : result.items;
      before = result.items.at(-1)?.id;
      hasMore = result.items.length === 100;
    } catch (failure) {
      error = failure instanceof Error ? failure.message : "Delivery status could not be loaded.";
    } finally { loading = false; busy = false; }
  }
  onMount(() => { void load(); });

  function choose(item: ScrobbleDelivery, action: "retry" | "confirm-delivered") {
    chosen = item;
    chosenAction = action;
    confirmation = true;
  }
  async function resolve() {
    if (!chosen) return;
    busy = true;
    error = "";
    try {
      await scrobbling.resolve(chosen, chosenAction, destination(chosen)?.revision, destination(chosen)?.id);
      confirmation = false;
      toast.success(chosenAction === "retry" ? "Listen queued for the saved destination." : "Delivery confirmed without sending again.");
    } catch (failure) {
      error = failure instanceof Error ? failure.message : "The delivery could not be resolved. Refresh its status and try again.";
    } finally { busy = false; }
    if (!confirmation) await load();
  }
</script>

<section class="settings-card scrobble-deliveries" aria-labelledby="scrobble-delivery-title">
  <header>
    <div><h3 id="scrobble-delivery-title">Scrobble delivery</h3><small>Your listens waiting for retry or repair. Reconnect the saved account before retrying.</small></div>
    <Button variant="secondary" disabled={busy} onclick={() => void load()}>Refresh delivery</Button>
  </header>
  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
  {#if loading}<p role="status">Loading delivery status…</p>
  {:else if visible.length === 0}<p class="service-empty">No listens need attention{targetId ? " for this service" : ""}.</p>
  {:else}
    <ul class="scrobble-delivery-list" aria-label="Your scrobble deliveries">
      {#each visible as item (item.id)}
        {@const account = destination(item)}
        <li>
          <div class="scrobble-delivery-copy">
            <strong>{item.submission?.title ?? "Saved listen"}</strong>
            {#if item.submission}<span>{item.submission.artist} · {new Date(item.submission.occurredAt).toLocaleString()}</span>{/if}
            <span>{targetName(item.targetId)} · {item.destinationAccountId ? "Saved account:" : "Destination to confirm:"} {account?.displayName ?? "Connect the intended account"}</span>
            <Badge state={item.state === "retrying" && !uncertain(item) ? "suggested" : "degraded"}>
              {uncertain(item) ? "Check destination history" : item.requiresReauthentication ? "Reconnect account" : item.state === "retrying" ? "Waiting to retry" : "Needs attention"}
            </Badge>
            <p>{item.safeMessage}</p>
            {#if !item.destinationAccountId && item.availableDestinationAccountIds?.length}
              <SelectField label="Destination account for older listen" value={destination(item)?.id ?? ""}
                options={accounts.filter(a => item.availableDestinationAccountIds?.includes(a.id)).map(a => ({ value: a.id, label: `${a.displayName} (${a.scope})` }))}
                disabled={busy} onchange={value => selectedDestinations[item.id] = value} />
            {/if}
            {#if item.retryAfter}<small>Next attempt {new Date(item.retryAfter).toLocaleString()}</small>{/if}
          </div>
          {#if item.state === "permanentfailure" || uncertain(item)}
            <div class="scrobble-delivery-actions">
              {#if !account}<a href={`#/integrations/services/${item.targetId}`}>Connect this service</a>{/if}
              {#if item.requiresReauthentication && account?.canManage}<Button variant="secondary" disabled={busy} onclick={() => onConfigure(account)}>Reconnect saved account</Button>{/if}
              {#if uncertain(item)}<Button variant="secondary" disabled={busy} onclick={() => choose(item, "confirm-delivered")}>Already in history</Button>{/if}
              <Button disabled={busy || !account?.enabled || !account.secret.configured || account.secret.revoked} onclick={() => choose(item, "retry")}>Retry saved listen</Button>
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
  {#if hasMore}<Button variant="secondary" disabled={busy} onclick={() => void load(true)}>Load earlier deliveries</Button>{/if}
</section>

<ConfirmDialog bind:open={confirmation}
  title={chosenAction === "retry" ? "Retry this saved listen?" : "Confirm delivery from its history?"}
  description={chosenAction === "confirm-delivered" ? "Only confirm if you found this listen in the destination's history. Allstarr will record it as delivered without sending anything."
    : chosen?.providerCode === "destination-unbound" ? `This older listen has no saved destination. Confirm that ${destination(chosen)?.displayName ?? "the selected account"} is the intended account, and check its history before retrying. Another send could create a duplicate.`
    : chosen && uncertain(chosen) ? "Check the destination's history first. Retry only if this listen is missing; another send could otherwise create a duplicate. It will use the saved destination account's current credentials."
    : "This listen will be queued for the same saved destination account using its current credentials."}
  confirmLabel={chosenAction === "retry" ? "Retry to saved account" : "Confirm already delivered"}
  confirmVariant="default" disabled={busy} closeOnConfirm={false} {error} onConfirm={resolve} />

<style>
  .scrobble-delivery-list { list-style: none; margin: 0; padding: 0; }
  .scrobble-delivery-list li { display: grid; gap: var(--section-gap); padding-block: var(--surface-gutter); border-top: 1px solid var(--md-sys-color-outline-variant); }
  .scrobble-delivery-copy { display: grid; gap: var(--space-2); min-width: 0; overflow-wrap: anywhere; }
  .scrobble-delivery-copy p { margin: 0; }
  .scrobble-delivery-copy :global(.badge) { justify-self: start; }
  .scrobble-delivery-actions { display: flex; flex-wrap: wrap; gap: var(--space-2); }
  .scrobble-delivery-actions a { display: inline-flex; align-items: center; min-height: var(--control-md); }
  @media (max-width: 650px) { .scrobble-delivery-actions { display: grid; } }
</style>
