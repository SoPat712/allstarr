<script lang="ts">
  import { AlertDialog } from "$lib/components/ui/alert-dialog";
  import { buttonVariants, type ButtonVariant } from "$lib/components/ui/button";

  let {
    open = $bindable(false),
    title,
    description,
    confirmLabel,
    cancelLabel = "Cancel",
    confirmVariant = "destructive",
    disabled = false,
    preventScroll = true,
    closeOnConfirm = true,
    error = "",
    onConfirm,
  }: {
    open: boolean;
    title: string;
    description: string;
    confirmLabel: string;
    cancelLabel?: string;
    confirmVariant?: ButtonVariant;
    disabled?: boolean;
    preventScroll?: boolean;
    closeOnConfirm?: boolean;
    error?: string;
    onConfirm: () => void | Promise<void>;
  } = $props();
</script>

<AlertDialog.Root bind:open>
  <AlertDialog.Portal>
    <AlertDialog.Overlay class="dialog-overlay confirm-dialog-overlay" />
    <AlertDialog.Content class="confirm-dialog" {preventScroll}>
      <AlertDialog.Title>{title}</AlertDialog.Title>
      <AlertDialog.Description>{description}</AlertDialog.Description>
      {#if error}<p class="notice-error" role="alert">{error}</p>{/if}
      <footer class="dialog-actions">
        <AlertDialog.Cancel class={buttonVariants({ variant: "secondary" })} {disabled}>{cancelLabel}</AlertDialog.Cancel>
        {#if closeOnConfirm}
          <AlertDialog.Action class={buttonVariants({ variant: confirmVariant })} {disabled} onclick={() => void onConfirm()}>{confirmLabel}</AlertDialog.Action>
        {:else}
          <button class={buttonVariants({ variant: confirmVariant })} {disabled} type="button" onclick={() => void onConfirm()}>{confirmLabel}</button>
        {/if}
      </footer>
    </AlertDialog.Content>
  </AlertDialog.Portal>
</AlertDialog.Root>
