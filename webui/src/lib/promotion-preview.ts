export function createNamingPreview<T>(options: {
  preview: (script: string, signal: AbortSignal) => Promise<T>;
  received: (result: T) => void;
  failed: (cause: unknown) => void;
}) {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;
  let revision = 0;
  function cancel() {
    clearTimeout(timer); controller?.abort(); revision++;
  }
  return {
    change(script: string) {
      cancel();
      const current = revision;
      timer = setTimeout(async () => {
        controller = new AbortController();
        try {
          const result = await options.preview(script, controller.signal);
          if (current === revision) options.received(result);
        } catch (cause) {
          if (current === revision) options.failed(cause);
        }
      }, 300);
    },
    cancel,
  };
}
