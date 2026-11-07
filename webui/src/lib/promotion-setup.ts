import { ApiError } from "./api-client";

export const promotionSetupHint = "Adding to your library isn't set up";
export function promotionPanelFailure(cause: unknown) {
  const setupRequired = cause instanceof ApiError && (cause.details as { reason?: string } | null)?.reason === "library-folder-not-set";
  return { setupRequired, error: setupRequired ? "" : cause instanceof Error ? cause.message : "The album preview could not be loaded." };
}
