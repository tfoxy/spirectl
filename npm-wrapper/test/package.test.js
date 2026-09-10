import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDir = dirname(fileURLToPath(import.meta.url));
const packageRoot = resolve(testDir, "..");
const packageJson = JSON.parse(readFileSync(resolve(packageRoot, "package.json"), "utf8"));
const playwrightExample = readFileSync(resolve(packageRoot, "examples", "playwright-live-smoke.spec.ts"), "utf8");

test("package.json keeps the wrapper publishable for documented npx usage", () => {
  assert.notEqual(
    packageJson.private,
    true,
    'npm-wrapper/package.json must not stay "private": true when docs promise npx @spirectl/sts2.',
  );
  assert.ok(Array.isArray(packageJson.files), "npm-wrapper/package.json should declare a files allowlist.");
});

test("npm pack publishes JavaScript wrapper files without a staged native binary", () => {
  const packResult = execFileSync("npm", ["pack", "--dry-run", "--json"], {
    cwd: packageRoot,
    encoding: "utf8",
  });
  const [tarball] = JSON.parse(packResult);
  const packedPaths = tarball.files.map((entry) => entry.path).sort();

  assert.ok(packedPaths.includes("README.md"));
  assert.ok(packedPaths.includes("bin/sts2.js"));
  assert.ok(packedPaths.includes("src/index.js"));
  assert.ok(packedPaths.includes("src/playwright.js"));
  assert.ok(!packedPaths.some((path) => path.startsWith("dist/")), "published tarball must not contain a native binary.");
  assert.ok(!packedPaths.some((path) => path.startsWith("test/")), "published tarball should exclude tests.");
  assert.equal(packageJson.scripts?.["prepare-package"], undefined);
  assert.equal(packageJson.scripts?.prepack, undefined);
  assert.equal(packageJson.publishConfig?.provenance, true);
});

test("published Playwright example imports package entrypoints instead of private src paths", () => {
  assert.match(playwrightExample, /from "@spirectl\/sts2"/);
  assert.match(playwrightExample, /from "@spirectl\/sts2\/playwright"/);
  assert.doesNotMatch(playwrightExample, /\.\.\/src\//);
});
