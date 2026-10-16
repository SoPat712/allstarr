export type ActivityScope = "mine" | "everyone";
export const activityScopeKey = "allstarr.activity.scope";

export function readActivityScope(administrator: boolean): ActivityScope {
  if (!administrator) return "mine";
  try { return localStorage.getItem(activityScopeKey) === "everyone" ? "everyone" : "mine"; }
  catch { return "mine"; }
}

export function rememberActivityScope(scope: ActivityScope) {
  try { localStorage.setItem(activityScopeKey, scope); } catch { /* The current choice still works without storage. */ }
}
