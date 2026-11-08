<script lang="ts">
  import { tick } from "svelte";
  import { GripVertical, MoreHorizontal, X } from "@lucide/svelte";
  import { DropdownMenu } from "$lib/components/ui/dropdown-menu";
  import { compileTokens, namingFields, parseScript, tokenLabel, type NamingToken } from "$lib/promotion-naming";

  let { script, onchange }: { script: string; onchange: (script: string) => void } = $props();
  type Entry = NamingToken & { id: number };
  let entries = $state<Entry[]>([]), announcement = $state("");
  let sequence = 0;
  const parsed = $derived(script === "" ? [] : parseScript(script));
  $effect(() => {
    if (parsed && compileTokens(entries) !== script) entries = parsed.map(token => ({ ...token, id: ++sequence }));
  });
  function changed() { onchange(compileTokens(entries)); }
  async function focus(id: number) { await tick(); document.getElementById(`naming-token-${id}`)?.focus(); }
  function add(token: NamingToken, index = entries.length) {
    const entry = { ...token, id: ++sequence };
    entries = [...entries.slice(0, index), entry, ...entries.slice(index)];
    changed(); announcement = `Added ${tokenLabel(token)} at position ${index + 1}.`; void focus(entry.id);
  }
  function move(id: number, target: number) {
    const index = entries.findIndex(entry => entry.id === id);
    if (index < 0 || target < 0 || target >= entries.length || target === index) return;
    const next = [...entries], [entry] = next.splice(index, 1);
    next.splice(target, 0, entry); entries = next; changed();
    announcement = `${tokenLabel(entry)} moved to position ${target + 1} of ${entries.length}.`; void focus(id);
  }
  function remove(id: number) {
    const index = entries.findIndex(entry => entry.id === id), entry = entries[index];
    entries = entries.filter(entry => entry.id !== id); changed();
    announcement = `Removed ${tokenLabel(entry)}.`;
    const next = entries[Math.min(index, entries.length - 1)]; if (next) void focus(next.id);
  }
  function key(event: KeyboardEvent, id: number, index: number) {
    const delta = ["ArrowLeft", "ArrowUp"].includes(event.key) ? -1 : ["ArrowRight", "ArrowDown"].includes(event.key) ? 1 : 0;
    if (delta) { event.preventDefault(); move(id, index + delta); }
    if (event.key === "Delete" || event.key === "Backspace") { event.preventDefault(); remove(id); }
  }
  function drag(event: DragEvent, payload: { id: number } | { field: string }) {
    if (!event.dataTransfer) return;
    event.dataTransfer.setData("application/x-naming-token", JSON.stringify(payload));
    event.dataTransfer.effectAllowed = "copyMove";
  }
  function drop(event: DragEvent, index: number) {
    event.preventDefault(); event.stopPropagation();
    const data = event.dataTransfer?.getData("application/x-naming-token");
    if (!data) return;
    try {
      const payload = JSON.parse(data);
      if (typeof payload.id === "number") move(payload.id, Math.min(index, entries.length - 1));
      else if (namingFields.some(field => field.value === payload.field)) add({ kind: "field", value: payload.field }, index);
    } catch { announcement = "Try dragging the field again, or use its button."; }
  }
</script>

{#if parsed === null}
  <div class="script-only"><strong>Script only</strong><p>This script uses expressions the builder cannot show. Edit the naming script below; your script stays unchanged.</p></div>
{:else}
  <p class="builder-help" id="naming-move-help">Drag fields into the name. Focus a token and use arrow keys or its move menu to reorder it.</p>
  <ol class="naming-builder" aria-label="Naming tokens" aria-describedby="naming-move-help" ondragover={event => event.preventDefault()} ondrop={event => drop(event, entries.length)}>
    {#each entries as entry, index (entry.id)}
      <li class:literal={entry.kind === "literal"} ondragover={event => event.preventDefault()} ondrop={event => drop(event, index)}>
        <button id={`naming-token-${entry.id}`} class="token-handle" type="button" draggable="true" ondragstart={event => drag(event, { id: entry.id })} onkeydown={event => key(event, entry.id, index)} aria-label={`${tokenLabel(entry)}, token ${index + 1}`} aria-describedby="naming-move-help"><GripVertical size={13} aria-hidden="true" />{entry.kind === "field" ? tokenLabel(entry) : "Text"}</button>
        {#if entry.kind === "literal"}<input aria-label={`Text for token ${index + 1}`} value={entry.value} size={Math.max(1, Math.min(entry.value.length, 18))} oninput={event => { entry.value = event.currentTarget.value; changed(); }} />{/if}
        <DropdownMenu.Root><DropdownMenu.Trigger class="token-action" aria-label={`Move token ${index + 1}`}><MoreHorizontal size={14} aria-hidden="true" /></DropdownMenu.Trigger><DropdownMenu.Portal><DropdownMenu.Content preventScroll={false} class="bits-menu" align="start">
          <DropdownMenu.Item class="bits-menu-item" disabled={index === 0} onSelect={() => move(entry.id, index - 1)}>Move left</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" disabled={index === entries.length - 1} onSelect={() => move(entry.id, index + 1)}>Move right</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" disabled={index === 0} onSelect={() => move(entry.id, 0)}>Move to start</DropdownMenu.Item>
          <DropdownMenu.Item class="bits-menu-item" disabled={index === entries.length - 1} onSelect={() => move(entry.id, entries.length - 1)}>Move to end</DropdownMenu.Item>
        </DropdownMenu.Content></DropdownMenu.Portal></DropdownMenu.Root>
        <button class="token-action" type="button" aria-label={`Remove token ${index + 1}`} onclick={() => remove(entry.id)}><X size={13} aria-hidden="true" /></button>
      </li>
    {/each}
    {#if !entries.length}<li class="empty-name">Add a field or text to start a name.</li>{/if}
  </ol>
  <div class="naming-palette" aria-label="Naming fields"><span>Fields:</span>{#each namingFields as field}<button type="button" draggable="true" ondragstart={event => drag(event, { field: field.value })} onclick={() => add({ kind: "field", value: field.value })} aria-label={`Add ${field.label}`}>{field.label}</button>{/each}<button class="text-token" type="button" onclick={() => add({ kind: "literal", value: " - " })}>+ Text</button></div>
{/if}
<span class="sr-only" role="status">{announcement}</span>

<style>
  .builder-help, .script-only p { margin: 0; color: var(--color-ink-muted); font-size: var(--text-xs); }
  .script-only { display: grid; gap: var(--space-2); padding: var(--space-3); border: 1px dashed var(--color-edge-strong); border-radius: var(--radius-lg); }
  .naming-builder { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); min-height: 3.25rem; margin: 0; padding: var(--space-3); list-style: none; border: 1px dashed var(--color-edge-strong); border-radius: var(--radius-lg); }
  li { display: inline-flex; align-items: center; min-width: 0; max-width: 100%; border-radius: 999px; background: var(--md-sys-color-secondary-container); color: var(--md-sys-color-on-secondary-container); }
  li.literal { background: transparent; border: 1px solid var(--color-edge-strong); color: var(--color-ink); }
  .token-handle, .naming-builder :global(.token-action) { display: inline-flex; align-items: center; justify-content: center; gap: 3px; min-height: 32px; border: 0; border-radius: 999px; background: transparent; color: inherit; padding: 0 var(--space-1); font: inherit; font-size: var(--text-xs); cursor: pointer; }
  .token-handle { padding-left: var(--space-2); cursor: grab; }
  .naming-builder :global(.token-action) { min-width: 26px; }
  li input { min-width: 2ch; max-width: 13rem; width: auto; min-height: 28px; padding: 0 var(--space-1); border: 0; background: transparent; font-size: var(--text-xs); }
  .naming-palette { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); }
  .naming-palette span { color: var(--color-ink-muted); font-size: var(--text-xs); }
  .naming-palette button { min-height: 32px; border: 1px solid transparent; border-radius: 999px; padding: var(--space-1) var(--space-3); background: var(--color-panel-raised); color: var(--color-ink); font: inherit; font-size: var(--text-xs); cursor: grab; }
  .naming-palette .text-token { border-color: var(--color-edge-strong); background: transparent; cursor: pointer; }
  .empty-name { background: transparent; color: var(--color-ink-muted); font-size: var(--text-xs); }
  @media (max-width: 650px) { .token-handle, .naming-builder :global(.token-action), .naming-palette button, li input { min-height: 44px; } .naming-builder :global(.token-action) { min-width: 44px; } li input { max-width: 8rem; } }
</style>
