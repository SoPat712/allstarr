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
</script>

<section class="panel route-error" role="alert">
  <span aria-hidden="true">!</span>
  <div><p class="eyebrow">{eyebrow}</p><h2>{title}</h2><p>{message}</p></div>
  <button class="route-error-retry" type="button" onclick={() => void onRetry()}>Try again</button>
</section>
