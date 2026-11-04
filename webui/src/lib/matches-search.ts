import { keyboardCommand } from "./matches-presentation";

let requested = false;

export function takeMatchSearchRequest() {
  const pending = requested;
  requested = false;
  return pending;
}

export function installMatchSearchShortcut() {
  const clear = () => { requested = false; };
  const request = (event: KeyboardEvent) => {
    if (!/^#\/library\/(?:matches|mappings)(?:[/?]|$)/.test(window.location.hash) ||
      keyboardCommand(event as unknown as Parameters<typeof keyboardCommand>[0]) !== "search" ||
      document.querySelector('[role="dialog"], [role="alertdialog"]')) return;
    event.preventDefault();
    requested = true;
    window.dispatchEvent(new Event("matches-search"));
  };
  window.addEventListener("keydown", request);
  window.addEventListener("hashchange", clear);
  return () => {
    window.removeEventListener("keydown", request);
    window.removeEventListener("hashchange", clear);
    clear();
  };
}
