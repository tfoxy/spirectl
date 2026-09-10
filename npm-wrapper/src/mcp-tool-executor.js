import { Sts2CliError, Sts2ServiceError, createSts2Client } from "./index.js";

function jsonText(value) {
  return JSON.stringify(value, null, 2);
}

function resultContent(payload) {
  return [
    {
      type: "text",
      text: jsonText(payload),
    },
  ];
}

function successResult(payload) {
  return {
    content: resultContent(payload),
    structuredContent: payload,
    isError: false,
  };
}

function toolErrorResult(payload) {
  return {
    content: resultContent(payload),
    structuredContent: payload,
    isError: true,
  };
}

function adapterError(code, error) {
  return {
    code,
    message: error.message,
    argv: Array.isArray(error.argv) ? error.argv : [],
    stdout: typeof error.stdout === "string" ? error.stdout : "",
    stderr: typeof error.stderr === "string" ? error.stderr : "",
  };
}

const toolHandlers = {
  game_detect: (client) => client.gameDetect(),
  game_info: (client) => client.gameInfo(),
  toolchain_info: (client) => client.toolchainInfo(),
  game_launch: (client, args) => client.gameLaunch(args),
  game_attach: (client, args) => client.gameAttach(args),
  game_close: (client, args) => client.gameClose(args),
  game_deploy: (client, args) => client.gameDeploy(args),
  state: (client, args) => client.state(args),
  // presentation_catalog / presentation_snapshot removed with the client methods: the CLI has no
  // `presentation` command group any more (bcf9a97e) and `inspect ai-tools` no longer lists them.
  inspect_actions: (client) => client.inspectActions(),
  inspect_viewport_presets: (client, args) => client.inspectViewportPresets(args),
  inspect_reference_topics: (client) => client.inspectReferenceTopics(),
  act: (client, args) => client.act(args),
  console: (client, args) => client.devConsole(args),
  logs: (client, args) => client.logs(args),
  log_health: (client, args) => client.logHealth(args),
  diagnostics: (client, args) => client.diagnostics(args),
  assets_extract: (client, args) => client.assetsExtract(args),
  assets_explain: (client, args) => client.assetsExplain(args),
  assets_extract_batch: (client, args) => client.assetsExtractBatch(args),
  load_fixture: (client, args) => client.loadFixture(args),
  hot_reload_status: (client, args) => client.hotReloadStatus(args),
  hot_reload: (client, args) => client.hotReload(args),
  scenario_export: (client, args) => client.scenarioExport(args),
  scenario_load: (client, args) => client.scenarioLoad(args),
  debug_status: (client, args) => client.debugStatus(args),
  debug_session_start: (client, args) => client.debugSessionStart(args),
  debug_session_status: (client, args) => client.debugSessionStatus(args),
  debug_session_end: (client, args) => client.debugSessionEnd(args),
  debug_events: (client, args) => client.debugEvents({ sessionId: args.session, ...args }),
  debug_pause: (client, args) => client.debugPause(args),
  debug_resume: (client, args) => client.debugResume(args),
  debug_step: (client, args) => client.debugStep(args),
  debug_wait: (client, args) => client.debugWait(args),
  breakpoint_list: (client, args) => client.breakpointList(args),
  breakpoint_add: (client, args) => client.breakpointAdd(args),
  breakpoint_remove: (client, args) => client.breakpointRemove(args),
  http: (client, args) => client.http(args),
  http_wait: (client, args) => client.httpWait(args),
  fetch: (client, args) => client.fetch(args),
  websocket: (client, args) => client.websocket(args),
  project_profile_list: (client) => client.projectProfileList(),
  project_profile_show: (client, args) => client.projectProfileShow(args),
  project_profile_run: (client, args) => client.projectProfileRun(args),
  project_hook_list: (client) => client.projectHookList(),
  project_hook_show: (client, args) => client.projectHookShow(args),
  project_hook_run: (client, args) => client.projectHookRun(args),
  skill_install: (client, args) => client.skillInstall(args),
  screenshot: (client, args) => client.screenshot(args),
  screenshot_diff: (client, args) => client.screenshotDiff(args),
  snapshot_export: (client, args) => client.snapshotExport(args),
  snapshot_compare: (client, args) => client.snapshotCompare(args),
  wait_for: (client, args) => client.waitFor(args),
  assert: (client, args) => client.assert(args),
  code_locate: (client, args) => client.codeLocate(args),
  code_describe: (client, args) => client.codeDescribe(args),
  code_refs: (client, args) => client.codeRefs(args),
  code_derived: (client, args) => client.codeDerived(args),
  code_decompile: (client, args) => client.codeDecompile(args),
  code_hooks: (client, args) => client.codeHooks(args),
  code_hook_info: (client, args) => client.codeHookInfo(args),
  code_scene_search: (client, args) => client.codeSceneSearch(args),
  code_scene_tree: (client, args) => client.codeSceneTree(args),
  code_scene_node: (client, args) => client.codeSceneNode(args),
  test_run: (client, args) => client.testRun(args),
  test_stress: (client, args) => client.testStress(args),
};

export async function callMcpTool(clientOptions, toolName, args = {}) {
  const handler = toolHandlers[toolName];
  if (!handler) {
    throw new TypeError(`Unsupported AI tool: ${toolName}`);
  }

  const client = createSts2Client(clientOptions);

  try {
    const payload = await handler(client, args);
    return successResult(payload);
  } catch (error) {
    if (error instanceof Sts2CliError) {
      return toolErrorResult({
        exitCode: error.exitCode,
        argv: error.argv,
        stderr: error.stderr,
        payload: error.payload,
      });
    }

    if (error instanceof Sts2ServiceError) {
      return toolErrorResult({
        statusCode: error.statusCode,
        payload: error.payload,
      });
    }

    if (error?.name === "Sts2CliSpawnError") {
      return toolErrorResult(adapterError("cli_spawn_failed", error));
    }

    if (error?.name === "Sts2CliProtocolError") {
      return toolErrorResult(adapterError("cli_protocol_error", error));
    }

    throw error;
  }
}
