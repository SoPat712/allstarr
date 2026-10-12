<script lang="ts">
  import { Upload } from "@lucide/svelte";

  let {
    label,
    accept,
    multiple = false,
    disabled = false,
    buttonLabel = "Choose file",
    onselect,
  }: {
    label: string;
    accept?: string;
    multiple?: boolean;
    disabled?: boolean;
    buttonLabel?: string;
    onselect: (files: File[]) => void;
  } = $props();

  let names = $state("");
</script>

<label class="file-picker" class:disabled>
  <input
    class="sr-only"
    type="file"
    aria-label={label}
    {accept}
    {multiple}
    {disabled}
    onchange={(event) => {
      const files = [...(event.currentTarget.files ?? [])];
      names = files.map((file) => file.name).join(", ");
      onselect(files);
    }}
  />
  <span class="file-picker-button" aria-hidden="true"><Upload size={16} />{buttonLabel}</span>
  <span class="file-picker-name">{names || "No file selected"}</span>
</label>
