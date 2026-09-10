import {
  accessSync,
  chmodSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { createHash, randomUUID } from "node:crypto";
import { homedir } from "node:os";
import { delimiter, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const sourceDir = dirname(fileURLToPath(import.meta.url));
const packageRoot = resolve(sourceDir, "..");
const repoRoot = resolve(packageRoot, "..");
const packageMetadata = JSON.parse(readFileSync(join(packageRoot, "package.json"), "utf8"));

export const RELEASE_REPOSITORY = "tfoxy/spirectl";
export const RELEASE_MANIFEST_SCHEMA = "spirectl-release/v1";

function localBinaryName(platform = process.platform) {
  return platform === "win32" ? "sts2.exe" : "sts2";
}

function isFile(path) {
  try {
    return statSync(path).isFile();
  } catch {
    return false;
  }
}

function isExecutable(path, platform = process.platform) {
  if (!isFile(path)) {
    return false;
  }

  try {
    accessSync(path, platform === "win32" ? undefined : 1);
    return true;
  } catch {
    return false;
  }
}

function sha256(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function isVerifiedCachedBinary(path, platform = process.platform) {
  if (!isExecutable(path, platform)) {
    return false;
  }
  try {
    const expected = readFileSync(`${path}.sha256`, "utf8").trim();
    return /^[a-f0-9]{64}$/.test(expected) && sha256(path) === expected;
  } catch {
    return false;
  }
}

function resolvedPath(path) {
  try {
    return realpathSync(path);
  } catch {
    return resolve(path);
  }
}

function isOwnShim(path, root = packageRoot) {
  const wrapperEntrypoint = resolvedPath(join(root, "bin", "sts2.js"));
  if (resolvedPath(path) === wrapperEntrypoint) {
    return true;
  }

  // On Windows npm creates a .cmd shim rather than a symlink. Finding the
  // wrapper entrypoint in it means it would recursively invoke this resolver.
  try {
    return readFileSync(path, "utf8").replaceAll("\\", "/").includes("bin/sts2.js");
  } catch {
    return false;
  }
}

function commandNames(platform = process.platform, env = process.env) {
  if (platform !== "win32") {
    return ["sts2"];
  }

  const extensions = (env.PATHEXT || ".COM;.EXE;.BAT;.CMD")
    .split(";")
    .filter(Boolean)
    .map((extension) => extension.toLowerCase());
  return ["sts2", ...extensions.map((extension) => `sts2${extension}`)];
}

export function binaryCandidates(root = repoRoot, platform = process.platform) {
  const binaryName = localBinaryName(platform);
  return [
    join(root, "target", "debug", binaryName),
    join(root, "target", "release", binaryName),
  ];
}

export function releaseAssetName(version = packageMetadata.version, platform = process.platform, arch = process.arch) {
  if (platform === "linux" && arch === "x64") {
    return `sts2-v${version}-x86_64-unknown-linux-gnu`;
  }
  if (platform === "win32" && arch === "x64") {
    return `sts2-v${version}-x86_64-pc-windows-msvc.exe`;
  }
  return null;
}

export function releaseManifestName(version = packageMetadata.version) {
  return `spirectl-v${version}-release-manifest.json`;
}

export function releaseUrl(name, version = packageMetadata.version, repository = RELEASE_REPOSITORY) {
  return `https://github.com/${repository}/releases/download/v${version}/${name}`;
}

export function defaultCacheRoot({ env = process.env, platform = process.platform, home = homedir() } = {}) {
  if (env.STS2_CACHE_DIR) {
    return resolve(env.STS2_CACHE_DIR);
  }
  if (platform === "win32") {
    return resolve(env.LOCALAPPDATA || join(home, "AppData", "Local"), "spirectl");
  }
  return resolve(env.XDG_CACHE_HOME || join(home, ".cache"), "spirectl");
}

export function cacheBinaryPath(options = {}) {
  const version = options.version ?? packageMetadata.version;
  const platform = options.platform ?? process.platform;
  const arch = options.arch ?? process.arch;
  const asset = releaseAssetName(version, platform, arch);
  if (!asset) {
    return null;
  }
  return join(options.cacheRoot ?? defaultCacheRoot(options), "sts2", version, `${platform}-${arch}`, asset);
}

export function findExternalSts2(options = {}) {
  const env = options.env ?? process.env;
  const platform = options.platform ?? process.platform;
  const pathValue = env.PATH ?? "";
  const root = options.packageRoot ?? packageRoot;

  for (const directory of pathValue.split(delimiter).filter(Boolean)) {
    for (const command of commandNames(platform, env)) {
      const candidate = join(directory, command);
      if (isExecutable(candidate, platform) && !isOwnShim(candidate, root)) {
        return candidate;
      }
    }
  }
  return null;
}

/** Finds only already-present binaries. Normal launches use resolveOrDownloadSts2Binary. */
export function resolveSts2Binary(options = {}) {
  if (typeof options === "string") {
    return options;
  }

  const explicitBinary = options.binaryPath;
  if (explicitBinary) {
    return explicitBinary;
  }

  const env = options.env ?? process.env;
  if (env.STS2_BINARY_PATH) {
    return env.STS2_BINARY_PATH;
  }

  const external = findExternalSts2(options);
  if (external) {
    return external;
  }

  const cached = cacheBinaryPath(options);
  if (cached && isVerifiedCachedBinary(cached, options.platform)) {
    return cached;
  }

  return binaryCandidates(options.repoRoot ?? repoRoot, options.platform).find((candidate) =>
    isExecutable(candidate, options.platform),
  ) ?? null;
}

function parseManifest(manifest, version, assetName) {
  if (manifest?.schemaVersion !== RELEASE_MANIFEST_SCHEMA) {
    throw new Error(`sts2 release manifest has unsupported schema '${manifest?.schemaVersion ?? "missing"}'.`);
  }
  if (manifest.version !== version) {
    throw new Error(`sts2 release manifest version '${manifest.version ?? "missing"}' does not match wrapper version '${version}'.`);
  }
  if (!Array.isArray(manifest.assets)) {
    throw new Error("sts2 release manifest does not contain an assets array.");
  }

  const asset = manifest.assets.find((entry) => entry?.name === assetName);
  if (!asset) {
    throw new Error(`sts2 release manifest does not contain '${assetName}'.`);
  }
  if (!/^[a-f0-9]{64}$/.test(asset.sha256 ?? "")) {
    throw new Error(`sts2 release manifest has an invalid SHA-256 for '${assetName}'.`);
  }
  return asset;
}

async function fetchOk(fetchImpl, url, description) {
  let response;
  try {
    response = await fetchImpl(url);
  } catch (error) {
    throw new Error(`failed to download ${description}: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (!response?.ok) {
    throw new Error(`failed to download ${description}: HTTP ${response?.status ?? "unknown"}.`);
  }
  return response;
}

function atomicWrite(path, bytes, platform = process.platform) {
  mkdirSync(dirname(path), { recursive: true });
  const temporary = join(dirname(path), `.${localBinaryName(platform)}-${process.pid}-${randomUUID()}.tmp`);
  try {
    writeFileSync(temporary, bytes, { mode: 0o755 });
    if (platform !== "win32") {
      chmodSync(temporary, 0o755);
    }
    renameSync(temporary, path);
  } finally {
    rmSync(temporary, { force: true });
  }
}

export async function downloadSts2Binary(options = {}) {
  const version = options.version ?? packageMetadata.version;
  const platform = options.platform ?? process.platform;
  const arch = options.arch ?? process.arch;
  const assetName = releaseAssetName(version, platform, arch);
  if (!assetName) {
    throw new Error(`@spirectl/sts2 supports Linux x64 and Windows x64 only (received ${platform} ${arch}).`);
  }

  const destination = cacheBinaryPath({ ...options, version, platform, arch });
  if (destination && isVerifiedCachedBinary(destination, platform)) {
    return destination;
  }

  const fetchImpl = options.fetch ?? globalThis.fetch;
  if (typeof fetchImpl !== "function") {
    throw new Error("cannot download sts2 because this Node runtime does not provide fetch.");
  }
  const repository = options.repository ?? RELEASE_REPOSITORY;
  const manifestResponse = await fetchOk(
    fetchImpl,
    options.manifestUrl ?? releaseUrl(releaseManifestName(version), version, repository),
    "the sts2 release manifest",
  );
  let manifest;
  try {
    manifest = await manifestResponse.json();
  } catch {
    throw new Error("sts2 release manifest is not valid JSON.");
  }
  const asset = parseManifest(manifest, version, assetName);
  const assetResponse = await fetchOk(
    fetchImpl,
    options.assetUrl ?? releaseUrl(assetName, version, repository),
    `'${assetName}'`,
  );
  const bytes = Buffer.from(await assetResponse.arrayBuffer());
  const actualDigest = createHash("sha256").update(bytes).digest("hex");
  if (actualDigest !== asset.sha256) {
    throw new Error(`sts2 download checksum mismatch for '${assetName}'. Expected ${asset.sha256}, got ${actualDigest}.`);
  }
  if (Number.isSafeInteger(asset.size) && asset.size !== bytes.length) {
    throw new Error(`sts2 download size mismatch for '${assetName}'. Expected ${asset.size} bytes, got ${bytes.length}.`);
  }
  atomicWrite(destination, bytes, platform);
  atomicWrite(`${destination}.sha256`, Buffer.from(`${actualDigest}\n`), platform);
  return destination;
}

export async function resolveOrDownloadSts2Binary(options = {}) {
  return resolveSts2Binary(options) ?? downloadSts2Binary(options);
}

export function binaryNotFoundMessage() {
  return "sts2 binary not found locally. Set `STS2_BINARY_PATH`, install a separate `sts2` CLI, build the local CLI with `mise exec -- cargo build -p sts2`, or allow @spirectl/sts2 to download its matching GitHub Release.";
}
