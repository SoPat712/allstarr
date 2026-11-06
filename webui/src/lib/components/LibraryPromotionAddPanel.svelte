<script lang="ts">
  import { onMount, untrack } from "svelte";
  import type { Snippet } from "svelte";
  import { libraryPromotions, type LibraryRequest, type PromotionPreview, type PromotionRequest } from "$lib/library-promotion-api";
  import type { ProviderDefinition } from "$lib/api";
  import { newReportRequestId } from "$lib/support";
  import { toast } from "$lib/toasts";
  import LibraryPromotionPanel from "./LibraryPromotionPanel.svelte";

  let { open = $bindable(false), recordingId, backendLibraryId = "", rootId = "00000000-0000-0000-0000-000000000000",
    title = "Add to library", description = "", cover, providers = [], copyActions, onPrevious, onNext, onclose, onrequestchange }: {
    open?: boolean; recordingId: string; backendLibraryId?: string; rootId?: string; title?: string;
    description?: string; cover?: string | null; providers?: ProviderDefinition[]; copyActions?: Snippet;
    onPrevious?: () => void; onNext?: () => void; onclose?: () => void; onrequestchange?: () => Promise<void>;
  } = $props();
  let preview = $state<PromotionPreview | null>(null), pending = $state<LibraryRequest | null>(null);
  let request = $state<PromotionRequest | null>(null), loading = $state(false), busy = $state(false), error = $state("");
  let controller: AbortController | undefined;
  let pollController: AbortController | undefined;
  let polling = false;
  const identity = $derived(`${recordingId}:${backendLibraryId}:${rootId}`);
  async function load() {
    controller?.abort(); pollController?.abort(); const next = new AbortController(); controller = next;
    preview = null; pending = null; loading = true; error = "";
    const value: PromotionRequest = { recordingId, backendLibraryId, rootId, requestKey: newReportRequestId(), unit: "album" };
    request = value;
    try {
      if (!recordingId) throw new Error("This kept copy has no recording identity yet. Index it before adding it to the library.");
      const plan = await libraryPromotions.preview(value, next.signal);
      if (next.signal.aborted) return;
      preview = plan;
      request = { ...value, backendLibraryId: plan.backendLibraryId || value.backendLibraryId, rootId: plan.rootId || value.rootId };
      if (plan.canAddDirectly === false) {
        const requests = await libraryPromotions.requests("pending", next.signal);
        if (!next.signal.aborted) pending = requests.find(item => item.recordingId === recordingId || item.unit === "album" && item.title === plan.album.title && item.artist === plan.album.artist) ?? null;
      }
    } catch (cause) { if (!next.signal.aborted) error = cause instanceof Error ? cause.message : "The album preview could not be loaded."; }
    finally { if (!next.signal.aborted) loading = false; }
  }
  async function start(unit: "album" | "song") {
    if (!request || busy || loading) return;
    busy = true; error = "";
    try {
      const result = preview?.canAddDirectly === false ? await libraryPromotions.createRequest({ ...request, unit }) : await libraryPromotions.start({ ...request, unit });
      if ("requested" in result) { pending = result.request; toast.success("Album requested."); await onrequestchange?.(); }
      else { toast.success(unit === "album" ? "Adding the album to your library." : "Adding this song to your library."); window.location.hash = `#/library/to-library?run=${encodeURIComponent(result.id)}`; }
    } catch (cause) { error = cause instanceof Error ? cause.message : "The album could not be started. Try again."; }
    finally { busy = false; }
  }
  async function cancelRequest() {
    if (!pending || busy) return;
    busy = true;
    try { await libraryPromotions.cancelRequest(pending.id); pending = null; toast.success("Request cancelled."); await onrequestchange?.(); }
    finally { busy = false; }
  }
  async function pollRequest() {
    if (!open || loading || busy || polling || document.visibilityState === "hidden" || !pending || !["pending", "approved"].includes(pending.state)) return;
    const id = pending.id, next = new AbortController(); pollController = next; polling = true;
    try {
      const receipt = await libraryPromotions.getRequest(id, next.signal);
      if (next.signal.aborted || pending?.id !== id) return;
      const changed = receipt.state !== pending.state;
      pending = receipt;
      if (changed) await onrequestchange?.();
    } catch (cause) { if (!next.signal.aborted) error = cause instanceof Error ? cause.message : "Request updates are unavailable."; }
    finally { polling = false; }
  }
  $effect(() => {
    identity; const shown = open;
    untrack(() => { if (shown) void load(); else { controller?.abort(); pollController?.abort(); } });
    return () => { controller?.abort(); pollController?.abort(); };
  });
  onMount(() => { const timer = setInterval(() => void pollRequest(), 2500); return () => { clearInterval(timer); pollController?.abort(); }; });
</script>

<LibraryPromotionPanel bind:open {preview} requestReceipt={pending} {providers} {title} {description} {cover} {loading} {error} {busy} {copyActions} onstart={start} oncancelrequest={cancelRequest} {onPrevious} {onNext} {onclose} />
