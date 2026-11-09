import { promotionProfiles } from "./promotion-profile-api";

export function loadPromotionSettings(administrator: boolean) {
  return Promise.all([
    promotionProfiles.mine(), promotionProfiles.roots(),
    administrator ? promotionProfiles.household() : Promise.resolve(null),
    administrator ? promotionProfiles.setup() : Promise.resolve(null),
    administrator ? promotionProfiles.folders().catch(() => null) : Promise.resolve(null),
  ]);
}
