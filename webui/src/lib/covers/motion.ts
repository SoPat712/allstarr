export function shouldAnimate({ reduced, onScreen, pageVisible, liquid, engaged }: {
  reduced: boolean; onScreen: boolean; pageVisible: boolean; liquid: boolean; engaged: boolean;
}) {
  return !reduced && onScreen && pageVisible && (liquid || engaged);
}
