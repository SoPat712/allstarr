<script lang="ts">
  import RefreshButton from "$lib/components/RefreshButton.svelte";
  import { formatLocalDateTime } from "$lib/dates";
  import { countLabel } from "$lib/count-label";
  import { untrack } from "svelte";
  import { scrobbling, type ScrobbleDelivery, type ProviderAccount } from "$lib/api";
  import { canResend, groupDeliveries, visibleDeliveries } from "$lib/source-deliveries";
  import GroupedRow from "$lib/components/patterns/GroupedRow.svelte";
  import EmptyState from "$lib/components/patterns/EmptyState.svelte";
  import ErrorState from "$lib/components/patterns/ErrorState.svelte";
  import RowsSkeleton from "$lib/components/patterns/RowsSkeleton.svelte";
  import StatePill from "$lib/components/patterns/StatePill.svelte";
  import ConfirmDialog from "$lib/components/ConfirmDialog.svelte";
  import SelectField from "$lib/components/SelectField.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Music2 } from "@lucide/svelte";
  import { toast } from "$lib/toasts";

  let { accounts, accountId = "", targetId = "", refreshKey = "", showRefresh = true, onConfigure, onloaded, onchanged }: {
    accounts: ProviderAccount[]; accountId?: string; targetId?: string; refreshKey?: string;
    onConfigure: (account: ProviderAccount) => void;
    onloaded?: (items: ScrobbleDelivery[], complete: boolean) => void;
    onchanged?: () => void;
    showRefresh?: boolean;
  } = $props();
  let items = $state<ScrobbleDelivery[]>([]), loading = $state(true), busy = $state(false), error = $state("");
  let hasMore = $state(false), before = $state<string>();
  let selectedDestinations = $state<Record<string, string>>({});
  let confirmation = $state(false), chosen = $state<ScrobbleDelivery[]>([]);
  let chosenAction = $state<"retry" | "confirm-delivered">("retry");
  const destination = (item: ScrobbleDelivery) => accounts.find(account => account.id === (item.destinationAccountId ?? selectedDestinations[item.id] ?? item.suggestedDestinationAccountId));
  const uncertain = (item: ScrobbleDelivery) => ["ambiguous", "destination-unbound"].includes(item.providerCode ?? "") || Boolean(item.attemptStartedAt);
  const visible = $derived(visibleDeliveries(items).filter(item => (!targetId || item.targetId === targetId) && (!accountId || destination(item)?.id === accountId)));
  const groups = $derived(groupDeliveries(visible));
  const targetName = (id: string) => ({ lastfm: "Last.fm", listenbrainz: "ListenBrainz", "multi-scrobbler": "multi-scrobbler", koito: "Koito" })[id] ?? id;
  const canRetry = (item: ScrobbleDelivery) => canResend(item) && Boolean(destination(item)?.enabled && destination(item)?.secret.configured && !destination(item)?.secret.revoked);
  const needsAction = (item: ScrobbleDelivery) => item.state === "permanentfailure" || uncertain(item);

  async function load(more = false) {
    if (busy) return;
    busy = true; error = "";
    try {
      const result = await scrobbling.deliveries(more ? before : undefined, targetId || undefined);
      if (!Array.isArray(result.items)) throw new Error("Listen status could not be read. Try again.");
      items = more ? [...items, ...result.items] : result.items;
      before = result.items.at(-1)?.id;
      hasMore = result.items.length === 100;
      onloaded?.(items, !hasMore);
    } catch (cause) { error = cause instanceof Error ? cause.message : "Listen status could not be loaded."; }
    finally { loading = false; busy = false; }
  }
  $effect(() => { void refreshKey; void targetId; untrack(() => void load()); });

  function choose(listens: ScrobbleDelivery[], action: "retry" | "confirm-delivered") {
    chosen = listens; chosenAction = action; error = ""; confirmation = true;
  }
  async function resolve() {
    if (busy) return;
    busy = true; error = "";
    let completed = 0;
    try {
      for (const item of chosen) {
        await scrobbling.resolve(item, chosenAction, destination(item)?.revision, destination(item)?.id);
        completed++;
      }
      confirmation = false;
      toast.success(chosenAction === "retry" ? `${countLabel(completed, "listen")} queued for the saved destination.` : "Delivery confirmed without sending again.");
    } catch (cause) {
      confirmation = false;
      toast.error(`${completed ? `${completed} queued. ` : ""}${cause instanceof Error ? cause.message : "The listens could not be updated."} Refresh before trying again.`);
    } finally { busy = false; }
    await load();
    onchanged?.();
  }
  async function dismiss(item: ScrobbleDelivery) {
    if (busy) return;
    busy = true;
    try {
      await scrobbling.resolve(item, "dismiss");
      toast.success("Delivery dismissed.");
    } catch (cause) { toast.error(cause instanceof Error ? cause.message : "The delivery could not be dismissed."); }
    finally { busy = false; }
    await load();
    onchanged?.();
  }
</script>

<section class="delivery-groups" aria-label="Scrobble delivery" aria-busy={busy} hidden={!loading && !error && !visible.length && !hasMore && !accountId}>
  {#if error}<ErrorState title="Listens could not be loaded" detail={error} onretry={() => load()} />{/if}
  {#if loading}<RowsSkeleton count={1} />
  {:else if !visible.length && accountId}
    <EmptyState title={hasMore ? "No listens for this account on this page" : "No listens waiting"} description={hasMore ? "Load earlier deliveries to check older listens." : "Listens for this account have no reported delivery problems."}>{#snippet icon()}<Music2 size={24} />{/snippet}</EmptyState>
  {:else}
    {#each groups as group (group.key)}
      {@const first = group.items[0]}
      {@const name = targetName(first.targetId)}
      {@const reason = first.unavailableReason || (first.providerCode === "destination-unbound" ? "The destination needs confirming" : first.requiresReauthentication ? "Reconnect the saved account" : first.safeMessage || "Waiting to retry")}
      <div class="delivery-group">
        {#if accountId}<p class="delivery-reason"><strong>{countLabel(group.items.length, "listen is", "listens are")} waiting.</strong> {reason}</p>{/if}
        <GroupedRow count={group.items.length} summary={`${group.items.length === 1 ? "listen" : "listens"} waiting for ${name}`} {reason} expanded={Boolean(accountId)}>
          {#snippet action()}
            <div class="delivery-actions">
              {#if canResend(first)}
                {#if first.requiresReauthentication && destination(first)?.canManage}<Button variant="outline" size="sm" disabled={busy} onclick={() => onConfigure(destination(first)!)}>Reconnect saved account</Button>
                {:else if needsAction(first)}<Button variant="outline" size="sm" disabled={busy || !group.items.every(canRetry)} onclick={() => choose(group.items, "retry")}>{first.providerCode === "destination-unbound" ? "Confirm destination" : "Retry all"}</Button>{/if}
                {#if first.providerCode === "destination-unbound"}<Button variant="text" size="sm" disabled={busy || !group.items.every(canRetry)} onclick={() => choose(group.items, "retry")}>Retry all</Button>{/if}
              {/if}
            </div>
          {/snippet}
          {#snippet members()}
            {#each group.items as item (item.id)}
              {@const account = destination(item)}
              <div class="listen-row">
                <span class="listen-icon" aria-hidden="true"><Music2 size={18} /></span>
                <div class="listen-copy"><strong>{item.submission?.title ?? "Saved listen"}</strong>
                  <small>{item.submission?.artist ?? name} · {formatLocalDateTime(item.submission?.occurredAt ?? item.updatedAt, "Invalid Date")}</small>
                  <small>{item.destinationAccountId ? "Saved account" : "Destination to confirm"}: {account?.displayName ?? "Connect the intended account"}</small>
                  {#if canResend(item) && !item.destinationAccountId && item.availableDestinationAccountIds?.length}
                    <SelectField label="Destination account for older listen" value={account?.id ?? ""} options={accounts.filter(a => item.availableDestinationAccountIds?.includes(a.id)).map(a => ({ value: a.id, label: `${a.displayName} (${a.scope})` }))} disabled={busy} onchange={value => selectedDestinations[item.id] = value} />
                  {/if}
                  {#if item.retryAfter}<small>Next attempt {formatLocalDateTime(item.retryAfter, "Invalid Date")}</small>{/if}
                </div>
                <div class="listen-actions">
                  {#if item.canDismiss}<Button variant="text" size="sm" disabled={busy} onclick={() => void dismiss(item)}>Dismiss</Button>
                  {:else if needsAction(item) && canResend(item)}
                    {#if item.canConfirm}<Button variant="text" size="sm" disabled={busy} onclick={() => choose([item], "confirm-delivered")}>Already in history</Button>{/if}
                    <Button variant="text" size="sm" disabled={busy || !canRetry(item)} onclick={() => choose([item], "retry")}>Retry</Button>
                    {#if !account}<Button variant="text" size="sm" href={`#/sources/accounts?source=${item.targetId}&connect=1`}>Connect this service</Button>{/if}
                  {:else}<StatePill text={item.state === "retrying" ? "Waiting to retry" : "Cannot resend"} />{/if}
                </div>
              </div>
            {/each}
          {/snippet}
        </GroupedRow>
      </div>
    {/each}
  {/if}
  {#if showRefresh}<RefreshButton disabled={busy} onrefresh={() => load()} label="Refresh delivery" />{/if}
  {#if hasMore}<Button variant="text" disabled={busy} onclick={() => void load(true)}>Load earlier deliveries</Button>{/if}
</section>

<ConfirmDialog bind:open={confirmation}
  title={chosenAction === "confirm-delivered" ? "Confirm delivery from its history?" : chosen.some(item => item.providerCode === "destination-unbound") ? "Confirm destination?" : `Retry ${chosen.length === 1 ? "this saved listen" : `all ${countLabel(chosen.length, "listen")}`}?`}
  description={chosenAction === "confirm-delivered" ? "Only confirm listens you found in the destination's history. They will be marked delivered without sending again."
    : chosen.some(uncertain) ? `Confirm the destination: ${[...new Set(chosen.map(item => { const account = destination(item); return account ? `${account.displayName} · ${account.scope === "Shared" ? "shared with everyone" : account.ownerDisplayName || "personal account"}` : "No account selected"; }))].join(", ")}. Check the destination's history first. Only missing listens should be sent again; another send could create duplicates. The saved account's current sign-in is used.`
    : "These listens will be queued for the same saved destination account using its current sign-in."}
  confirmLabel={chosenAction === "retry" ? "Retry to saved account" : "Confirm already delivered"} confirmVariant="default" disabled={busy} closeOnConfirm={false} onConfirm={resolve} />

<style>
  .delivery-groups, .delivery-group { display: grid; min-width: 0; gap: var(--space-3); }
  .delivery-groups[hidden] { display: none; }
  .delivery-actions, .listen-actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-1); }
  .delivery-reason { margin: 0; padding: var(--space-3); border-radius: var(--radius-md); background: color-mix(in srgb, var(--warning) 10%, var(--color-panel)); font-size: var(--text-sm); overflow-wrap: anywhere; }
  .listen-row { display: flex; gap: var(--space-3); align-items: center; border-bottom: 1px solid var(--color-edge); padding: var(--space-3); }
  .listen-row:last-child { border-bottom: 0; }
  .listen-icon { display: grid; width: 36px; height: 36px; flex-shrink: 0; place-items: center; border-radius: var(--radius-sm); background: var(--color-panel-raised); color: var(--color-ink-muted); }
  .listen-copy { display: grid; min-width: 0; flex: 1; gap: var(--space-1); overflow-wrap: anywhere; }
  .listen-copy strong { font-size: var(--text-sm); }
  .listen-copy small { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .listen-actions { justify-content: flex-end; }
  @media (max-width: 599px) { .delivery-groups :global(.group-heading) { flex-wrap: wrap; padding-right: 0; } .delivery-groups :global(.group-action) { padding: 0 var(--space-3) var(--space-2); } .listen-row { flex-wrap: wrap; } .listen-actions { width: 100%; } }
</style>
