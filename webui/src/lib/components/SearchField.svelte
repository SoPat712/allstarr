<script lang="ts">
  let {
    value = $bindable(""),
    label,
    placeholder,
    hiddenLabel = false,
    class: className = "",
  }: {
    value: string;
    label: string;
    placeholder: string;
    hiddenLabel?: boolean;
    class?: string;
  } = $props();
</script>

<label class={`search-field ${className}`}>
  <span class:sr-only={hiddenLabel}>{label}</span>
  <span class="search-input">
    <svg aria-hidden="true" viewBox="0 0 16 16">
      <circle cx="7" cy="7" r="4.5"></circle>
      <path d="m10.5 10.5 3 3"></path>
    </svg>
    <input bind:value type="search" {placeholder} />
  </span>
</label>
