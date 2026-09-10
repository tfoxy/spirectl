// The play-zone line for a dragged card, in 1080-design px. A LEAF module: no imports, so an
// imperative host (the CouchCoop mirror) can share the predicate without pulling in the DOM
// targeting layer.

// Base line: a card is "aimed at the board" once the pointer is in the upper three quarters
// of the play area.
const PLAY_ZONE_BASE_RATIO = 0.75;
// How far above a LOW grab point the line may be dragged down to, so a card picked up at the
// very bottom of the hand does not have to travel all the way to the base line to be played.
const DRAG_UP_TIGHTEN = 100;
// Minimum upward travel demanded of a grab that already started above the base line, so every
// drag still needs a deliberate lift instead of playing on the first pixel of movement.
const DRAG_DOWN_TIGHTEN = 50;
// Extra slack when there is no grab point at all (see `playZoneThreshold`).
const SHORTCUT_LOOSEN = 100;

/**
 * The Y (design px, measured down from the top of the play area) above which a dragged card
 * counts as being played: the drag is in the play zone while `pointerY < threshold`.
 *
 * The line sits at `PLAY_ZONE_BASE_RATIO` of the viewport height and is then nudged relative to
 * where the drag started, so the gesture always reads as a deliberate lift: a card grabbed below
 * the line only has to rise `DRAG_UP_TIGHTEN` px, and a card grabbed above it still has to rise
 * `DRAG_DOWN_TIGHTEN` px. `dragStartY` null = a start with no grab point (e.g. a card that was
 * already selected when the drag surface adopted it), which loosens the zone instead.
 *
 * Exported so an imperative host (the CouchCoop mirror) reproduces the same predicate from the
 * pointer Y — reused, not duplicated.
 */
export function playZoneThreshold(viewportHeight: number, dragStartY: number | null): number {
  const base = viewportHeight * PLAY_ZONE_BASE_RATIO;
  if (dragStartY === null) return base + SHORTCUT_LOOSEN;
  if (dragStartY > base) return Math.max(base, dragStartY - DRAG_UP_TIGHTEN);
  return Math.min(base, dragStartY - DRAG_DOWN_TIGHTEN);
}
