<script lang="ts">
  import type { ProviderAccount } from "$lib/api";
  import SourceDeliveryGroups from "$lib/components/SourceDeliveryGroups.svelte";
  let { accounts, targetId = "", onConfigure }: {
    accounts: ProviderAccount[];
    targetId?: string;
    onConfigure: (account: ProviderAccount) => void;
  } = $props();
</script>

<SourceDeliveryGroups {accounts} {targetId} {onConfigure} />
