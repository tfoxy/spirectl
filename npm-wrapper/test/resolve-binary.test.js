import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";

import {
  binaryNotFoundMessage,
  cacheBinaryPath,
  defaultCacheRoot,
  downloadSts2Binary,
  findExternalSts2,
  releaseAssetName,
  releaseManifestName,
  resolveOrDownloadSts2Binary,
  resolveSts2Binary,
} from "../src/resolve-binary.js";

function makeTempRoots() {
  const root = mkdtempSync(join(tmpdir(), "sts2-resolve-"));
  return {
    cacheRoot: join(root, "cache"), packageRoot: join(root, "package"), repoRoot: join(root, "repo"), root,
    cleanup() { rmSync(root, { recursive: true, force: true }); },
  };
}

function writeExecutable(path, contents = "#!/usr/bin/env node\nprocess.exit(0);\n") {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, contents);
  chmodSync(path, 0o755);
}

function binaryName() { return process.platform === "win32" ? "sts2.exe" : "sts2"; }
function response(body) { return new Response(body); }

test("resolver prefers explicit path, env, external CLI, cache, then repo-local", () => {
  const temp = makeTempRoots();
  try {
    const explicit = join(temp.root, "explicit", binaryName());
    const envBinary = join(temp.root, "env", binaryName());
    const externalDirectory = join(temp.root, "external");
    const external = join(externalDirectory, binaryName());
    const cached = cacheBinaryPath({ cacheRoot: temp.cacheRoot });
    const repoBinary = join(temp.repoRoot, "target", "debug", binaryName());
    [explicit, envBinary, external, cached, repoBinary].forEach((path) => writeExecutable(path));
    writeFileSync(`${cached}.sha256`, `${createHash("sha256").update(readFileSync(cached)).digest("hex")}\n`);
    const options = { env: { STS2_BINARY_PATH: envBinary, PATH: externalDirectory }, packageRoot: temp.packageRoot, repoRoot: temp.repoRoot, cacheRoot: temp.cacheRoot };
    assert.equal(resolveSts2Binary({ ...options, binaryPath: explicit }), explicit);
    assert.equal(resolveSts2Binary(options), envBinary);
    const noEnv = { ...options, env: { STS2_BINARY_PATH: "", PATH: externalDirectory } };
    assert.equal(resolveSts2Binary(noEnv), external);
    rmSync(external);
    assert.equal(resolveSts2Binary(noEnv), cached);
    rmSync(cached);
    assert.equal(resolveSts2Binary(noEnv), repoBinary);
  } finally { temp.cleanup(); }
});

test("PATH discovery excludes the wrapper's own npm shim", () => {
  const temp = makeTempRoots();
  try {
    const entrypoint = join(temp.packageRoot, "bin", "sts2.js");
    const shimDirectory = join(temp.root, "shim");
    const externalDirectory = join(temp.root, "external");
    writeExecutable(entrypoint);
    mkdirSync(shimDirectory, { recursive: true });
    symlinkSync(entrypoint, join(shimDirectory, "sts2"));
    const external = join(externalDirectory, "sts2");
    writeExecutable(external);
    assert.equal(findExternalSts2({ env: { PATH: [shimDirectory, externalDirectory].join(":"), PATHEXT: "" }, packageRoot: temp.packageRoot }), external);
  } finally { temp.cleanup(); }
});

test("release naming supports only published Linux and Windows targets", () => {
  assert.equal(releaseAssetName("0.1.0", "linux", "x64"), "sts2-v0.1.0-x86_64-unknown-linux-gnu");
  assert.equal(releaseAssetName("0.1.0", "win32", "x64"), "sts2-v0.1.0-x86_64-pc-windows-msvc.exe");
  assert.equal(releaseAssetName("0.1.0", "darwin", "arm64"), null);
  assert.equal(releaseManifestName("0.1.0"), "spirectl-v0.1.0-release-manifest.json");
});

test("download verifies the release manifest and atomically caches the raw binary", async () => {
  const temp = makeTempRoots();
  const bytes = Buffer.from("fake release executable");
  const sha256 = createHash("sha256").update(bytes).digest("hex");
  const asset = releaseAssetName("0.1.0", "linux", "x64");
  const calls = [];
  try {
    const downloaded = await downloadSts2Binary({
      version: "0.1.0", platform: "linux", arch: "x64", cacheRoot: temp.cacheRoot,
      manifestUrl: "https://example.test/manifest", assetUrl: "https://example.test/asset",
      fetch: async (url) => {
        calls.push(url);
        return url.endsWith("manifest")
          ? response(JSON.stringify({ schemaVersion: "spirectl-release/v1", version: "0.1.0", assets: [{ name: asset, sha256, size: bytes.length }] }))
          : response(bytes);
      },
    });
    assert.equal(downloaded, cacheBinaryPath({ version: "0.1.0", platform: "linux", arch: "x64", cacheRoot: temp.cacheRoot }));
    assert.deepEqual(readFileSync(downloaded), bytes);
    assert.deepEqual(calls, ["https://example.test/manifest", "https://example.test/asset"]);
    const cached = await resolveOrDownloadSts2Binary({
      env: { PATH: "" }, version: "0.1.0", platform: "linux", arch: "x64", cacheRoot: temp.cacheRoot, repoRoot: temp.repoRoot,
      fetch: async () => { throw new Error("valid cache must avoid network"); },
    });
    assert.equal(cached, downloaded);
  } finally { temp.cleanup(); }
});

test("download errors identify unsupported platforms, network failures, and checksum mismatches", async () => {
  await assert.rejects(downloadSts2Binary({ platform: "darwin", arch: "arm64" }), /Linux x64 and Windows x64 only/);
  const temp = makeTempRoots();
  try {
    await assert.rejects(
      downloadSts2Binary({ version: "0.1.0", platform: "linux", arch: "x64", cacheRoot: temp.cacheRoot, fetch: async () => { throw new Error("offline"); } }),
      /failed to download the sts2 release manifest: offline/,
    );
    await assert.rejects(
      downloadSts2Binary({
        version: "0.1.0", platform: "linux", arch: "x64", cacheRoot: temp.cacheRoot,
        fetch: async (url) => url.includes("release-manifest")
          ? response(JSON.stringify({ schemaVersion: "spirectl-release/v1", version: "0.1.0", assets: [{ name: releaseAssetName("0.1.0", "linux", "x64"), sha256: "0".repeat(64) }] }))
          : response("not the expected executable"),
      }),
      /checksum mismatch/,
    );
  } finally { temp.cleanup(); }
});

test("cache locations honor STS2_CACHE_DIR and platform defaults", () => {
  assert.equal(defaultCacheRoot({ env: { STS2_CACHE_DIR: "/tmp/custom-cache" } }), "/tmp/custom-cache");
  assert.equal(defaultCacheRoot({ env: {}, platform: "linux", home: "/users/ada" }), "/users/ada/.cache/spirectl");
  assert.match(defaultCacheRoot({ env: { LOCALAPPDATA: "C:\\Users\\Ada\\AppData\\Local" }, platform: "win32" }), /spirectl$/);
});

test("binaryNotFoundMessage explains local and release-backed options", () => {
  assert.match(binaryNotFoundMessage(), /STS2_BINARY_PATH/);
  assert.match(binaryNotFoundMessage(), /GitHub Release/);
  assert.match(binaryNotFoundMessage(), /cargo build -p sts2/);
});
