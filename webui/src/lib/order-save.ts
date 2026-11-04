type PendingOrder = { order: string[]; previous: string[]; due: number };

export function createOrderSave(options: {
  save: (id: string, order: string[]) => Promise<void>;
  saved: (id: string, order: string[], previous: string[]) => void;
  failed: (id: string, cause: unknown) => void | Promise<void>;
}) {
  const pending = new Map<string, PendingOrder>();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let active = false;
  const waiting: Array<() => void> = [];

  function schedule() {
    clearTimeout(timer);
    if (active) return;
    if (!pending.size) { for (const resolve of waiting.splice(0)) resolve(); return; }
    const delay = Math.min(...[...pending.values()].map(item => item.due)) - Date.now();
    if (delay <= 0) void write();
    else timer = setTimeout(() => void write(), delay);
  }

  async function write() {
    if (active) return;
    const next = [...pending].find(([, item]) => item.due <= Date.now());
    if (!next) { schedule(); return; }
    const [id, item] = next;
    pending.delete(id);
    active = true;
    try {
      await options.save(id, item.order);
      const queued = pending.get(id);
      if (queued) queued.previous = item.order;
      options.saved(id, item.order, item.previous);
    } catch (cause) {
      pending.delete(id);
      await options.failed(id, cause);
    } finally {
      active = false;
      schedule();
    }
  }

  return {
    change(id: string, order: string[], previous: string[]) {
      pending.set(id, { order: [...order], previous: pending.get(id)?.previous ?? [...previous], due: Date.now() + 800 });
      schedule();
    },
    flush() {
      for (const item of pending.values()) item.due = 0;
      clearTimeout(timer);
      void write();
      return active || pending.size ? new Promise<void>(resolve => waiting.push(resolve)) : Promise.resolve();
    },
  };
}
