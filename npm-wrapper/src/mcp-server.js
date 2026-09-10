import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ErrorCode,
  ListToolsRequestSchema,
  McpError,
} from "@modelcontextprotocol/sdk/types.js";
import { AjvJsonSchemaValidator } from "@modelcontextprotocol/sdk/validation/ajv-provider.js";

import { createSts2Client } from "./index.js";
import { callMcpTool } from "./mcp-tool-executor.js";

function envClientOptions(env = process.env) {
  return {
    binaryPath: env.STS2_BINARY_PATH,
    configPath: env.STS2_CONFIG_PATH,
    mode: env.STS2_MODE,
    cwd: env.STS2_CWD,
    serviceUrl: env.STS2_SERVICE_URL,
    serviceToken: env.STS2_SERVICE_TOKEN,
    env,
  };
}

function toolDescription(tool) {
  const sections = [tool.summary];

  if (Array.isArray(tool.mapsTo) && tool.mapsTo.length > 0) {
    sections.push(`Maps to: ${tool.mapsTo.join(", ")}`);
  }

  sections.push(`Status: ${tool.status}. ${tool.readOnly ? "Read-only." : "May mutate state."}`);

  if (Array.isArray(tool.limitations) && tool.limitations.length > 0) {
    sections.push(`Limitations: ${tool.limitations.join(" ")}`);
  }

  if (Array.isArray(tool.recommendedUsage) && tool.recommendedUsage.length > 0) {
    sections.push(`Recommended usage: ${tool.recommendedUsage.join(" ")}`);
  }

  return sections.join("\n\n");
}

function toolDefinition(tool, adapterMetadata = {}) {
  return {
    name: tool.name,
    description: toolDescription(tool),
    inputSchema: tool.inputSchema,
    annotations: {
      title: tool.name,
      readOnlyHint: tool.readOnly,
      destructiveHint: tool.readOnly ? false : true,
      idempotentHint: tool.readOnly,
      openWorldHint: false,
    },
    _meta: {
      "spirectl/source": "sts2 --json inspect ai-tools",
      "spirectl/catalogSource": adapterMetadata.catalogSource ?? "sts2 --json inspect ai-tools",
      "spirectl/transport": adapterMetadata.transport ?? "stdio",
      "spirectl/status": tool.status,
      "spirectl/mapsTo": tool.mapsTo,
    },
  };
}

export async function loadAiToolCatalog(clientOptions = envClientOptions()) {
  const client = createSts2Client(clientOptions);
  return client.inspectAiTools();
}

export async function createMcpServer(clientOptions = envClientOptions()) {
  return createMcpServerFromCatalog(await loadAiToolCatalog(clientOptions), clientOptions);
}

export function createMcpServerFromCatalog(
  catalog,
  clientOptions = envClientOptions(),
  adapterMetadata = {},
) {
  const tools = catalog.tools.map((tool) => toolDefinition(tool, adapterMetadata));
  const toolNames = new Set(tools.map((tool) => tool.name));
  const jsonSchemaValidator = new AjvJsonSchemaValidator();
  const toolValidators = new Map(
    tools.map((tool) => [tool.name, jsonSchemaValidator.getValidator(tool.inputSchema)]),
  );

  const server = new Server(
    {
      name: "spirectl",
      version: "0.1.0",
    },
    {
      capabilities: {
        tools: {
          listChanged: false,
        },
      },
      instructions:
        "Thin MCP adapter over the repo-local sts2 CLI. Use state and inspect_actions before act, and prefer locate/describe before decompile.",
    },
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => ({
    tools,
  }));

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args = {} } = request.params;

    if (!toolNames.has(name)) {
      throw new McpError(ErrorCode.InvalidParams, `Unknown tool: ${name}`);
    }

    const validator = toolValidators.get(name);
    const validation = validator?.(args);
    if (validation && !validation.valid) {
      throw new McpError(
        ErrorCode.InvalidParams,
        `Invalid arguments for tool ${name}: ${validation.errorMessage}`,
      );
    }

    return callMcpTool(clientOptions, name, args);
  });

  return { server, catalog };
}

export async function startMcpServer(clientOptions = envClientOptions()) {
  const { server, catalog } = await createMcpServer(clientOptions);
  const transport = new StdioServerTransport();
  await server.connect(transport);
  return { server, transport, catalog };
}
