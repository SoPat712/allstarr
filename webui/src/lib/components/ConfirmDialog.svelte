<script lang="ts">
  import { AlertDialog } from "bits-ui";

  let {
    open = $bindable(false),
    title,
    description,
    confirmLabel,
    cancelLabel = "Cancel",
    confirmClass = "button-danger",
    disabled = false,
    onConfirm,
  }: {
    open: boolean;
    title: string;
    description: string;
    confirmLabel: string;
    cancelLabel?: string;
    confirmClass?: string;
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
        <AlertDialog.Cancel class="button-secondary" {disabled}>{cancelLabel}</AlertDialog.Cancel>
        <AlertDialog.Action class={confirmClass} {disabled} onclick={() => void onConfirm()}>{confirmLabel}</AlertDialog.Action>
      </footer>
    </AlertDialog.Content>
  </AlertDialog.Portal>
</AlertDialog.Root>
