import type { GodotBbcodeTagDescriptor } from "@godot-scene-web/html";

import {
  RICH_FX_JITTER,
  RICH_FX_SINE,
  RICH_FX_THINKY_DOTS,
} from "./richTextEffects";

// The STS2 custom BBCode tag table: color aliases (`[gold]`, `[red]`, …) and
// effect/style tags (`[sine]`, `[jitter]`, `[ancient_banner]`, …) that are NOT
// standard Godot BBCode. godot-scene-web's parser only formats a tag it finds a
// descriptor for here; without the table it leaves `[gold]…[/gold]` literal.
//
// This table is the authority: a host that lays out STS2 rich text passes it to the
// parser directly, so the same tag spelling formats the same way everywhere, with no
// second copy to keep in step.
//
// THE THREE LOOPING EFFECTS ARE REAL ANIMATIONS; THE THREE ONE-SHOTS ARE NOT.
// `sine` (wavy), `jitter` (shaky) and `thinky_dots` (a travelling hop) loop forever off nothing but
// elapsed time, so `richTextEffects.ts` reproduces them in CSS and they carry the class its rules key
// on. `ancient_banner`, `fade_in` and `fly_in` are REVEALS: each one's shape comes from values the
// owning scene pushes per instance (a slide offset, a ramp speed, a banner's arc), none of which a
// remote host receives — and a reveal that re-fires whenever the markup is re-emitted flashes the
// text. They stay `{kind:"style", css:{}}`: parsed and consumed, so the brackets never leak as
// literal text, and visually inert.
export const DEFAULT_BBCODE_TAGS: Record<string, GodotBbcodeTagDescriptor> =
  Object.fromEntries([
    ["aqua", { kind: "color", value: "#2aebbe" }],
    ["blue", { kind: "color", value: "#87ceeb" }],
    ["gold", { kind: "color", value: "#efc851" }],
    ["green", { kind: "color", value: "#7fff00" }],
    ["orange", { kind: "color", value: "#ffa518" }],
    ["pink", { kind: "color", value: "#ff78a0" }],
    ["purple", { kind: "color", value: "#ee82ee" }],
    ["red", { kind: "color", value: "#ff5555" }],
    ["yellow", { kind: "color", value: "#efc851" }],
    ["grey", { kind: "color", value: "#a9a9a9" }],
    ["gray", { kind: "color", value: "#a9a9a9" }],
    ["white", { kind: "color", value: "#fff6e2" }],
    ["black", { kind: "color", value: "#000000" }],
    ["sine", { kind: "effect", perChar: true, className: RICH_FX_SINE }],
    ["jitter", { kind: "effect", perChar: true, className: RICH_FX_JITTER }],
    [
      "thinky_dots",
      { kind: "effect", perChar: true, className: RICH_FX_THINKY_DOTS },
    ],
    ["ancient_banner", { kind: "style", css: {} }],
    ["fade_in", { kind: "style", css: {} }],
    ["fly_in", { kind: "style", css: {} }],
  ]) as Record<string, GodotBbcodeTagDescriptor>;
