import assert from "node:assert/strict";
import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { execFileSync, spawn } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

import { createSts2Client } from "../src/index.js";

const testDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(testDir, "..", "..");
const mcpBinPath = resolve(repoRoot, "npm-wrapper", "bin", "sts2-mcp.js");
const fakeBinaryPath = resolve(testDir, "fixtures", "fake-sts2.mjs");
const realBinaryPath = resolve(repoRoot, "target", "debug", process.platform === "win32" ? "sts2.exe" : "sts2");
const mockConfigPath = resolve(repoRoot, "tests", "sts2.mock.yaml");
const fixtureProject = resolve(
  repoRoot,
  "dotnet-tools",
  "tests",
  "Spirectl.DotnetTools.TestSymbols",
  "Spirectl.DotnetTools.TestSymbols.csproj",
);
const fixtureAssemblyDir = resolve(
  repoRoot,
  "dotnet-tools",
  "tests",
  "Spirectl.DotnetTools.TestSymbols",
  "bin",
  "Debug",
  "net9.0",
);
const fixtureResourcesDir = resolve(
  repoRoot,
  "dotnet-tools",
  "tests",
  "Spirectl.DotnetTools.TestSymbols",
  "Fixtures",
);

function writeConfig(contents) {
  const dir = mkdtempSync(join(tmpdir(), "sts2-mcp-"));
  const configPath = join(dir, "sts2.config.yaml");
  writeFileSync(configPath, contents);
  return {
    configPath,
    cleanup() {
      rmSync(dir, { recursive: true, force: true });
    },
  };
}

function parseTextContent(result) {
  const text = result.content?.find((item) => item.type === "text")?.text;
  return text ? JSON.parse(text) : null;
}

function assertAiToolCatalogBoundary(names) {
  assert.ok(names.includes("test_run"));
  for (const omitted of ["mouse-click", "game kill", "dev scene tree", "dev scene node", "dev scene children", "dev scene set-visible"]) {
    assert.equal(names.includes(omitted), false, `${omitted} must not be exposed as an MCP tool`);
  }
}

function ensureSts2Built() {
  if (!existsSync(realBinaryPath)) {
    execFileSync("cargo", ["build", "-p", "sts2"], {
      cwd: repoRoot,
      stdio: "inherit",
    });
  }
}

function spawnMcpServer({ configPath, env = {}, args = [] } = {}) {
  ensureSts2Built();

  const child = spawn("node", [mcpBinPath, ...args], {
    cwd: repoRoot,
    env: {
      ...process.env,
      STS2_CWD: repoRoot,
      STS2_BINARY_PATH: realBinaryPath,
      ...(configPath ? { STS2_CONFIG_PATH: configPath } : {}),
      ...env,
    },
    stdio: ["pipe", "pipe", "pipe"],
  });

  let nextId = 1;
  let stdoutBuffer = "";
  let stderr = "";
  const pending = new Map();

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");

  child.stdout.on("data", (chunk) => {
    stdoutBuffer += chunk;

    while (stdoutBuffer.includes("\n")) {
      const newlineIndex = stdoutBuffer.indexOf("\n");
      const line = stdoutBuffer.slice(0, newlineIndex).trim();
      stdoutBuffer = stdoutBuffer.slice(newlineIndex + 1);

      if (!line) {
        continue;
      }

      const message = JSON.parse(line);
      const deferred = pending.get(message.id);
      if (deferred) {
        pending.delete(message.id);
        deferred.resolve(message);
      }
    }
  });

  child.stderr.on("data", (chunk) => {
    stderr += chunk;
  });

  child.on("exit", (code) => {
    for (const deferred of pending.values()) {
      deferred.reject(new Error(`sts2-mcp exited with code ${code ?? 1}: ${stderr}`));
    }
    pending.clear();
  });

  function notify(method, params) {
    child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", method, params })}\n`);
  }

  function request(method, params = {}) {
    const id = nextId++;
    child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id, method, params })}\n`);
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
    });
  }

  return {
    child,
    request,
    notify,
    async initialize() {
      const response = await request("initialize", {
        protocolVersion: "2025-03-26",
        capabilities: {},
        clientInfo: {
          name: "npm-wrapper-test",
          version: "1.0.0",
        },
      });
      notify("notifications/initialized");
      return response;
    },
    async close() {
      child.stdin.end();
      await new Promise((resolve) => {
        child.once("exit", resolve);
        setTimeout(() => {
          child.kill("SIGTERM");
          resolve();
        }, 1_000).unref();
      });
    },
  };
}

async function spawnMcpHttpServer({ configPath, env = {}, args = [] } = {}) {
  ensureSts2Built();
  const child = spawn("node", [
    mcpBinPath,
    "--transport",
    "http",
    "--listen",
    "127.0.0.1:0",
    ...args,
  ], {
    cwd: repoRoot,
    env: {
      ...process.env,
      STS2_CWD: repoRoot,
      STS2_BINARY_PATH: realBinaryPath,
      ...(configPath ? { STS2_CONFIG_PATH: configPath } : {}),
      ...env,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");

  let stdoutBuffer = "";
  let stderr = "";
  const metadata = await new Promise((resolveStartup, rejectStartup) => {
    const timeout = setTimeout(() => {
      rejectStartup(new Error(`Timed out waiting for MCP HTTP startup. stderr: ${stderr}`));
    }, 5_000);

    child.stdout.on("data", (chunk) => {
      stdoutBuffer += chunk;
      const newlineIndex = stdoutBuffer.indexOf("\n");
      if (newlineIndex === -1) {
        return;
      }
      clearTimeout(timeout);
      resolveStartup(JSON.parse(stdoutBuffer.slice(0, newlineIndex).trim()));
    });

    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });

    child.once("exit", (code) => {
      clearTimeout(timeout);
      rejectStartup(new Error(`sts2-mcp http exited with code ${code ?? 1}: ${stderr}`));
    });
  });

  return {
    child,
    metadata,
    stderr: () => stderr,
    async close() {
      child.kill("SIGTERM");
      await new Promise((resolveClose) => {
        child.once("exit", resolveClose);
        setTimeout(() => {
          child.kill("SIGKILL");
          resolveClose();
        }, 1_000).unref();
      });
    },
  };
}

async function spawnMcpHttpServerFromEnv({ configPath, env = {} } = {}) {
  ensureSts2Built();
  const child = spawn("node", [mcpBinPath], {
    cwd: repoRoot,
    env: {
      ...process.env,
      STS2_CWD: repoRoot,
      STS2_BINARY_PATH: realBinaryPath,
      STS2_MCP_TRANSPORT: "http",
      STS2_MCP_LISTEN: "127.0.0.1:0",
      ...(configPath ? { STS2_CONFIG_PATH: configPath } : {}),
      ...env,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");

  let stdoutBuffer = "";
  let stderr = "";
  const metadata = await new Promise((resolveStartup, rejectStartup) => {
    const timeout = setTimeout(() => {
      rejectStartup(new Error(`Timed out waiting for MCP HTTP env startup. stderr: ${stderr}`));
    }, 5_000);

    child.stdout.on("data", (chunk) => {
      stdoutBuffer += chunk;
      const newlineIndex = stdoutBuffer.indexOf("\n");
      if (newlineIndex === -1) {
        return;
      }
      clearTimeout(timeout);
      resolveStartup(JSON.parse(stdoutBuffer.slice(0, newlineIndex).trim()));
    });

    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });

    child.once("exit", (code) => {
      clearTimeout(timeout);
      rejectStartup(new Error(`sts2-mcp env http exited with code ${code ?? 1}: ${stderr}`));
    });
  });

  return {
    child,
    metadata,
    async close() {
      child.kill("SIGTERM");
      await new Promise((resolveClose) => {
        child.once("exit", resolveClose);
        setTimeout(() => {
          child.kill("SIGKILL");
          resolveClose();
        }, 1_000).unref();
      });
    },
  };
}

async function expectMcpHttpStartupFailure(args, expectedMessage) {
  ensureSts2Built();
  const child = spawn("node", [mcpBinPath, "--transport", "http", ...args], {
    cwd: repoRoot,
    env: {
      ...process.env,
      STS2_CWD: repoRoot,
      STS2_BINARY_PATH: realBinaryPath,
      STS2_CONFIG_PATH: mockConfigPath,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  child.stderr.setEncoding("utf8");
  let stderr = "";
  child.stderr.on("data", (chunk) => {
    stderr += chunk;
  });

  const code = await new Promise((resolveExit) => child.once("exit", resolveExit));
  assert.notEqual(code, 0);
  assert.match(stderr, expectedMessage);
}

async function connectMcpHttpClient(mcpUrl, authToken) {
  const client = new Client({
    name: "npm-wrapper-http-test",
    version: "1.0.0",
  });
  const transport = new StreamableHTTPClientTransport(new URL(mcpUrl), {
    requestInit: authToken
      ? {
          headers: {
            authorization: `Bearer ${authToken}`,
          },
        }
      : undefined,
  });
  await client.connect(transport);
  return {
    client,
    transport,
    async close() {
      await transport.terminateSession().catch(() => {});
      await client.close();
    },
  };
}

async function spawnAutomationService() {
  const token = "local-dev-token";
  const child = spawn(realBinaryPath, [
    "--config",
    mockConfigPath,
    "--json",
    "service",
    "serve",
    "--listen",
    "127.0.0.1:0",
    "--auth-token",
    token,
    "--job-store",
    "durable",
  ], {
    cwd: repoRoot,
    stdio: ["ignore", "pipe", "pipe"],
  });

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");

  let stdoutBuffer = "";
  let stderr = "";

  const startup = await new Promise((resolveStartup, rejectStartup) => {
    const timeout = setTimeout(() => {
      rejectStartup(new Error(`Timed out waiting for automation service startup. stderr: ${stderr}`));
    }, 5_000);

    child.stdout.on("data", (chunk) => {
      stdoutBuffer += chunk;
      const newlineIndex = stdoutBuffer.indexOf("\n");
      if (newlineIndex === -1) {
        return;
      }

      clearTimeout(timeout);
      resolveStartup(JSON.parse(stdoutBuffer.slice(0, newlineIndex).trim()));
    });

    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });

    child.once("exit", (code) => {
      clearTimeout(timeout);
      rejectStartup(new Error(`automation service exited with code ${code ?? 1}: ${stderr}`));
    });
  });

  return {
    baseUrl: startup.baseUrl,
    token,
    child,
    async close() {
      child.kill("SIGTERM");
      await new Promise((resolveClose) => {
        child.once("exit", resolveClose);
        setTimeout(() => {
          child.kill("SIGKILL");
          resolveClose();
        }, 1_000).unref();
      });
    },
  };
}

test("sts2-mcp registers the CLI ai-tool catalog over stdio", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    const init = await server.initialize();
    assert.equal(init.result.serverInfo.name, "spirectl");

    const toolsResponse = await server.request("tools/list");
    const cliCatalog = await createSts2Client({
      binaryPath: realBinaryPath,
      cwd: repoRoot,
      configPath: mockConfigPath,
    }).inspectAiTools();

    const actualNames = toolsResponse.result.tools.map((tool) => tool.name).sort();
    const expectedNames = cliCatalog.tools.map((tool) => tool.name).sort();

    assert.deepEqual(actualNames, expectedNames);
    assertAiToolCatalogBoundary(actualNames);
    assert.ok(actualNames.includes("debug_events"));
  } finally {
    await server.close();
  }
});

test("sts2-mcp serves the CLI ai-tool catalog over Streamable HTTP", async () => {
  const server = await spawnMcpHttpServer({ configPath: mockConfigPath });
  const mcp = await connectMcpHttpClient(server.metadata.mcpUrl);

  try {
    assert.equal(server.metadata.transport, "http");
    assert.equal(server.metadata.auth.required, false);

    const toolsResponse = await mcp.client.listTools();
    const cliCatalog = await createSts2Client({
      binaryPath: realBinaryPath,
      cwd: repoRoot,
      configPath: mockConfigPath,
    }).inspectAiTools();

    const actualNames = toolsResponse.tools.map((tool) => tool.name).sort();
    const expectedNames = cliCatalog.tools.map((tool) => tool.name).sort();
    assert.deepEqual(actualNames, expectedNames);
    assertAiToolCatalogBoundary(actualNames);
    assert.ok(
      toolsResponse.tools.every(
        (tool) =>
          tool._meta?.["spirectl/catalogSource"] === "sts2 --json inspect ai-tools" &&
          tool._meta?.["spirectl/transport"] === "http",
      ),
    );

    const gameInfo = await mcp.client.callTool({
      name: "game_info",
      arguments: {},
    });
    const cliGameInfo = await createSts2Client({
      binaryPath: realBinaryPath,
      cwd: repoRoot,
      configPath: mockConfigPath,
    }).gameInfo();
    assert.equal(gameInfo.isError, false);
    assert.deepEqual(gameInfo.structuredContent, cliGameInfo);
    assert.equal(parseTextContent(gameInfo).repository, "spirectl");
  } finally {
    await mcp.close();
    await server.close();
  }
});

test("sts2-mcp network transport is opt-in through environment configuration", async () => {
  const server = await spawnMcpHttpServerFromEnv({ configPath: mockConfigPath });

  try {
    assert.equal(server.metadata.transport, "http");
    assert.match(server.metadata.listenAddress, /^127\.0\.0\.1:/);
    assert.equal(server.metadata.auth.required, false);
  } finally {
    await server.close();
  }
});

test("sts2-mcp HTTP preserves service-backed invalid action failures as structured content", async () => {
  const service = await spawnAutomationService();
  const server = await spawnMcpHttpServer({
    configPath: mockConfigPath,
    env: {
      STS2_SERVICE_URL: service.baseUrl,
      STS2_SERVICE_TOKEN: service.token,
    },
  });
  const mcp = await connectMcpHttpClient(server.metadata.mcpUrl);

  try {
    const result = await mcp.client.callTool({
      name: "act",
      arguments: {
        kind: "choose",
      },
    });

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.exitCode, 2);
    assert.equal(result.structuredContent.payload.error.code, "invalid_ai_tool_arguments");
    assert.deepEqual(parseTextContent(result), result.structuredContent);
  } finally {
    await mcp.close();
    await server.close();
    await service.close();
  }
});

test("sts2-mcp HTTP preserves service-backed durable test_run terminal failures as structured content", async () => {
  const service = await spawnAutomationService();
  const server = await spawnMcpHttpServer({
    configPath: mockConfigPath,
    env: {
      STS2_SERVICE_URL: service.baseUrl,
      STS2_SERVICE_TOKEN: service.token,
    },
  });
  const mcp = await connectMcpHttpClient(server.metadata.mcpUrl);

  try {
    const result = await mcp.client.callTool({
      name: "test_run",
      arguments: {
        inline: [
          "name: remote-failing-assert",
          "steps:",
          "  - dev.assert:",
          "      path: screen.type",
          "      equals: combat",
          "",
        ].join("\n"),
        durable: true,
      },
    });

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.exitCode, 3);
    assert.equal(result.structuredContent.payload.status, "failed");
    assert.equal(result.structuredContent.payload.scenarios[0].steps[0].error.code, "assertion_failed");
    assert.deepEqual(parseTextContent(result), result.structuredContent);
  } finally {
    await mcp.close();
    await server.close();
    await service.close();
  }
});

test("sts2-mcp rejects public HTTP binds without auth", async () => {
  await expectMcpHttpStartupFailure(
    ["--listen", "0.0.0.0:0", "--acknowledge-network-risk"],
    /Non-loopback network MCP binds require --auth-token/,
  );
});

test("sts2-mcp rejects public HTTP binds without risk acknowledgement", async () => {
  await expectMcpHttpStartupFailure(
    ["--listen", "0.0.0.0:0", "--auth-token", "local-mcp-token"],
    /Non-loopback network MCP binds require --acknowledge-network-risk/,
  );
});

test("sts2-mcp enforces bearer auth for HTTP MCP requests", async () => {
  const token = "local-mcp-token";
  const server = await spawnMcpHttpServer({
    configPath: mockConfigPath,
    args: ["--auth-token", token],
  });

  try {
    const rejected = await fetch(server.metadata.mcpUrl, {
      method: "POST",
      headers: {
        "content-type": "application/json",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 1,
        method: "initialize",
        params: {
          protocolVersion: "2025-03-26",
          capabilities: {},
          clientInfo: { name: "unauthorized-test", version: "1.0.0" },
        },
      }),
    });
    assert.equal(rejected.status, 401);

    const mcp = await connectMcpHttpClient(server.metadata.mcpUrl, token);
    try {
      const result = await mcp.client.callTool({
        name: "game_info",
        arguments: {},
      });
      assert.equal(result.isError, false);
      assert.equal(result.structuredContent.repository, "spirectl");
    } finally {
      await mcp.close();
    }
  } finally {
    await server.close();
  }
});

test("sts2-mcp dispatches debug_events through the CLI-owned catalog", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "debug_events",
      arguments: {
        session: "dbg:observer",
        fromSequence: 0,
        limit: 10,
      },
    });
    assert.equal(result.result.isError, false);
    assert.equal(Array.isArray(result.result.structuredContent.events), true);
    assert.equal(typeof result.result.structuredContent.retention.oldestSequence, "number");
  } finally {
    await server.close();
  }
});

test("sts2-mcp returns structured content for game_info and state", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const gameInfo = await server.request("tools/call", {
      name: "game_info",
      arguments: {},
    });
    assert.equal(gameInfo.result.isError, false);
    assert.equal(gameInfo.result.structuredContent.repository, "spirectl");
    assert.equal(parseTextContent(gameInfo.result).repository, "spirectl");

    const state = await server.request("tools/call", {
      name: "state",
      arguments: {},
    });
    assert.equal(state.result.isError, false);
    assert.equal(state.result.structuredContent.schemaVersion, "spirectl.state/v0");
    assert.equal(state.result.structuredContent.rootScene, "screens/main_menu");
  } finally {
    await server.close();
  }
});

test("sts2-mcp returns structured content for act and test_run", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const action = await server.request("tools/call", {
      name: "act",
      arguments: {
        kind: "choose",
        choiceId: "menu:start-run",
      },
    });
    assert.equal(action.result.isError, false);
    assert.equal(action.result.structuredContent.kind, "choose");
    assert.equal(action.result.structuredContent.accepted, true);

    const run = await server.request("tools/call", {
      name: "test_run",
      arguments: {
        path: "tests/scenarios/smoke-main-menu.sts2.yaml",
      },
    });
    assert.equal(run.result.isError, false);
    assert.equal(run.result.structuredContent.status, "passed");
  } finally {
    await server.close();
  }
});

test("sts2-mcp can use the automation service backend through env configuration", async () => {
  const service = await spawnAutomationService();
  const server = spawnMcpServer({
    configPath: mockConfigPath,
    env: {
      STS2_SERVICE_URL: service.baseUrl,
      STS2_SERVICE_TOKEN: service.token,
    },
  });

  try {
    await server.initialize();

    const gameInfo = await server.request("tools/call", {
      name: "game_info",
      arguments: {},
    });
    assert.equal(gameInfo.result.isError, false);
    assert.equal(gameInfo.result.structuredContent.repository, "spirectl");

    const run = await server.request("tools/call", {
      name: "test_run",
      arguments: {
        inline: "{name: remote-smoke, steps: [game.info]}",
        durable: true,
      },
    });
    assert.equal(run.result.isError, false);
    assert.equal(run.result.structuredContent.status, "passed");
    assert.ok(Array.isArray(run.result.structuredContent.remoteArtifacts));
    assert.ok(
      run.result.structuredContent.remoteArtifacts.some(
        (artifact) =>
          artifact.relativePath === "summary.json" &&
          artifact.downloadUrl.includes("/v0/test-runs/") &&
          artifact.downloadUrl.includes("/artifacts/summary.json"),
      ),
    );

    const client = createSts2Client({
      binaryPath: realBinaryPath,
      cwd: repoRoot,
      serviceUrl: service.baseUrl,
      serviceToken: service.token,
    });
    const submitted = await client.submitTestRun({
      inline: "{name: remote-helper-smoke, steps: [game.info]}",
      durable: true,
    });
    assert.equal(submitted.status, "queued");
    const completed = await client.waitForTestRun(submitted.runId);
    assert.equal(completed.status, "completed");
    assert.equal(completed.summary.status, "passed");
    const manifest = await client.listTestRunArtifacts(submitted.runId);
    assert.equal(manifest.runId, submitted.runId);
    assert.ok(manifest.artifacts.some((artifact) => artifact.relativePath === "summary.json"));
    const artifact = await client.downloadTestRunArtifact(submitted.runId, "summary.json");
    assert.equal(artifact.contentType, "application/json");
    assert.equal(artifact.body.status, "passed");
  } finally {
    await server.close();
    await service.close();
  }
});

test("sts2-mcp returns structured content for select-map-node actions", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: map\n");
  const server = spawnMcpServer({ configPath: temp.configPath });

  try {
    await server.initialize();

    const action = await server.request("tools/call", {
      name: "act",
      arguments: {
        kind: "select-map-node",
        mapNodeId: "map-node:3:1",
      },
    });
    assert.equal(action.result.isError, false);
    assert.equal(action.result.structuredContent.kind, "select-map-node");
    assert.equal(action.result.structuredContent.accepted, true);
  } finally {
    await server.close();
    temp.cleanup();
  }
});

test("sts2-mcp returns structured content for skip-rewards", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: rewards\n");
  const server = spawnMcpServer({ configPath: temp.configPath });

  try {
    await server.initialize();

    const action = await server.request("tools/call", {
      name: "act",
      arguments: {
        kind: "skip-rewards",
      },
    });
    assert.equal(action.result.isError, false);
    assert.equal(action.result.structuredContent.kind, "skip-rewards");
    assert.equal(action.result.structuredContent.accepted, true);
  } finally {
    await server.close();
    temp.cleanup();
  }
});

test("sts2-mcp exposes bundle-selection state and select-bundle execution", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: bundle-selection\n");
  const server = spawnMcpServer({ configPath: temp.configPath });

  try {
    await server.initialize();

    const state = await server.request("tools/call", {
      name: "state",
      arguments: {},
    });
    assert.equal(state.result.isError, false);
    assert.equal(state.result.structuredContent.schemaVersion, "spirectl.state/v0");
    // Canonically the bundle picker is an overlay on the local player (see client.test.js).
    const overlay = state.result.structuredContent.run.players[0].overlays[0];
    assert.equal(overlay.screenId, "Screens.CardSelection.NChooseABundleSelectionScreen");
    assert.equal(
      overlay.bundleSelection.bundles[0].id,
      "card-selection:bundle:offensive-pack:0",
    );
    const notices = state.result.structuredContent.run.notices;
    assert.ok(["undefined", "string"].includes(typeof notices[0]?.severity));
    assert.ok(["undefined", "string"].includes(typeof notices[0]?.source));

    const action = await server.request("tools/call", {
      name: "act",
      arguments: {
        kind: "select-bundle",
        bundleId: "card-selection:bundle:offensive-pack:0",
      },
    });
    assert.equal(action.result.isError, false);
    assert.equal(action.result.structuredContent.kind, "select-bundle");
    assert.equal(action.result.structuredContent.accepted, true);
  } finally {
    await server.close();
    temp.cleanup();
  }
});

test("sts2-mcp preserves typed state fields from CLI JSON", async () => {
  const server = spawnMcpServer({
    env: {
      STS2_BINARY_PATH: fakeBinaryPath,
      FAKE_STS2_CASE: "state",
    },
  });

  try {
    await server.initialize();

    const state = await server.request("tools/call", {
      name: "state",
      arguments: {},
    });
    assert.equal(state.result.isError, false);
    assert.equal(state.result.structuredContent.screen.id, "bundle-selection");
    assert.equal(state.result.structuredContent.map.nodes[0].id, "map-node:3:1");
    assert.equal(state.result.structuredContent.eventRoom.options[0].id, "event-room:gain-gold:0");
    assert.equal(state.result.structuredContent.treasureRoom.relics[0].id, "treasure-room:relic:anchor:0");
    assert.equal(state.result.structuredContent.relicSelection.relics[0].id, "relic-selection:anchor:0");
    assert.equal(state.result.structuredContent.restSite.controls[0].preferredAction.kind, "rest");
    assert.equal(state.result.structuredContent.shop.purchasableItems[0].cost.amount, 50);
    assert.equal(state.result.structuredContent.shop.purchasableItems[0].preferredAction.kind, "buy-card");
    assert.equal(state.result.structuredContent.rewards.rewards[0].id, "reward:p1:0");
    assert.equal(state.result.structuredContent.rewards.rewards[0].preferredAction.kind, "claim-reward");
    assert.equal(state.result.structuredContent.cardSelection.cards[0].id, "card-selection:card:bash:0");
    assert.equal(state.result.structuredContent.simpleCardSelection.choices[0].id, "simple-card-selection:card:strike:0");
    assert.equal(state.result.structuredContent.deckCardSelection.deckCards[0].id, "deck-card-selection:card:defend:0");
    assert.notEqual(state.result.structuredContent.bundleSelection, null);
    assert.equal(state.result.structuredContent.bundleSelection.bundles[0].id, "bundle:offensive-pack");
    assert.equal(state.result.structuredContent.multiplayerLobby.players[0].perspective, "local:p1");
    assert.equal(
      state.result.structuredContent.multiplayerLobby.players.find((player) => player.id === "p3").ownerRole,
      "host-local-seat",
    );
    assert.equal(
      state.result.structuredContent.multiplayerLobby.players.find((player) => player.id === "p3").remoteOrchestrationCapability,
      "host-local-seat",
    );
    assert.equal(state.result.structuredContent.multiplayerLobby.actions[0].intentKind, "ready");
    assert.equal(
      state.result.structuredContent.multiplayerLobby.actions.find((action) => action.ownerPlayerId === "p3").ownerRole,
      "host-local-seat",
    );
    assert.equal(
      state.result.structuredContent.multiplayerLobby.actions.find((action) => action.ownerPlayerId === "p3").remoteOrchestrationCapability,
      "host-local-seat",
    );
    assert.equal(state.result.structuredContent.cardOverlay.cards[0].id, "card-overlay:p1:rage:0");
    assert.equal(state.result.structuredContent.cardOverlay.cards[0].assetRefs[0].key, "card:red:rage");
    assert.equal(state.result.structuredContent.cardOverlay.previewText, "Whenever you play an Attack this turn, gain 5 Block.");
    assert.equal(state.result.structuredContent.cardOverlay.breadcrumbs[0].screenType, "bundle-selection");
    assert.equal(state.result.structuredContent.cardOverlay.sourceBreadcrumbs[0].source, "underlying-screen");
    assert.equal(state.result.structuredContent.cardOverlay.close.intentKind, "close-overlay");
    assert.equal(state.result.structuredContent.cardOverlay.back.enabled, false);
    assert.equal(state.result.structuredContent.cardOverlay.followThroughControls[0].preferredAction, "choose");
    assert.equal(state.result.structuredContent.cardOverlay.notices[0].severity, "partial");
    assert.equal(state.result.structuredContent.cardOverlay.metadata.noticeCode, "card-overlay-partial");
    assert.equal(state.result.structuredContent.choices[0].choiceKind, "bundle");
    assert.equal(state.result.structuredContent.choices[0].preferredAction, "select-bundle");
    assert.equal(state.result.structuredContent.choices[0].preferredActionRef.kind, "select-bundle");
    assert.equal(state.result.structuredContent.choices[1].choiceKind, "overlay-close");
    assert.equal(state.result.structuredContent.choices[1].metadata.overlayPolicy, "blocking-overlay");
    assert.equal(state.result.structuredContent.availableActions[0].ownerPlayerId, "p1");
    assert.equal(state.result.structuredContent.availableActions[0].preferredAction, "select-bundle");
    assert.equal(state.result.structuredContent.availableActions[1].intentKind, "close-overlay");
    assert.equal(state.result.structuredContent.availableActions[1].metadata.overlayPolicy, "blocking-overlay");
    assert.equal(state.result.structuredContent.notices[0].path, "bundleSelection.bundles");
    assert.equal(state.result.structuredContent.notices[0].perspective, "local:p1");
    assert.equal(state.result.structuredContent.notices[1].severity, "partial");
    assert.equal(state.result.structuredContent.notices[2].code, "card-overlay-passive");
  } finally {
    await server.close();
  }
});

test("sts2-mcp returns structured content for buy-card", async () => {
  const temp = writeConfig("transport:\n  kind: mock\n  mockScenario: shop\n");
  const server = spawnMcpServer({ configPath: temp.configPath });

  try {
    await server.initialize();

    const action = await server.request("tools/call", {
      name: "act",
      arguments: {
        kind: "buy-card",
        shopItemId: "shop:p1:card:strike:0",
      },
    });
    assert.equal(action.result.isError, false);
    assert.equal(action.result.structuredContent.kind, "buy-card");
    assert.equal(action.result.structuredContent.accepted, true);
  } finally {
    await server.close();
    temp.cleanup();
  }
});

test("sts2-mcp returns structured content for static inspection", async () => {
  execFileSync("dotnet", ["build", fixtureProject], { cwd: repoRoot, stdio: "pipe" });
  const temp = writeConfig(`game:\n  path: auto\n  assembliesDir: ${fixtureAssemblyDir}\ntransport:\n  kind: mock\n`);
  const server = spawnMcpServer({ configPath: temp.configPath });

  try {
    await server.initialize();

    const locate = await server.request("tools/call", {
      name: "code_locate",
      arguments: {
        subject: "type",
        query: "DeckController",
      },
    });
    assert.equal(locate.result.isError, false);
    assert.equal(locate.result.structuredContent.command, "locate");
    assert.equal(locate.result.structuredContent.status, "ok");
  } finally {
    await server.close();
    temp.cleanup();
  }
});

test("sts2-mcp preserves structured CLI failures for assertion mismatches", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "assert",
      arguments: {
        path: "screen.type",
        contains: "combat",
      },
    });
    assert.equal(result.result.isError, true);
    assert.equal(result.result.structuredContent.exitCode, 3);
    assert.equal(result.result.structuredContent.payload.error.code, "assertion_failed");
  } finally {
    await server.close();
  }
});

test("sts2-mcp returns structured content for promoted screenshot", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const screenshot = await server.request("tools/call", {
      name: "screenshot",
      arguments: {
        output: "tmp/mcp-runtime.png",
      },
    });
    assert.equal(screenshot.result.isError, false);
    assert.equal(
      screenshot.result.structuredContent.path,
      resolve(repoRoot, "tmp", "mcp-runtime.png"),
    );
    assert.equal(
      parseTextContent(screenshot.result).path,
      resolve(repoRoot, "tmp", "mcp-runtime.png"),
    );
  } finally {
    await server.close();
  }
});

test("sts2-mcp returns structured content for promoted game_detect", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "game_detect",
      arguments: {},
    });
    assert.equal(result.result.isError, false);
    assert.ok(
      ["configured", "detected", "missing", "not-detected"].includes(
        result.result.structuredContent.status,
      ),
    );
    assert.equal(
      parseTextContent(result.result).status,
      result.result.structuredContent.status,
    );
  } finally {
    await server.close();
  }
});

test("sts2-mcp returns structured content for promoted assets_extract", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "assets_extract",
      arguments: {
        query: "hand",
        execution: "offline",
        resourcesDir: fixtureResourcesDir,
      },
    });
    assert.equal(result.result.isError, false);
    assert.equal(result.result.structuredContent.command, "extract");
    assert.equal(result.result.structuredContent.status, "ok");
  } finally {
    await server.close();
  }
});

test("sts2-mcp returns structured content for promoted assets_extract_batch", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });
  const manifestPath = join(tmpdir(), `sts2-assets-batch-${process.pid}.json`);

  try {
    writeFileSync(
      manifestPath,
      JSON.stringify({
        version: 0,
        assets: [{ id: "hand", query: "hand", execution: "offline" }],
      }),
    );
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "assets_extract_batch",
      arguments: {
        manifest: manifestPath,
        execution: "offline",
        resourcesDir: fixtureResourcesDir,
      },
    });
    assert.equal(result.result.isError, false);
    assert.equal(result.result.structuredContent.command, "extract-batch");
    assert.equal(result.result.structuredContent.status, "ok");
  } finally {
    rmSync(manifestPath, { force: true });
    await server.close();
  }
});

test("sts2-mcp preserves asset diagnostic fields from CLI JSON", async () => {
  const server = spawnMcpServer({
    env: {
      STS2_BINARY_PATH: fakeBinaryPath,
      FAKE_STS2_CASE: "asset-diagnostics",
    },
  });

  try {
    await server.initialize();

    const explain = await server.request("tools/call", {
      name: "assets_explain",
      arguments: {
        query: "encounter:kaiser_crab_boss:scene-package",
        execution: "live",
      },
    });
    assert.equal(explain.result.isError, false);
    assert.equal(
      explain.result.structuredContent.explanation.selectorDiagnostics[0].status,
      "resolved",
    );
    assert.equal(
      explain.result.structuredContent.explanation.renderTargets[0].decision.decision,
      "render-overlay",
    );

    const batch = await server.request("tools/call", {
      name: "assets_extract_batch",
      arguments: {
        manifest: "/tmp/asset-manifest.json",
        execution: "live",
      },
    });
    assert.equal(batch.result.isError, false);
    assert.equal(batch.result.structuredContent.results[0].exports[0].notices[0].code, "selector-fallback");
    assert.equal(
      batch.result.structuredContent.results[1].error.code,
      "encounter_visual_package_unsupported",
    );
  } finally {
    await server.close();
  }
});

test("sts2-mcp preserves structured CLI failures for promoted load_fixture errors", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "load_fixture",
      arguments: {
        path: "fixtures/does-not-exist.sts2.fixture.yaml",
      },
    });
    assert.equal(result.result.isError, true);
    assert.equal(result.result.structuredContent.exitCode, 2);
    assert.equal(result.result.structuredContent.payload.error.code, "fixture_read_failed");
  } finally {
    await server.close();
  }
});

test("sts2-mcp preserves structured CLI failures for promoted hot_reload_status", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });

  try {
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "hot_reload_status",
      arguments: {
        project: "./missing-hot-reload-project",
      },
    });
    assert.equal(result.result.isError, true);
    assert.equal(result.result.structuredContent.exitCode, 2);
    assert.equal(
      result.result.structuredContent.payload.error.code,
      "hot_reload_project_not_found",
    );
  } finally {
    await server.close();
  }
});

test("sts2-mcp preserves structured CLI failures for promoted skill_install errors", async () => {
  const server = spawnMcpServer({ configPath: mockConfigPath });
  const dir = mkdtempSync(join(tmpdir(), "sts2-mcp-skill-install-"));
  const invalidPath = join(dir, "not-a-dir");
  writeFileSync(invalidPath, "x");

  try {
    await server.initialize();

    const result = await server.request("tools/call", {
      name: "skill_install",
      arguments: {
        path: invalidPath,
      },
    });
    assert.equal(result.result.isError, true);
    assert.equal(result.result.structuredContent.exitCode, 2);
    assert.equal(result.result.structuredContent.payload.error.code, "skill_install_failed");
  } finally {
    rmSync(dir, { recursive: true, force: true });
    await server.close();
  }
});
