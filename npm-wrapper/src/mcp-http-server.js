import { randomUUID } from "node:crypto";
import { createServer } from "node:http";

import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";

import { createMcpServerFromCatalog, loadAiToolCatalog } from "./mcp-server.js";

const DEFAULT_LISTEN = "127.0.0.1:4318";

function parseListen(listen = DEFAULT_LISTEN) {
  const value = String(listen || DEFAULT_LISTEN);
  const ipv6Match = value.match(/^\[([^\]]+)]:(\d+)$/);
  if (ipv6Match) {
    return { host: ipv6Match[1], port: Number(ipv6Match[2]) };
  }

  const lastColon = value.lastIndexOf(":");
  if (lastColon <= 0 || lastColon === value.length - 1) {
    throw new Error(`Invalid MCP listen address: ${value}`);
  }

  return {
    host: value.slice(0, lastColon),
    port: Number(value.slice(lastColon + 1)),
  };
}

function isLoopbackHost(host) {
  const normalized = String(host || "").toLowerCase();
  return (
    normalized === "localhost" ||
    normalized === "::1" ||
    normalized === "0:0:0:0:0:0:0:1" ||
    normalized.startsWith("127.")
  );
}

function validateNetworkPolicy({ listen = DEFAULT_LISTEN, authToken, acknowledgeNetworkRisk = false }) {
  const parsed = parseListen(listen);
  if (!Number.isInteger(parsed.port) || parsed.port < 0 || parsed.port > 65535) {
    throw new Error(`Invalid MCP listen port: ${parsed.port}`);
  }

  if (!isLoopbackHost(parsed.host) && !authToken) {
    throw new Error("Non-loopback network MCP binds require --auth-token.");
  }

  if (!isLoopbackHost(parsed.host) && !acknowledgeNetworkRisk) {
    throw new Error(
      "Non-loopback network MCP binds require --acknowledge-network-risk.",
    );
  }

  return parsed;
}

function sendJson(res, status, payload) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(payload));
}

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    req.setEncoding("utf8");
    req.on("data", (chunk) => {
      body += chunk;
    });
    req.on("end", () => {
      if (!body.trim()) {
        resolve(undefined);
        return;
      }
      try {
        resolve(JSON.parse(body));
      } catch (error) {
        reject(error);
      }
    });
    req.on("error", reject);
  });
}

function authorize(req, authToken) {
  if (!authToken) {
    return true;
  }
  return req.headers.authorization === `Bearer ${authToken}`;
}

export async function startMcpHttpServer(options = {}) {
  const {
    listen = DEFAULT_LISTEN,
    authToken,
    acknowledgeNetworkRisk = false,
    clientOptions,
  } = options;
  const { host, port } = validateNetworkPolicy({ listen, authToken, acknowledgeNetworkRisk });
  const catalog = await loadAiToolCatalog(clientOptions);
  const transports = new Map();

  const httpServer = createServer(async (req, res) => {
    if (req.url !== "/mcp") {
      sendJson(res, 404, { error: { code: "not_found", message: "Use /mcp." } });
      return;
    }

    if (!["POST", "GET", "DELETE"].includes(req.method)) {
      res.writeHead(405, { allow: "POST, GET, DELETE" });
      res.end();
      return;
    }

    if (!authorize(req, authToken)) {
      sendJson(res, 401, {
        error: {
          code: "unauthorized",
          message: "Missing or invalid bearer token.",
        },
      });
      return;
    }

    const sessionId = req.headers["mcp-session-id"];

    try {
      if (req.method === "POST") {
        const parsedBody = await readJsonBody(req);
        let transport = sessionId ? transports.get(sessionId) : undefined;

        if (!transport && !sessionId && isInitializeRequest(parsedBody)) {
          transport = new StreamableHTTPServerTransport({
            sessionIdGenerator: () => randomUUID(),
            onsessioninitialized: (newSessionId) => {
              transports.set(newSessionId, transport);
            },
          });
          transport.onclose = () => {
            const closedSessionId = transport.sessionId;
            if (closedSessionId) {
              transports.delete(closedSessionId);
            }
          };

          const { server } = createMcpServerFromCatalog(catalog, clientOptions, {
            transport: "http",
          });
          await server.connect(transport);
        }

        if (!transport) {
          sendJson(res, 400, {
            jsonrpc: "2.0",
            error: {
              code: -32000,
              message: "Bad Request: No valid session ID provided",
            },
            id: null,
          });
          return;
        }

        await transport.handleRequest(req, res, parsedBody);
        return;
      }

      const transport = sessionId ? transports.get(sessionId) : undefined;
      if (!transport) {
        res.writeHead(400, { "content-type": "text/plain" });
        res.end("Invalid or missing session ID");
        return;
      }

      await transport.handleRequest(req, res);
    } catch (error) {
      if (!res.headersSent) {
        sendJson(res, 500, {
          jsonrpc: "2.0",
          error: {
            code: -32603,
            message: error?.message || "Internal server error",
          },
          id: null,
        });
      }
    }
  });

  await new Promise((resolve, reject) => {
    httpServer.once("error", reject);
    httpServer.listen(port, host, () => {
      httpServer.off("error", reject);
      resolve();
    });
  });

  const address = httpServer.address();
  const listenAddress =
    typeof address === "object" && address
      ? `${address.address}:${address.port}`
      : `${host}:${port}`;
  const baseUrl = `http://${listenAddress}`;

  return {
    server: httpServer,
    catalog,
    transports,
    metadata: {
      service: "sts2-mcp",
      version: "0.1.0",
      transport: "http",
      sourceOfTruth: "sts2 --json inspect ai-tools",
      baseUrl,
      mcpUrl: `${baseUrl}/mcp`,
      listenAddress,
      auth: {
        mode: authToken ? "bearer" : "none",
        required: Boolean(authToken),
      },
    },
    async close() {
      for (const transport of transports.values()) {
        await transport.close();
      }
      transports.clear();
      await new Promise((resolve) => httpServer.close(resolve));
    },
  };
}

export { DEFAULT_LISTEN, isLoopbackHost, parseListen, validateNetworkPolicy };
