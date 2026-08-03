<script lang="ts">
  import { browser } from "$app/environment";
  import { intelligence, type IntelligenceScope, type ListeningApp, type ListeningAppCreated } from "$lib/api";

  let { scope, policyEnabled }: { scope: IntelligenceScope; policyEnabled: boolean } = $props();
  let items = $state<ListeningApp[]>([]);
  let created = $state<ListeningAppCreated | null>(null);
  let sendToConnectedServices = $state(false);
  let busy = $state(false);
  let error = $state("");
  let loadedScope = "";
  const address = browser ? `${location.origin}/apis/listenbrainz` : "/apis/listenbrainz";
  const server = $derived(scope.protocol === "jellyfin" ? "Jellyfin" : scope.protocol === "subsonic" ? "Subsonic" : "your media server");

  $effect(() => {
    const key = `${scope.protocol}\0${scope.backendInstanceId}\0${scope.libraryScopeId}\0${policyEnabled}`;
    if (key === loadedScope) return;
    loadedScope = key;
    void load();
  });

  async function load() {
    error = "";
    try {
      items = (await intelligence.listeningApps(scope)).items;
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "Listening apps could not be loaded.";
    }
  }

  async function create() {
    busy = true;
    error = "";
    try {
      created = await intelligence.createListeningApp(scope, sendToConnectedServices);
      await load();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The private key could not be created.";
    } finally {
      busy = false;
    }
  }

  async function copy(value: string) {
    try {
      await navigator.clipboard.writeText(value);
    } catch {
      error = "Copying failed. Select the private key and copy it manually.";
    }
  }

  async function revoke(item: ListeningApp) {
    if (!confirm("Stop accepting listens from this private key?")) return;
    busy = true;
    error = "";
    try {
      await intelligence.revokeListeningApp(scope, item.id);
      items = items.filter((value) => value.id !== item.id);
    } catch (cause) {
      error = cause instanceof Error ? cause.message : "The private key could not be stopped.";
    } finally {
      busy = false;
    }
  }
</script>

<section class="panel listening-apps-card">
  <header>
    <div><p class="eyebrow">Listening apps</p><h3>Apps sending listens to Allstarr</h3></div>
  </header>
  <p>{policyEnabled ? "Give Koito or another listening app a private key. Allstarr will save its listens to" : "Saving is off, so listening apps cannot send listens to"} <strong>{scope.libraryScopeId}</strong> on {server}.</p>

  {#if error}<p class="notice-error" role="alert">{error}</p>{/if}

  {#if created}
    <div class="new-key" aria-live="polite">
      <strong>Copy this private key now</strong>
      <p>Allstarr will not show it again.</p>
      <div><input aria-label="New listening app private key" readonly value={created.token} /><button class="button-secondary" type="button" onclick={() => void copy(created!.token)}>Copy</button></div>
      <p><small>Listening address: <code>{address}</code></small></p>
    </div>
  {/if}

  <form onsubmit={(event) => { event.preventDefault(); void create(); }}>
    <label class="toggle-line"><input type="checkbox" bind:checked={sendToConnectedServices} /><span><strong>Also send these listens to my connected services</strong><small>{sendToConnectedServices ? "Allstarr will also send completed listens to your connected Last.fm and ListenBrainz accounts." : "Allstarr will keep received listens here and will not send them to another service."}</small></span></label>
    <button class="button-secondary" type="submit" disabled={!policyEnabled || busy}>{busy ? "Creating…" : "Create private key"}</button>
    {#if !policyEnabled}<small>Turn on “Save my listening automatically” before creating a private key.</small>{/if}
  </form>

  <ul class="key-list">
    {#each items as item}
      <li><article>
        <span><strong>Created {new Date(item.createdAt).toLocaleDateString()}</strong><small>{policyEnabled ? `Allstarr will save listens from this key to ${scope.libraryScopeId} on ${server}.` : "Allstarr is not accepting listens from this key while saving is off."} {item.relayExternally ? "Allstarr will also send completed listens to connected services." : "Allstarr will not send these listens to another service."}</small></span>
        <button type="button" disabled={busy} onclick={() => void revoke(item)}>Stop accepting</button>
      </article></li>
    {:else}<li class="muted">No apps can send listens to this library yet.</li>{/each}
  </ul>
</section>

<style>
  .listening-apps-card{display:grid;gap:1rem;padding:1.15rem}.listening-apps-card h3,.listening-apps-card p{margin:.2rem 0}.listening-apps-card>p{color:var(--color-ink-muted)}form{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:1rem;border-top:1px solid var(--color-edge);padding-top:1rem}.toggle-line{display:flex;gap:.75rem}.toggle-line span>*,article span>*{display:block}.toggle-line small,article small{color:var(--color-ink-muted)}.new-key{display:grid;gap:.5rem;border:1px solid var(--color-signal);border-radius:var(--radius-card);padding:1rem}.new-key>div{display:flex;gap:.5rem}.new-key input{min-width:0;flex:1;font-family:var(--font-mono)}.key-list{display:grid;margin:0;padding:0;list-style:none}.key-list article{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:1rem;border-top:1px solid var(--color-edge);padding:.8rem 0}.key-list button{color:var(--color-ink-muted)}
  @media(max-width:620px){form,.key-list article{grid-template-columns:1fr}.new-key>div{align-items:stretch;flex-direction:column}}
</style>
