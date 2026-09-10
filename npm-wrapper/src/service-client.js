import { normalizeTestRunOptions } from "./test-run-normalizer.js";

function trimTrailingSlash(value) {
  return value.replace(/\/+$/, "");
}

function serviceErrorMessage(payload, statusCode, fallback) {
  return payload?.error?.message ?? payload?.message ?? fallback ?? `sts2 service request failed with status ${statusCode}.`;
}

export class Sts2ServiceError extends Error {
  constructor({ statusCode, payload, url, method }) {
    super(serviceErrorMessage(payload, statusCode));
    this.name = "Sts2ServiceError";
    this.statusCode = statusCode;
    this.payload = payload;
    this.url = url;
    this.method = method;
  }
}

async function parseResponseBody(response) {
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("application/json")) {
    return response.json();
  }

  const text = await response.text();
  try {
    return JSON.parse(text);
  } catch {
    return { message: text };
  }
}

function buildHeaders(serviceToken, hasBody) {
  const headers = {};
  if (serviceToken) {
    headers.Authorization = `Bearer ${serviceToken}`;
  }
  if (hasBody) {
    headers["Content-Type"] = "application/json";
  }
  return headers;
}

function testRunBody(runOptions = {}) {
  const normalized = normalizeTestRunOptions(runOptions);
  const body = {
    profile: normalized.profile,
    tags: normalized.tags.length > 0 || normalized.tagsProvided ? normalized.tags : undefined,
    artifactsDir: normalized.artifactsDir,
    failureArtifacts: normalized.failureArtifacts,
    durable: normalized.durable,
  };

  if (!normalized.input) {
    return body;
  }

  body[normalized.input.kind] = normalized.input.value;
  return body;
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function assertRequiredString(value, methodName, fieldName) {
  if (value === undefined || value === null || value === "") {
    throw new TypeError(`${methodName} requires ${fieldName}.`);
  }
}

function testRunExitCode(summary) {
  return Number.isInteger(summary?.exitCode) ? summary.exitCode : 1;
}

function isTerminalServiceErrorStatus(status) {
  return ["failed", "canceled", "orphaned", "unknown"].includes(status?.status);
}

function statusErrorCode(status) {
  if (status?.status === "failed") {
    return 500;
  }
  if (status?.status === "canceled") {
    return 499;
  }
  return 500;
}

function encodeArtifactRelativePath(relativePath) {
  return String(relativePath)
    .split("/")
    .map((segment) => encodeURIComponent(segment))
    .join("/");
}

function normalizeServiceErrorPayload(payload) {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    return payload;
  }
  if (Number.isInteger(payload.exitCode) || !payload.error) {
    return payload;
  }
  return {
    exitCode: 1,
    ...payload,
  };
}

export function createServiceClient({ serviceUrl, serviceToken } = {}) {
  if (!serviceUrl) {
    throw new TypeError("createServiceClient requires serviceUrl.");
  }

  const baseUrl = trimTrailingSlash(String(serviceUrl));

  async function requestJson(path, { method = "GET", body } = {}) {
    const url = `${baseUrl}${path}`;
    const response = await fetch(url, {
      method,
      headers: buildHeaders(serviceToken, body !== undefined),
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    const payload = await parseResponseBody(response);
    if (!response.ok) {
      throw new Sts2ServiceError({
        statusCode: response.status,
        payload: normalizeServiceErrorPayload(payload),
        url,
        method,
      });
    }

    return payload;
  }

  async function requestArtifact(path) {
    const url = `${baseUrl}${path}`;
    const response = await fetch(url, {
      method: "GET",
      headers: buildHeaders(serviceToken, false),
    });

    const contentType = response.headers.get("content-type") ?? "application/octet-stream";
    if (!response.ok) {
      const payload = await parseResponseBody(response);
      throw new Sts2ServiceError({
        statusCode: response.status,
        payload,
        url,
        method: "GET",
      });
    }

    if (contentType.includes("application/json")) {
      return {
        contentType,
        body: await response.json(),
      };
    }

    if (contentType.startsWith("text/")) {
      return {
        contentType,
        body: await response.text(),
      };
    }

    return {
      contentType,
      body: new Uint8Array(await response.arrayBuffer()),
    };
  }

  async function waitForTestRun(runId) {
    assertRequiredString(runId, "waitForTestRun", "runId");
    const encodedRunId = encodeURIComponent(String(runId));
    for (;;) {
      const status = await requestJson(`/v0/test-runs/${encodedRunId}`);
      if (status.status === "completed") {
        return status;
      }
      if (isTerminalServiceErrorStatus(status)) {
        throw new Sts2ServiceError({
          statusCode: statusErrorCode(status),
          payload: status,
          url: `${baseUrl}/v0/test-runs/${encodedRunId}`,
          method: "GET",
        });
      }
      await delay(50);
    }
  }

  return {
    async serviceInfo() {
      return requestJson("/v0/service");
    },

    async inspectAiTools() {
      return requestJson("/v0/ai-tools");
    },

    async callTool(name, args = {}) {
      return requestJson("/v0/tools/call", {
        method: "POST",
        body: {
          name,
          arguments: args,
        },
      });
    },

    async debugSessionStart(options = {}) {
      return requestJson("/v0/debug-sessions", {
        method: "POST",
        body: {
          name: options.name,
          role: options.role,
          pause: options.pause,
          leaseTimeoutMs: options.leaseTimeoutMs,
        },
      });
    },

    async debugSessionStatus(sessionId) {
      assertRequiredString(sessionId, "debugSessionStatus", "sessionId");
      return requestJson(`/v0/debug-sessions/${encodeURIComponent(String(sessionId))}`);
    },

    async debugSessionEnd(sessionId, options = {}) {
      assertRequiredString(sessionId, "debugSessionEnd", "sessionId");
      const suffix = options.resume ? "?resume=true" : "";
      return requestJson(`/v0/debug-sessions/${encodeURIComponent(String(sessionId))}${suffix}`, {
        method: "DELETE",
      });
    },

    async debugWait(sessionId, options = {}) {
      assertRequiredString(sessionId, "debugWait", "sessionId");
      return requestJson(`/v0/debug-sessions/${encodeURIComponent(String(sessionId))}/wait`, {
        method: "POST",
        body: {
          timeoutMs: options.timeoutMs,
        },
      });
    },

    async debugEvents(sessionId, options = {}) {
      assertRequiredString(sessionId, "debugEvents", "sessionId");
      return requestJson(`/v0/debug-sessions/${encodeURIComponent(String(sessionId))}/events`, {
        method: "POST",
        body: {
          fromSequence: options.fromSequence,
          limit: options.limit,
          follow: options.follow,
          timeoutMs: options.timeoutMs,
        },
      });
    },

    async submitTestRun(runOptions = {}) {
      return requestJson("/v0/test-runs", {
        method: "POST",
        body: testRunBody(runOptions),
      });
    },

    waitForTestRun,

    async listTestRunArtifacts(runId) {
      assertRequiredString(runId, "listTestRunArtifacts", "runId");
      return requestJson(`/v0/test-runs/${encodeURIComponent(String(runId))}/artifacts`);
    },

    async downloadTestRunArtifact(runId, relativePath) {
      assertRequiredString(runId, "downloadTestRunArtifact", "runId");
      assertRequiredString(relativePath, "downloadTestRunArtifact", "relativePath");
      return requestArtifact(
        `/v0/test-runs/${encodeURIComponent(String(runId))}/artifacts/${encodeArtifactRelativePath(relativePath)}`,
      );
    },

    async testRun(runOptions = {}) {
      const submitted = await requestJson("/v0/test-runs", {
        method: "POST",
        body: testRunBody(runOptions),
      });
      const status = await waitForTestRun(submitted.runId);
      if (testRunExitCode(status.summary) !== 0) {
        throw new Sts2ServiceError({
          statusCode: 400,
          payload: status.summary,
          url: `${baseUrl}/v0/test-runs/${submitted.runId}`,
          method: "GET",
        });
      }
      return status.summary;
    },
  };
}
