import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vitest/config";

// `bbcodeTags.ts` type-imports `@godot-scene-web/html`; alias it to the sibling-checkout
// TS source (no build step), exactly as the consuming hosts do. godot-scene-web is a
// sibling of the spirectl repo (../../../godot-scene-web from presentation/web).
const sibling = (p: string) =>
  fileURLToPath(new URL(`../../../godot-scene-web/${p}`, import.meta.url));

export default defineConfig({
  resolve: {
    alias: {
      "@godot-scene-web/effects/shaders": sibling("packages/effects/src/shaders/index.ts"),
      "@godot-scene-web/effects/particles": sibling("packages/effects/src/particles/index.ts"),
      "@godot-scene-web/effects/easing": sibling("packages/effects/src/easing/index.ts"),
      "@godot-scene-web/effects": sibling("packages/effects/src/index.ts"),
      "@godot-scene-web/html/runtime": sibling("packages/html/src/runtime.ts"),
      "@godot-scene-web/html": sibling("packages/html/src/index.ts"),
    },
  },
  test: {
    include: ["test/**/*.test.ts"],
  },
});
