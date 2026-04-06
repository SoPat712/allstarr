function toBoolean(value) {
  if (value === true || value === false) {
    return value;
  }
  const normalized = String(value ?? "")
    .trim()
    .toLowerCase();
  return normalized === "true" || normalized === "1" || normalized === "yes";
}

function toNumber(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function getActionArgs(el) {
  if (!el || !el.dataset) {
    return {};
  }

  // Convention:
  // - data-action="foo"
  // - data-arg-bar="baz"  => { bar: "baz" }
  const args = {};
  for (const [key, value] of Object.entries(el.dataset)) {
    if (!key.startsWith("arg")) continue;
    const argName = key.slice(3);
    if (!argName) continue;
    const normalized =
      argName.charAt(0).toLowerCase() + argName.slice(1);
    args[normalized] = value;
  }

  return args;
}

export function initActionDispatcher({ root = document } = {}) {
  const handlers = new Map();

  function register(actionName, handler) {
    if (!actionName || typeof handler !== "function") {
      return;
    }
    handlers.set(actionName, handler);
  }

  async function dispatch(actionName, el, event = null) {
    const handler = handlers.get(actionName);
    const args = getActionArgs(el);

    if (handler) {
      return await handler({ el, event, args, toBoolean, toNumber });
    }

    // Transitional fallback: if a legacy window function exists, call it.
    // This allows incremental conversion away from inline onclick.
    const legacy = typeof window !== "undefined" ? window[actionName] : null;
    if (typeof legacy === "function") {
      const legacyArgs = args && Object.keys(args).length > 0 ? [args] : [];
      return legacy(...legacyArgs);
    }

    console.warn(`No handler registered for action "${actionName}"`);
    return null;
  }

  function bind() {
    root.addEventListener("click", (event) => {
      const trigger = event.target?.closest?.("[data-action]");
      if (!trigger) return;

      const actionName = trigger.getAttribute("data-action") || "";
      if (!actionName) return;

      event.preventDefault();
      dispatch(actionName, trigger, event);
    });
  }

  bind();

  return { register, dispatch };
}

