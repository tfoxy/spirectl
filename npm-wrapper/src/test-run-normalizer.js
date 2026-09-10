function inputSourceEntries(runOptions) {
  return [
    ["path", runOptions.path],
    ["inline", runOptions.inline],
    ["scenario", runOptions.scenario],
  ].filter(([, value]) => value !== undefined && value !== null);
}

function normalizeInputSource(runOptions) {
  const entries = inputSourceEntries(runOptions);
  if (entries.length > 1 || (entries.length === 0 && !runOptions.profile)) {
    throw new TypeError("testRun requires exactly one input source: path, inline, scenario, or profile.");
  }

  if (entries.length === 0) {
    return undefined;
  }

  const [kind, value] = entries[0];
  if (kind === "path") {
    if (!value) {
      throw new TypeError("testRun requires path.");
    }
    return { kind, value: String(value) };
  }

  if (kind === "inline") {
    if (!value) {
      throw new TypeError("testRun requires inline.");
    }
    return { kind, value: String(value) };
  }

  if (typeof value !== "object") {
    throw new TypeError("testRun requires scenario to be a JSON-serializable object or array.");
  }

  const serialized = JSON.stringify(value);
  if (!serialized) {
    throw new TypeError("testRun requires scenario to be a JSON-serializable object or array.");
  }
  return { kind: "inline", value: serialized };
}

function normalizeTags(runOptions) {
  const tags = Array.isArray(runOptions.tags) ? runOptions.tags : [];
  const tag = runOptions.tag ? [runOptions.tag] : [];
  return [...tags, ...tag].filter((value) => value !== undefined && value !== null && value !== false);
}

// This module is intentionally not part of the package export surface. Both local CLI argv and
// service JSON serializers consume the same normalized input so their accepted testRun options stay aligned.
export function normalizeTestRunOptions(runOptions = {}) {
  return {
    input: normalizeInputSource(runOptions),
    profile: runOptions.profile,
    tags: normalizeTags(runOptions),
    tagsProvided: Array.isArray(runOptions.tags),
    artifactsDir: runOptions.artifactsDir,
    failureArtifacts: runOptions.failureArtifacts,
    durable: runOptions.durable,
  };
}
