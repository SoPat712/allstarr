export type ProfileScope = "me" | "household";

export function createProfileSave<T>(options: {
  save: (scope: ProfileScope, value: T) => Promise<unknown>;
  saved: (scope: ProfileScope, value: T) => void;
  failed: (scope: ProfileScope, cause: unknown) => void;
}) {
  const pending = new Map<ProfileScope, { value: T; due: number }>();
  let timer: ReturnType<typeof setTimeout> | undefined, active = false;
  const waiting: Array<() => void> = [];
  function schedule() {
    clearTimeout(timer);
    if (active) return;
    if (!pending.size) { waiting.splice(0).forEach(resolve => resolve()); return; }
    const delay = Math.min(...[...pending.values()].map(item => item.due)) - Date.now();
    if (delay <= 0) void write();
    else timer = setTimeout(() => void write(), delay);
  }
  async function write() {
    if (active) return;
    const next = [...pending].find(([, item]) => item.due <= Date.now());
    if (!next) { schedule(); return; }
    const [scope, item] = next;
    pending.delete(scope); active = true;
    try { await options.save(scope, item.value); options.saved(scope, item.value); }
    catch (cause) { options.failed(scope, cause); }
    finally { active = false; schedule(); }
  }
  return {
    change(scope: ProfileScope, value: T) { pending.set(scope, { value, due: Date.now() + 500 }); schedule(); },
    discard(scope: ProfileScope) { pending.delete(scope); schedule(); },
    flush() {
      for (const item of pending.values()) item.due = 0;
      clearTimeout(timer); void write();
      return active || pending.size ? new Promise<void>(resolve => waiting.push(resolve)) : Promise.resolve();
    },
  };
}
