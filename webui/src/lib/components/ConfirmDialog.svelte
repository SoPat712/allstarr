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
    onConfirm,
  }: {
    open: boolean;
    title: string;
    description: string;
    confirmLabel: string;
    cancelLabel?: string;
    confirmVariant?: ButtonVariant;
    disabled?: boolean;
    onConfirm: () => void | Promise<void>;
  } = $props();
</script>

<AlertDialog.Root bind:open>
  <AlertDialog.Portal>
    <AlertDialog.Overlay class="dialog-overlay" />
    <AlertDialog.Content class="confirm-dialog">
      <AlertDialog.Title>{title}</AlertDialog.Title>
      <AlertDialog.Description>{description}</AlertDialog.Description>
      <footer>
        <AlertDialog.Cancel class={buttonVariants({ variant: "secondary" })} {disabled}>{cancelLabel}</AlertDialog.Cancel>
        <AlertDialog.Action class={buttonVariants({ variant: confirmVariant })} {disabled} onclick={() => void onConfirm()}>{confirmLabel}</AlertDialog.Action>
      </footer>
    </AlertDialog.Content>
  </AlertDialog.Portal>
</AlertDialog.Root>
