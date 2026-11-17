<script lang="ts">
  import { onMount } from "svelte";
  import { listeningPreferences, type MyLibraries } from "$lib/api";
  import { toast } from "$lib/toasts";
  import { Switch } from "$lib/components/ui/switch";

  let { administrator = false }: { administrator?: boolean } = $props();
  let libraries = $state<MyLibraries | null>(null);
  let saving = $state(false);
  const chosen = (id: string) => libraries?.selected == null || libraries.selected.includes(id);
  const onCount = $derived(libraries ? libraries.available.filter(library => chosen(library.id)).length : 0);

  onMount(async () => {
    try {
      const read = await listeningPreferences.libraries();
      libraries = Array.isArray(read?.available) ? read : null;
    } catch { libraries = null; }
  });

  async function toggle(id: string, on: boolean) {
    if (!libraries || saving) return;
    const next = libraries.available.map(library => library.id).filter(library => library === id ? on : chosen(library));
    if (next.length === 0) return;
    saving = true;
    try {
      libraries = await listeningPreferences.saveLibraries(next.length === libraries.available.length ? null : next);
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : "Your music libraries were not changed.");
    } finally { saving = false; }
  }
</script>

<!-- One library leaves nothing to choose, so the card only appears when there are several. -->
{#if libraries && libraries.available.length > 1}
  <section class="settings-card preferences-panel" aria-label="Your music libraries" aria-busy={saving}>
    <header>
      {#if administrator}<div class="settings-card-title"><h3>Your music libraries</h3><span class="scope-tag" data-scope="you">Just you</span></div>
      {:else}<h3>Your music libraries</h3>{/if}
      <p>Mixes, Made for you and autoplay pick your songs from these. Everyone else keeps their own choice.</p>
    </header>
    {#each libraries.available as library (library.id)}
      <div class="settings-row">
        <div><strong>{library.name}</strong></div>
        <span class="settings-toggle"><Switch checked={chosen(library.id)} disabled={saving || chosen(library.id) && onCount === 1}
          aria-label={`Use ${library.name}`} onchange={event => void toggle(library.id, event.currentTarget.checked)} /></span>
      </div>
    {/each}
  </section>
{/if}

<style>
  .preferences-panel { min-width: 0; overflow: hidden; }
  header { padding: var(--space-3) var(--space-4); }
  h3 { margin: 0; font-size: var(--text-sm); font-weight: 600; }
  header p { margin: var(--space-1) 0 0; color: var(--color-ink-muted); font-size: var(--text-sm); }
</style>
