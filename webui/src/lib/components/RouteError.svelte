<script lang="ts">
  let {
    eyebrow,
    title,
    message,
    onRetry,
  }: {
    eyebrow: string;
    title: string;
    message: string;
    onRetry: () => void | Promise<void>;
  } = $props();

  let retrying = $state(false);

  async function retry() {
    if (retrying) return;
    retrying = true;
    try {
      await onRetry();
    } finally {
      retrying = false;
    }
  }
</script>

<section class="panel route-error" role="alert">
  <span aria-hidden="true">!</span>
  <div><p class="eyebrow">{eyebrow}</p><h2>{title}</h2><p>{message}</p></div>
  <button class="route-error-retry" type="button" disabled={retrying} onclick={() => void retry()}>{retrying ? "Trying again…" : "Try again"}</button>
</section>
