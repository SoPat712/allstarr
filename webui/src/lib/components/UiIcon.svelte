<script lang="ts">
  let { name, size = 18 }: { name: string; size?: number } = $props();
</script>

<svg
  class="ui-icon"
  width={size}
  height={size}
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.8"
  stroke-linecap="round"
  stroke-linejoin="round"
  aria-hidden="true"
>
  <use href={`/ui-icons.svg#${name}`} />
</svg>
