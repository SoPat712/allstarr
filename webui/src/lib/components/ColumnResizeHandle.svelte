<script lang="ts">
  import { resizedColumnWidth } from "$lib/playlists";

  let {
    value = $bindable(0),
    label,
    min,
    max,
  }: {
    value: number;
    label: string;
    min: number;
    max: number;
  } = $props();

  let active = $state(false);
  let startX = 0;
  let startValue = 0;

  function currentWidth(element: HTMLElement) {
    return value || element.parentElement?.getBoundingClientRect().width || min;
  }

  function begin(event: PointerEvent) {
    active = true;
    startX = event.clientX;
    startValue = currentWidth(event.currentTarget as HTMLElement);
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  }

  function move(event: PointerEvent) {
    if (active) value = resizedColumnWidth(startValue, event.clientX - startX, min, max);
  }

  function end() {
    active = false;
  }

  function keydown(event: KeyboardEvent) {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    value = resizedColumnWidth(
      currentWidth(event.currentTarget as HTMLElement),
      event.key === "ArrowLeft" ? -16 : 16,
      min,
      max,
    );
  }
</script>

<button
  type="button"
  class:active
  class="column-resize-handle"
  aria-label={`Resize ${label} column`}
  title={`Resize ${label} column`}
  onpointerdown={begin}
  onpointermove={move}
  onpointerup={end}
  onpointercancel={end}
  onkeydown={keydown}
></button>
