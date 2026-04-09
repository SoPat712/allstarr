// Modal management
const modalState = new Map();
const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex="-1"])';

function getModal(id) {
  return document.getElementById(id);
}

function getFocusableElements(modal) {
  return Array.from(modal.querySelectorAll(FOCUSABLE_SELECTOR)).filter(
    (el) => !el.hasAttribute("disabled") && !el.getAttribute("aria-hidden"),
  );
}

function onModalKeyDown(event, modal) {
  if (event.key === "Escape") {
    event.preventDefault();
    closeModal(modal.id);
    return;
  }

  if (event.key !== "Tab") {
    return;
  }

  const focusable = getFocusableElements(modal);
  if (focusable.length === 0) {
    event.preventDefault();
    return;
  }

  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  const isShift = event.shiftKey;

  if (isShift && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!isShift && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}

export function openModal(id) {
  const modal = getModal(id);
  if (!modal) return;

  const modalContent = modal.querySelector(".modal-content");
  if (!modalContent) return;

  const previousActive = document.activeElement;
  modalState.set(id, { previousActive });

  modal.setAttribute("role", "dialog");
  modal.setAttribute("aria-modal", "true");
  modal.removeAttribute("aria-hidden");
  modal.classList.add("active");

  const keydownHandler = (event) => onModalKeyDown(event, modal);
  modalState.set(id, { previousActive, keydownHandler });
  modal.addEventListener("keydown", keydownHandler);

  const focusable = getFocusableElements(modalContent);
  if (focusable.length > 0) {
    focusable[0].focus();
  } else {
    modalContent.setAttribute("tabindex", "-1");
    modalContent.focus();
  }
}

export function closeModal(id) {
  const modal = getModal(id);
  if (!modal) return;

  modal.classList.remove("active");
  modal.setAttribute("aria-hidden", "true");

  const state = modalState.get(id);
  if (state?.keydownHandler) {
    modal.removeEventListener("keydown", state.keydownHandler);
  }

  if (state?.previousActive && typeof state.previousActive.focus === "function") {
    state.previousActive.focus();
  }

  modalState.delete(id);
}

export function setupModalBackdropClose() {
  document.querySelectorAll(".modal").forEach((modal) => {
    modal.setAttribute("aria-hidden", "true");
    modal.addEventListener("click", (e) => {
      if (e.target === modal) closeModal(modal.id);
    });
  });
}
