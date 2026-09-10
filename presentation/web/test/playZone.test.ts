// Pins the play-zone line a dragged card is measured against. The predicate lives in a leaf
// module because two independent hosts evaluate it: this package's own targeting layer, and an
// imperative browser host (the CouchCoop mirror) that owns its own pointer handling. Both must
// draw the SAME line, so the numbers are locked here rather than left to each caller.
//
// Contract: the drag is in the play zone while `pointerY < playZoneThreshold(...)`, with Y in
// 1080-design px measured DOWN from the top of the play area.
import { describe, expect, it } from "vitest";

import { playZoneThreshold } from "../src/render/playZone";

const H = 1080;
const BASE = H * 0.75; // 810

describe("playZoneThreshold", () => {
  it("is three quarters down the play area for a grab that sits exactly on the base line", () => {
    // dragStartY === base takes the "started at/above the line" arm: the line is pulled 50px
    // above the grab point, so even this drag has to travel before it counts as played.
    expect(playZoneThreshold(H, BASE)).toBe(760);
  });

  it("loosens by 100px when there is no grab point", () => {
    expect(playZoneThreshold(H, null)).toBe(BASE + 100);
    expect(playZoneThreshold(H, null)).toBe(910);
    // A no-grab start is looser than every grab-anchored line.
    expect(playZoneThreshold(H, null)).toBeGreaterThan(playZoneThreshold(H, BASE));
  });

  it("only asks a card grabbed low in the hand to rise 100px", () => {
    // Grab at 1000 (below the base line) -> the line follows the grab down to 100px above it.
    expect(playZoneThreshold(H, 1000)).toBe(900);
    // ...but never rises past the base line: a grab just below it still has to reach the base.
    expect(playZoneThreshold(H, 850)).toBe(BASE);
  });

  it("still demands 50px of lift from a grab that started above the base line", () => {
    expect(playZoneThreshold(H, 400)).toBe(350);
    expect(playZoneThreshold(H, 700)).toBe(650);
    // The line is never pushed BELOW the base by a high grab.
    expect(playZoneThreshold(H, 700)).toBeLessThan(BASE);
  });

  it("scales the base line with the viewport height", () => {
    expect(playZoneThreshold(1200, null)).toBe(1000); // 900 base + 100
    expect(playZoneThreshold(1200, 1150)).toBe(1050); // max(900, 1150 - 100)
    expect(playZoneThreshold(720, 300)).toBe(250); // min(540, 300 - 50)
  });

  it("reads as a predicate: the pointer is in the play zone while it is ABOVE the line", () => {
    const inZone = (pointerY: number, dragStartY: number | null) =>
      pointerY < playZoneThreshold(H, dragStartY);
    // Card grabbed at the bottom of the hand, dragged 120px up -> played.
    expect(inZone(880, 1000)).toBe(true);
    // Same grab, barely moved -> not yet.
    expect(inZone(980, 1000)).toBe(false);
    // Grab already high on the board still needs its 50px of lift.
    expect(inZone(399, 400)).toBe(false);
    expect(inZone(340, 400)).toBe(true);
  });
});
