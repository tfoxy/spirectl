#!/usr/bin/env node

import { startMcpServer } from "../src/mcp-server.js";
import { DEFAULT_LISTEN, startMcpHttpServer } from "../src/mcp-http-server.js";

function parseBoolean(value) {
  return ["1", "true", "yes", "on"].includes(String(value || "").toLowerCase());
}

function parseArgs(argv, env = process.env) {
  const options = {
    transport: env.STS2_MCP_TRANSPORT || "stdio",
    listen: env.STS2_MCP_LISTEN || DEFAULT_LISTEN,
    authToken: env.STS2_MCP_AUTH_TOKEN,
    acknowledgeNetworkRisk: parseBoolean(env.STS2_MCP_ACKNOWLEDGE_NETWORK_RISK),
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--transport") {
      options.transport = argv[++index];
    } else if (arg.startsWith("--transport=")) {
      options.transport = arg.slice("--transport=".length);
    } else if (arg === "--listen") {
      options.listen = argv[++index];
    } else if (arg.startsWith("--listen=")) {
      options.listen = arg.slice("--listen=".length);
    } else if (arg === "--auth-token") {
      options.authToken = argv[++index];
    } else if (arg.startsWith("--auth-token=")) {
      options.authToken = arg.slice("--auth-token=".length);
    } else if (arg === "--acknowledge-network-risk") {
      options.acknowledgeNetworkRisk = true;
    } else {
      throw new Error(`Unknown sts2-mcp option: ${arg}`);
    }
  }

  if (!["stdio", "http"].includes(options.transport)) {
    throw new Error("--transport must be stdio or http.");
  }

  return options;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.transport === "stdio") {
    await startMcpServer();
    return;
  }

  const started = await startMcpHttpServer(options);
  process.stdout.write(`${JSON.stringify(started.metadata)}\n`);
}

main().catch((error) => {
  const detail = error?.stack || error?.message || String(error);
  process.stderr.write(`sts2-mcp failed to start: ${detail}\n`);
  process.exit(1);
});
