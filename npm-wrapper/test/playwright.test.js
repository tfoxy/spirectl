import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";

import { hotReloadAndWait, withSts2 } from "../src/playwright.js";

function makeTempDir() {
  const dir = mkdtempSync(join(tmpdir(), "sts2-playwright-"));
  return {
    dir,
    cleanup() {
      rmSync(dir, { recursive: true, force: true });
    },
  };
}

function writeFixtureBinary(root, options = {}) {
  const binaryPath = join(root, "fake-sts2-playwright.mjs");
  writeFileSync(
    binaryPath,
    `#!/usr/bin/env node
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

const argv = process.argv.slice(2);
const command = argv.slice(1);

if (command[0] === "game" && command[1] === "info") {
  process.stdout.write(JSON.stringify({ argv, repository: "spirectl" }) + "\\n");
  process.exit(0);
}

if (command[0] === "state") {
  process.stdout.write(JSON.stringify({ screen: { type: "combat" }, source: "fake-state" }) + "\\n");
  process.exit(0);
}

if (command[0] === "dev" && command[1] === "logs") {
  process.stdout.write(JSON.stringify({ nextCursor: 42, entries: [{ target: "fake", message: argv.join(" ") }] }) + "\\n");
  process.exit(0);
}

if (command[0] === "dev" && command[1] === "diagnostics") {
  const bundleIndex = argv.indexOf("--bundle-dir");
  const bundleDir = bundleIndex >= 0 ? argv[bundleIndex + 1] : null;
  if (!bundleDir) {
    process.stderr.write("missing --bundle-dir\\n");
    process.exit(2);
  }
  if (${options.failDiagnostics === true}) {
    process.stderr.write("diagnostics failed\\n");
    process.exit(5);
  }
  mkdirSync(bundleDir, { recursive: true });
  const diagnosticsPath = join(bundleDir, "diagnostics.json");
  const statePath = join(bundleDir, "state.json");
  const logsPath = join(bundleDir, "logs.json");
  const runtimePath = join(bundleDir, "runtime.png");
  writeFileSync(statePath, JSON.stringify({ screen: { type: "combat" }, source: "fake-state" }) + "\\n");
  writeFileSync(logsPath, JSON.stringify({ nextCursor: 42, entries: [{ target: "fake", message: argv.join(" ") }] }) + "\\n");
  writeFileSync(runtimePath, "fake-png");
  const payload = {
    status: "partial",
    argv,
    captures: {
      state: { status: "captured", path: statePath },
      logs: { status: "captured", path: logsPath },
      screenshot: { status: "captured", path: runtimePath },
      sceneTree: { status: "error", error: { code: "not_implemented", message: "scene tree unavailable" } },
    },
    bundle: {
      dir: bundleDir,
      files: {
        diagnostics: diagnosticsPath,
        state: statePath,
        logs: logsPath,
        screenshot: runtimePath,
      },
    },
  };
  writeFileSync(diagnosticsPath, JSON.stringify(payload) + "\\n");
  process.stdout.write(JSON.stringify(payload) + "\\n");
  process.exit(0);
}

if (command[0] === "dev" && command[1] === "screenshot") {
  const outputIndex = argv.indexOf("--output");
  const outputPath = outputIndex >= 0 ? argv[outputIndex + 1] : null;
  if (!outputPath) {
    process.stderr.write("missing --output\\n");
    process.exit(2);
  }
  if (${options.failScreenshot === true}) {
    process.stderr.write("screenshot failed\\n");
    process.exit(5);
  }
  mkdirSync(dirname(outputPath), { recursive: true });
  writeFileSync(outputPath, "fake-png");
  process.stdout.write(JSON.stringify({ outputPath }) + "\\n");
  process.exit(0);
}

process.stderr.write("unsupported argv: " + argv.join(" ") + "\\n");
process.exit(64);
`,
    { mode: 0o755 },
  );
  return binaryPath;
}

function makeBase() {
  return {
    extend(fixtures) {
      return fixtures;
    },
  };
}

function makeTestInfo(outputDir, status = "passed", expectedStatus = "passed") {
  return {
    status,
    expectedStatus,
    outputPath(name) {
      return join(outputDir, name);
    },
  };
}

test("withSts2 exposes an sts2 fixture backed by createSts2Client options", async () => {
  const temp = makeTempDir();

  try {
    const binaryPath = writeFixtureBinary(temp.dir);
    const fixtures = withSts2(makeBase(), {
      client: {
        binaryPath,
        cwd: temp.dir,
      },
    });

    await fixtures.sts2({}, async (sts2) => {
      const info = await sts2.gameInfo();
      assert.deepEqual(info.argv, ["--json", "game", "info"]);
      assert.equal(info.repository, "spirectl");
    }, makeTestInfo(temp.dir));
  } finally {
    temp.cleanup();
  }
});

test("hotReloadAndWait builds reloads and asserts generation change", async () => {
  const calls = [];
  const sts2 = {
    async hotReloadStatus(options) {
      calls.push(["status", options]);
      return { shell: { activeGeneration: calls.length === 1 ? 3 : 4 } };
    },
    async hotReload(options) {
      calls.push(["reload", options]);
      return { status: "loaded", reload: { generation: 4 }, shell: { activeGeneration: 4 } };
    },
  };

  const result = await hotReloadAndWait(sts2, {
    project: "./mods/MyHotMod",
    build: true,
    timeoutMs: 30_000,
  });

  assert.equal(result.generationChanged, true);
  assert.deepEqual(calls[1], [
    "reload",
    {
      project: "./mods/MyHotMod",
      build: true,
      wait: true,
      timeoutMs: 30_000,
    },
  ]);
});

test("withSts2 writes predictable diagnostics artifacts through testInfo.outputPath", async () => {
  const temp = makeTempDir();

  try {
    const binaryPath = writeFixtureBinary(temp.dir);
    const fixtures = withSts2(makeBase(), {
      client: {
        binaryPath,
        cwd: temp.dir,
      },
      failureArtifacts: {
        enabled: true,
        logs: {
          tail: 7,
        },
      },
    });

    await fixtures.sts2({}, async () => {}, makeTestInfo(temp.dir, "failed", "passed"));

    const diagnostics = JSON.parse(readFileSync(join(temp.dir, "sts2-diagnostics.json"), "utf8"));
    assert.equal(diagnostics.status, "partial");
    assert.equal(diagnostics.bundle.dir, join(temp.dir, "sts2-evidence"));
    assert.deepEqual(JSON.parse(readFileSync(join(temp.dir, "sts2-evidence", "state.json"), "utf8")), {
      screen: { type: "combat" },
      source: "fake-state",
    });

    const logs = JSON.parse(readFileSync(join(temp.dir, "sts2-evidence", "logs.json"), "utf8"));
    assert.equal(logs.nextCursor, 42);
    assert.match(logs.entries[0].message, /--tail 7/);
    assert.equal(readFileSync(join(temp.dir, "sts2-evidence", "runtime.png"), "utf8"), "fake-png");
  } finally {
    temp.cleanup();
  }
});

test("withSts2 forwards failure-artifact screenshot options into diagnostics capture", async () => {
  const temp = makeTempDir();

  try {
    const binaryPath = writeFixtureBinary(temp.dir);
    const fixtures = withSts2(makeBase(), {
      client: {
        binaryPath,
        cwd: temp.dir,
      },
      failureArtifacts: {
        enabled: true,
        screenshot: {
          preset: "desktop-1080p",
          presetCatalogs: ["tests/visual/sample-catalog.sts2.viewport-presets.yaml"],
        },
      },
    });

    await fixtures.sts2({}, async () => {}, makeTestInfo(temp.dir, "failed", "passed"));

    const diagnostics = JSON.parse(readFileSync(join(temp.dir, "sts2-diagnostics.json"), "utf8"));
    assert.match(diagnostics.argv.join(" "), /--preset desktop-1080p/);
    assert.match(
      diagnostics.argv.join(" "),
      /--preset-catalog tests\/visual\/sample-catalog\.sts2\.viewport-presets\.yaml/,
    );
  } finally {
    temp.cleanup();
  }
});

test("withSts2 skips failure-artifact capture when the test outcome matched expectations", async () => {
  const temp = makeTempDir();

  try {
    const binaryPath = writeFixtureBinary(temp.dir);
    const fixtures = withSts2(makeBase(), {
      client: {
        binaryPath,
        cwd: temp.dir,
      },
      failureArtifacts: {
        enabled: true,
      },
    });

    await fixtures.sts2({}, async () => {}, makeTestInfo(temp.dir, "passed", "passed"));

    assert.throws(() => readFileSync(join(temp.dir, "sts2-diagnostics.json"), "utf8"));
    assert.throws(() => readFileSync(join(temp.dir, "sts2-evidence", "runtime.png"), "utf8"));
  } finally {
    temp.cleanup();
  }
});

test("withSts2 keeps diagnostics capture best-effort when diagnostics collection fails", async () => {
  const temp = makeTempDir();
  const originalWarn = console.warn;
  const warnings = [];
  console.warn = (message) => {
    warnings.push(message);
  };

  try {
    const binaryPath = writeFixtureBinary(temp.dir, { failDiagnostics: true });
    const fixtures = withSts2(makeBase(), {
      client: {
        binaryPath,
        cwd: temp.dir,
      },
      failureArtifacts: {
        enabled: true,
        logs: {
          tail: 7,
        },
      },
    });

    await fixtures.sts2({}, async () => {}, makeTestInfo(temp.dir, "failed", "passed"));

    assert.throws(() => readFileSync(join(temp.dir, "sts2-diagnostics.json"), "utf8"));
    assert.throws(() => readFileSync(join(temp.dir, "sts2-evidence", "runtime.png"), "utf8"));
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /sts2-diagnostics\.json/);
    assert.match(warnings[0], /diagnostics failed/);
  } finally {
    console.warn = originalWarn;
    temp.cleanup();
  }
});
