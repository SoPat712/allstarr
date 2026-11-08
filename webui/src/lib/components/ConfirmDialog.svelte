<script lang="ts">
  import type { Snippet } from "svelte";
  import { AlertDialog } from "$lib/components/ui/alert-dialog";
  import { buttonVariants, type ButtonVariant } from "$lib/components/ui/button";

  let {
    open = $bindable(false),
    title,
    description,
    safe = "",
    confirmLabel,
    cancelLabel = "Cancel",
    confirmVariant = "destructive",
    disabled = false,
    confirmDisabled = false,
    preventScroll = true,
    closeOnConfirm = true,
    error = "",
    children,
    onConfirm,
  }: {
    open: boolean;
    title: string;
    description: string;
    // What the action leaves alone, shown in a green box.
    safe?: string;
    confirmLabel: string;
    cancelLabel?: string;
    confirmVariant?: ButtonVariant;
    disabled?: boolean;
    confirmDisabled?: boolean;
    preventScroll?: boolean;
    closeOnConfirm?: boolean;
    error?: string;
    children?: Snippet;
    onConfirm: () => void | Promise<void>;
  } = $props();
</script>

<AlertDialog.Root bind:open>
  <AlertDialog.Portal>
    <AlertDialog.Overlay class="dialog-overlay confirm-dialog-overlay" />
    <AlertDialog.Content class="confirm-dialog" {preventScroll}>
      <AlertDialog.Title>{title}</AlertDialog.Title>
      <AlertDialog.Description>{description}</AlertDialog.Description>
      {#if safe}<p class="notice-error notice-safe">✓ {safe}</p>{/if}
      {@render children?.()}
      {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
      <footer class="dialog-actions">
        <AlertDialog.Cancel type="button" class={buttonVariants({ variant: "text" })} {disabled}>{cancelLabel}</AlertDialog.Cancel>
        {#if closeOnConfirm}
          <AlertDialog.Action class={buttonVariants({ variant: confirmVariant })} disabled={disabled || confirmDisabled} onclick={() => void onConfirm()}>{confirmLabel}</AlertDialog.Action>
        {:else}
          <button class={buttonVariants({ variant: confirmVariant })} disabled={disabled || confirmDisabled} type="button" onclick={() => void onConfirm()}>{confirmLabel}</button>
        {/if}
      </footer>
    </AlertDialog.Content>
  </AlertDialog.Portal>
</AlertDialog.Root>
