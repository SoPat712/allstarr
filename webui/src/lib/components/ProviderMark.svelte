<script lang="ts">
  import { adminUrl } from "$lib/admin-url";
  import type { ProviderDefinition } from "$lib/api";
  import { providerColor } from "$lib/playlists";

  let {
    id,
    definition,
    label = definition?.name ?? id,
  }: { id: string; definition?: ProviderDefinition; label?: string } = $props();
  let failed = $state(false);
  // Extensions and variants reuse the bundled mark of the service they reach.
  const aliases: Record<string, string> = {
    amazon: "amazonmusic", appledownload: "applemusic", applemusicdownload: "applemusic", applemusickit: "applemusic", spotifyweb: "spotify",
    tidalweb: "tidal", qobuzweb: "qobuz", ytmusic: "youtubemusic", ytmusicspotiflac: "youtubemusic", youtube: "youtubemusic",
  };
  const key = (value: string) => {
    const plain = value.toLowerCase().replace(/^spotiflac-/, "").replace(/[^a-z0-9]/g, "");
    return aliases[plain] ?? plain;
  };
  const icon = $derived(
    definition?.icon && definition.icon !== "extension"
      ? definition.icon
      : definition?.icon === "extension" || !label ? key(id) : key(label),
  );
  const source = $derived(adminUrl(definition?.logoUrl || `/images/providers/${encodeURIComponent(icon || id.toLowerCase())}.svg`));

  $effect(() => {
    source;
    failed = false;
  });
</script>

<span class="provider-mark" style={`--route-color:${providerColor(id)}`}>
  {#if !failed}
    <img src={source} alt="" onerror={() => { failed = true; }} />
  {:else}
    <span aria-hidden="true">{label[0]?.toUpperCase() ?? "?"}</span>
  {/if}
  <span class="sr-only">{label}</span>
</span>
