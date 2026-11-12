<script lang="ts">
  import type { ConfigSection, UiSchema } from "$lib/api";
  import SettingsFields from "$lib/components/SettingsFields.svelte";
  import AdvancedFold from "$lib/components/patterns/AdvancedFold.svelte";

  // Settings that serve every source: song.link lookups, extension installs and the Cloudflare challenge solver.
  // Settings › Advanced already reads the schema and settings, so these cards show its copy and ask it to refresh after a save.
  let { schema, config, refresh }: { schema: UiSchema | null; config: Record<string, unknown>; refresh: () => Promise<void> } = $props();
  const fields = $derived((schema?.configSections ?? []).flatMap(item => item.fields));
  const pick = (...keys: string[]) => keys.flatMap(key => fields.filter(field => field.key === key));
  const group = (id: string, label: string, selected: typeof fields): ConfigSection => ({ id, label, fields: selected });
  const songLinks = $derived(group("song-links", "song.link", pick("ODESLI_ENABLED", "ODESLI_API_KEY")));
  const songLinkTiming = $derived(group("song-link-timing", "Song link timing", pick("ODESLI_RATE_LIMIT_MS")));
  const extensions = $derived(group("extension-installs", "Extensions", pick("EXTENSIONS_ALLOW_REMOTE_INSTALL")));
  const cloudflare = $derived(group("cloudflare", "Cloudflare checks", pick("CHALLENGE_SOLVER_URL", "CHALLENGE_SOLVER_TIMEOUT_SECONDS")));

</script>

{#if songLinks.fields.length || songLinkTiming.fields.length || extensions.fields.length || cloudflare.fields.length}
  {#if songLinks.fields.length}<SettingsFields section={songLinks} {config} onChanged={refresh}>
    {#snippet footer()}{#if songLinkTiming.fields.length}<AdvancedFold label="Advanced: wait between song.link lookups"><SettingsFields embedded section={songLinkTiming} {config} onChanged={refresh} /></AdvancedFold>{/if}{/snippet}
  </SettingsFields>{/if}
  {#if extensions.fields.length}<SettingsFields section={extensions} {config} onChanged={refresh} />{/if}
  {#if cloudflare.fields.length}<SettingsFields section={cloudflare} {config} onChanged={refresh} />{/if}
{/if}
