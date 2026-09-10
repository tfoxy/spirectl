import { Sts2CliError } from "./error.js";
import { runJsonCommand } from "./command.js";
import { Sts2ServiceError, createServiceClient } from "./service-client.js";
import { normalizeTestRunOptions } from "./test-run-normalizer.js";

function pushFlag(argv, flag, value) {
  if (value !== undefined && value !== null && value !== false) {
    argv.push(flag, String(value));
  }
}

function pushBooleanFlag(argv, flag, enabled) {
  if (enabled) {
    argv.push(flag);
  }
}

function pushRepeatedFlag(argv, flag, values) {
  if (!Array.isArray(values)) {
    return;
  }
  for (const value of values) {
    if (value !== undefined && value !== null && value !== false) {
      argv.push(flag, String(value));
    }
  }
}

function pushPerspectiveArgs(argv, options = {}) {
  pushFlag(argv, "--perspective", options.perspective);
  pushFlag(argv, "--player-id", options.playerId);
}

function pushActionPlayerArgs(argv, action = {}) {
  pushFlag(argv, "--player-id", action.playerId);
}

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function normalizePreferredAction(action) {
  const preferredAction = action?.preferredActionRef ?? action?.preferredAction;
  if (!isObject(preferredAction)) {
    return action;
  }

  const preferredArgs = isObject(preferredAction.arguments) ? preferredAction.arguments : {};
  const actionArgs = isObject(action.arguments) ? action.arguments : {};

  return {
    ...preferredArgs,
    ...preferredAction,
    ...action,
    arguments: {
      ...preferredArgs,
      ...actionArgs,
    },
    kind: action.kind ?? preferredAction.kind,
    ownerPlayerId: action.ownerPlayerId ?? preferredAction.ownerPlayerId,
    playerId: action.playerId ?? actionArgs.playerId ?? preferredArgs.playerId ?? preferredAction.ownerPlayerId,
  };
}

function normalizeAction(action) {
  const preferred = normalizePreferredAction(action);
  const args = isObject(preferred.arguments) ? preferred.arguments : {};

  return {
    ...preferred,
    choiceId: preferred.choiceId ?? preferred.choice ?? args.choiceId,
    rewardId: preferred.rewardId ?? preferred.reward ?? args.rewardId,
    mapNodeId: preferred.mapNodeId ?? preferred.node ?? args.mapNodeId,
    cardId: preferred.cardId ?? preferred.card ?? args.cardId,
    bundleId: preferred.bundleId ?? preferred.bundle ?? args.bundleId,
    shopItemId: preferred.shopItemId ?? preferred.shopItem ?? args.shopItemId,
    relicId: preferred.relicId ?? preferred.relic ?? args.relicId,
    restOptionId: preferred.restOptionId ?? preferred.restOption ?? args.restOptionId,
    eventOptionId: preferred.eventOptionId ?? preferred.eventOption ?? args.eventOptionId,
    controlId: preferred.controlId ?? preferred.control ?? args.controlId,
    potionId: preferred.potionId ?? preferred.potion ?? args.potionId,
    targetId: preferred.targetId ?? preferred.target ?? args.targetId,
    characterId: preferred.characterId ?? preferred.character ?? args.characterId,
    playerId: preferred.playerId ?? args.playerId,
  };
}

function normalizedActionToolArgs(action) {
  const args = {
    kind: action.kind,
    ownerPlayerId: action.ownerPlayerId,
    perspective: action.perspective,
    choiceId: action.choiceId,
    rewardId: action.rewardId,
    mapNodeId: action.mapNodeId,
    cardId: action.cardId,
    bundleId: action.bundleId,
    shopItemId: action.shopItemId,
    relicId: action.relicId,
    restOptionId: action.restOptionId,
    eventOptionId: action.eventOptionId,
    controlId: action.controlId,
    potionId: action.potionId,
    targetId: action.targetId,
    characterId: action.characterId,
    playerId: action.playerId,
  };

  return Object.fromEntries(
    Object.entries(args).filter(([, value]) => value !== undefined && value !== null),
  );
}

function pushCodeSearchRootArgs(argv, options = {}) {
  pushFlag(argv, "--game-path", options.gamePath);
  pushFlag(argv, "--assemblies-dir", options.assembliesDir);
  pushFlag(argv, "--resources-dir", options.resourcesDir);
  pushFlag(argv, "--mods-dir", options.modsDir);
  pushBooleanFlag(argv, "--include-mods", options.includeMods === true);
  pushBooleanFlag(argv, "--include-dependencies", options.includeDependencies === true);
}

function predicateEntries(options) {
  return [
    ["--equals", options.equals],
    ["--contains", options.contains],
    ["--regex", options.regex],
    ["--gt", options.gt],
    ["--gte", options.gte],
    ["--lt", options.lt],
    ["--lte", options.lte],
    ["--exists", options.exists === true ? true : undefined],
    ["--not-exists", options.notExists === true ? true : undefined],
  ].filter(([, value]) => value !== undefined && value !== null && value !== false);
}

function predicateArgv(options, methodName) {
  const entries = predicateEntries(options);

  if (entries.length !== 1) {
    throw new TypeError(
      `${methodName} requires exactly one predicate option: equals, contains, regex, gt, gte, lt, lte, exists, or notExists.`,
    );
  }

  const [flag, value] = entries[0];
  if (value === true) {
    return [flag];
  }

  return [flag, String(value)];
}

function optionalPredicateArgv(options, methodName) {
  const entries = predicateEntries(options);
  if (entries.length === 0) {
    return [];
  }

  if (entries.length !== 1) {
    throw new TypeError(
      `${methodName} requires at most one predicate option: equals, contains, regex, gt, gte, lt, lte, exists, or notExists.`,
    );
  }

  const [flag, value] = entries[0];
  if (value === true) {
    return [flag];
  }

  return [flag, String(value)];
}

function assertPath(path, methodName) {
  if (!path) {
    throw new TypeError(`${methodName} requires a non-empty path.`);
  }
}

function assertRequired(value, label, methodName) {
  if (!value) {
    throw new TypeError(`${methodName} requires ${label}.`);
  }
}

function assertStringValue(value, label, methodName) {
  if (value === undefined || value === null || value === "") {
    throw new TypeError(`${methodName} requires ${label}.`);
  }
}

function serializeOptionalJsonInput(value, methodName) {
  if (value === undefined || value === null) {
    return undefined;
  }
  if (typeof value === "string") {
    return value;
  }

  const serialized = JSON.stringify(value);
  if (!serialized) {
    throw new TypeError(`${methodName} requires input to be a JSON-serializable string, object, or array.`);
  }
  return serialized;
}

function serviceErrorStdout(payload) {
  if (payload === undefined) {
    return "";
  }

  return `${JSON.stringify(payload)}\n`;
}

function toPublicCliError(error) {
  if (!(error instanceof Sts2ServiceError)) {
    return error;
  }

  const exitCode = Number.isInteger(error.payload?.exitCode) ? error.payload.exitCode : 1;
  return new Sts2CliError({
    exitCode,
    argv: [],
    stdout: serviceErrorStdout(error.payload),
    stderr: "",
    payload: error.payload ?? null,
  });
}

function testRunInputArgv(runOptions = {}) {
  const normalized = normalizeTestRunOptions(runOptions);
  const argv = [];
  pushFlag(argv, "--profile", normalized.profile);
  pushRepeatedFlag(argv, "--tag", normalized.tags);

  if (!normalized.input) {
    return argv;
  }

  if (normalized.input.kind === "path") {
    argv.push(normalized.input.value);
    return argv;
  }

  argv.push("--inline", normalized.input.value);
  return argv;
}

export function createSts2Client(options = {}) {
  const serviceUrl = options.serviceUrl ?? options.env?.STS2_SERVICE_URL ?? process.env.STS2_SERVICE_URL;
  const serviceToken =
    options.serviceToken ?? options.env?.STS2_SERVICE_TOKEN ?? process.env.STS2_SERVICE_TOKEN;
  const serviceClient = serviceUrl ? createServiceClient({ serviceUrl, serviceToken }) : null;

  async function run(argv) {
    return runJsonCommand(options, argv);
  }

  async function runServiceCall(execute) {
    try {
      return await execute();
    } catch (error) {
      throw toPublicCliError(error);
    }
  }

  async function callToolOrRun(toolName, toolArgs, argv) {
    if (serviceClient) {
      return runServiceCall(() => serviceClient.callTool(toolName, toolArgs ?? {}));
    }
    return run(argv);
  }

  return {
    async gameDetect() {
      return callToolOrRun("game_detect", {}, ["game", "detect"]);
    },

    async gameInfo() {
      if (serviceClient) {
        return runServiceCall(() => serviceClient.callTool("game_info", {}));
      }
      return run(["game", "info"]);
    },

    async toolchainInfo() {
      return callToolOrRun("toolchain_info", {}, ["toolchain", "info"]);
    },

    async gameLaunch(launchOptions = {}) {
      const argv = ["game", "launch"];
      pushFlag(argv, "--timeout-ms", launchOptions.timeoutMs);
      pushFlag(argv, "--interval-ms", launchOptions.intervalMs);
      if (Array.isArray(launchOptions.launchArgs) && launchOptions.launchArgs.length > 0) {
        argv.push("--", ...launchOptions.launchArgs.map(String));
      }
      return callToolOrRun("game_launch", launchOptions, argv);
    },

    async gameAttach(attachOptions = {}) {
      const argv = ["game", "attach"];
      pushFlag(argv, "--timeout-ms", attachOptions.timeoutMs);
      pushFlag(argv, "--interval-ms", attachOptions.intervalMs);
      return callToolOrRun("game_attach", attachOptions, argv);
    },

    async gameClose(closeOptions = {}) {
      const argv = ["game", "close"];
      pushFlag(argv, "--timeout-ms", closeOptions.timeoutMs);
      pushFlag(argv, "--interval-ms", closeOptions.intervalMs);
      return callToolOrRun("game_close", closeOptions, argv);
    },

    async gameKill(killOptions = {}) {
      const argv = ["game", "kill"];
      pushFlag(argv, "--timeout-ms", killOptions.timeoutMs);
      pushFlag(argv, "--interval-ms", killOptions.intervalMs);
      return run(argv);
    },

    async gameDeploy(deployOptions) {
      assertStringValue(deployOptions?.path, "path", "gameDeploy");

      const argv = ["game", "deploy", String(deployOptions.path)];
      pushBooleanFlag(argv, "--build", deployOptions.build === true);
      pushBooleanFlag(argv, "--restart", deployOptions.restart === true);
      pushBooleanFlag(argv, "--verify", deployOptions.verify === true);
      pushFlag(argv, "--timeout-ms", deployOptions.timeoutMs);
      pushFlag(argv, "--interval-ms", deployOptions.intervalMs);
      return callToolOrRun("game_deploy", deployOptions, argv);
    },

    async inspectActions() {
      return callToolOrRun("inspect_actions", {}, ["inspect", "actions"]);
    },

    async inspectAiTools() {
      if (serviceClient) {
        return runServiceCall(() => serviceClient.inspectAiTools());
      }
      return run(["inspect", "ai-tools"]);
    },

    async inspectViewportPresets(options = {}) {
      const argv = ["inspect", "viewport-presets"];
      pushRepeatedFlag(argv, "--preset-catalog", options.presetCatalogs);
      return callToolOrRun("inspect_viewport_presets", options, argv);
    },

    async inspectReferenceTopics() {
      return callToolOrRun("inspect_reference_topics", {}, ["inspect", "reference-topics"]);
    },

    async state(stateOptions = {}) {
      if (serviceClient) {
        return runServiceCall(() =>
          serviceClient.callTool("state", {
            perspective: stateOptions.perspective,
            playerId: stateOptions.playerId,
          }),
        );
      }
      const argv = ["state"];
      pushPerspectiveArgs(argv, stateOptions);
      return run(argv);
    },

    // `sts2 state actions`: the canonical LIVE action surface derived from the current state
    // (`{ actions: [...], diagnostics: [...] }`). The canonical state envelope itself carries no
    // `actions[]` — that top-level section belonged to the deleted current state — so this is how a
    // caller joins visible objects to executable actions (`actions[].args` / `sourcePath`).
    async stateActions(stateOptions = {}) {
      const argv = ["state", "actions"];
      pushPerspectiveArgs(argv, stateOptions);
      return run(argv);
    },

    // NOTE: presentationCatalog/presentationSnapshot/presentationRender were removed. The whole
    // top-level `presentation` CLI command group was deleted upstream (bcf9a97e) — the TypeScript
    // dev server under presentation/ is the renderer now — so every one of them shelled out to
    // `sts2 presentation …` and could only ever fail with `unrecognized subcommand 'presentation'`.

    async logs(logOptions = {}) {
      const argv = ["dev", "logs"];
      pushFlag(argv, "--limit", logOptions.limit);
      pushFlag(argv, "--tail", logOptions.tail);
      pushFlag(argv, "--after-cursor", logOptions.afterCursor);
      pushFlag(argv, "--level", logOptions.level);
      pushFlag(argv, "--target", logOptions.target);
      return callToolOrRun("logs", logOptions, argv);
    },

    async logHealth(logHealthOptions = {}) {
      const argv = ["dev", "log-health"];
      pushFlag(argv, "--limit", logHealthOptions.limit);
      pushFlag(argv, "--tail", logHealthOptions.tail);
      pushFlag(argv, "--after-cursor", logHealthOptions.afterCursor);
      pushFlag(argv, "--level", logHealthOptions.level);
      pushFlag(argv, "--target", logHealthOptions.target);
      pushRepeatedFlag(argv, "--exclude-target", logHealthOptions.excludeTargets);
      pushRepeatedFlag(argv, "--exclude-message-regex", logHealthOptions.excludeMessageRegexes);
      return callToolOrRun("log_health", logHealthOptions, argv);
    },

    async diagnostics(diagnosticsOptions = {}) {
      const argv = ["dev", "diagnostics"];
      pushFlag(argv, "--limit", diagnosticsOptions.limit);
      pushFlag(argv, "--tail", diagnosticsOptions.tail);
      pushFlag(argv, "--after-cursor", diagnosticsOptions.afterCursor);
      pushFlag(argv, "--level", diagnosticsOptions.level);
      pushFlag(argv, "--target", diagnosticsOptions.target);
      pushRepeatedFlag(argv, "--exclude-target", diagnosticsOptions.excludeTargets);
      pushRepeatedFlag(argv, "--exclude-message-regex", diagnosticsOptions.excludeMessageRegexes);
      pushFlag(argv, "--preset", diagnosticsOptions.preset);
      pushFlag(argv, "--width", diagnosticsOptions.width);
      pushFlag(argv, "--height", diagnosticsOptions.height);
      pushRepeatedFlag(argv, "--preset-catalog", diagnosticsOptions.presetCatalogs);
      pushFlag(argv, "--bundle-dir", diagnosticsOptions.bundleDir);
      return callToolOrRun("diagnostics", diagnosticsOptions, argv);
    },

    async assetsExtract(extractOptions) {
      assertStringValue(extractOptions?.query, "query", "assetsExtract");

      const argv = ["assets", "extract", String(extractOptions.query)];
      pushFlag(argv, "--execution", extractOptions.execution);
      pushFlag(argv, "--format", extractOptions.format);
      pushCodeSearchRootArgs(argv, extractOptions);
      return callToolOrRun("assets_extract", extractOptions, argv);
    },

    async assetsExplain(explainOptions) {
      assertStringValue(explainOptions?.query, "query", "assetsExplain");

      const argv = ["assets", "explain", String(explainOptions.query)];
      pushFlag(argv, "--execution", explainOptions.execution);
      pushCodeSearchRootArgs(argv, explainOptions);
      return callToolOrRun("assets_explain", explainOptions, argv);
    },

    async assetsExtractBatch(batchOptions) {
      assertStringValue(batchOptions?.manifest, "manifest", "assetsExtractBatch");

      const argv = [
        "assets",
        "extract-batch",
        "--manifest",
        String(batchOptions.manifest),
      ];
      pushFlag(argv, "--output", batchOptions.output);
      pushFlag(argv, "--execution", batchOptions.execution);
      pushFlag(argv, "--format", batchOptions.format);
      pushBooleanFlag(argv, "--fail-fast", batchOptions.failFast === true);
      pushCodeSearchRootArgs(argv, batchOptions);
      return callToolOrRun("assets_extract_batch", batchOptions, argv);
    },

    async loadFixture(loadFixtureOptions) {
      assertStringValue(loadFixtureOptions?.path, "path", "loadFixture");
      return callToolOrRun(
        "load_fixture",
        loadFixtureOptions,
        ["dev", "fixture", "load", "--path", String(loadFixtureOptions.path)],
      );
    },

    async hotReloadStatus(options) {
      assertStringValue(options?.project, "project", "hotReloadStatus");
      return callToolOrRun(
        "hot_reload_status",
        options,
        [
          "dev",
          "mod-reload",
          "status",
          "--project",
          String(options.project),
        ],
      );
    },

    async hotReload(options) {
      assertStringValue(options?.project, "project", "hotReload");
      const argv = [
          "dev",
        "mod-reload",
        "--project",
        String(options.project),
      ];
      pushBooleanFlag(argv, "--build", options.build === true);
      pushBooleanFlag(argv, "--wait", options.wait === true);
      pushFlag(argv, "--timeout-ms", options.timeoutMs);
      pushFlag(argv, "--interval-ms", options.intervalMs);
      return callToolOrRun("hot_reload", options, argv);
    },

    async scenarioExport(options) {
      assertStringValue(options?.output, "output", "scenarioExport");
      const argv = [
        "dev",
        "scenario",
        "export",
        "--output",
        String(options.output),
      ];
      pushBooleanFlag(argv, "--include-exact", options.includeExact === true);
      return callToolOrRun("scenario_export", options, argv);
    },

    async scenarioLoad(options) {
      assertStringValue(options?.path, "path", "scenarioLoad");
      const argv = [
        "dev",
        "scenario",
        "load",
        "--path",
        String(options.path),
      ];
      pushBooleanFlag(argv, "--restart", options.restart === true);
      pushFlag(argv, "--timeout-ms", options.timeoutMs);
      pushFlag(argv, "--interval-ms", options.intervalMs);
      pushBooleanFlag(
        argv,
        "--allow-degraded-local-multiplayer",
        options.allowDegradedLocalMultiplayer === true,
      );
      return callToolOrRun("scenario_load", options, argv);
    },

    async debugStatus(options = {}) {
      const argv = ["dev", "debug", "status"];
      pushFlag(argv, "--session", options.session);
      return callToolOrRun("debug_status", options, argv);
    },

    async debugSessionStart(options = {}) {
      const argv = ["dev", "debug", "session", "start"];
      pushFlag(argv, "--name", options.name);
      pushFlag(argv, "--role", options.role);
      if (options.pause) {
        argv.push("--pause");
      }
      pushFlag(argv, "--lease-timeout-ms", options.leaseTimeoutMs);
      if (serviceClient) {
        return runServiceCall(() => serviceClient.debugSessionStart(options));
      }
      return run(argv);
    },

    async debugSessionStatus(options) {
      assertStringValue(options?.sessionId, "sessionId", "debugSessionStatus");
      if (serviceClient) {
        return runServiceCall(() => serviceClient.debugSessionStatus(options.sessionId));
      }
      return run(["dev", "debug", "session", "status", "--id", String(options.sessionId)]);
    },

    async debugSessionEnd(options) {
      assertStringValue(options?.sessionId, "sessionId", "debugSessionEnd");
      const argv = ["dev", "debug", "session", "end", "--id", String(options.sessionId)];
      if (options.resume) {
        argv.push("--resume");
      }
      if (serviceClient) {
        return runServiceCall(() => serviceClient.debugSessionEnd(options.sessionId, options));
      }
      return run(argv);
    },

    async debugEvents(options) {
      assertStringValue(options?.sessionId, "sessionId", "debugEvents");
      const eventOptions = {
        session: options.sessionId,
        fromSequence: options.fromSequence,
        limit: options.limit,
        follow: options.follow,
        timeoutMs: options.timeoutMs,
      };
      const argv = ["dev", "debug", "events", "--session", String(options.sessionId)];
      pushFlag(argv, "--from-sequence", options.fromSequence);
      pushFlag(argv, "--limit", options.limit);
      pushBooleanFlag(argv, "--follow", options.follow === true);
      pushFlag(argv, "--timeout-ms", options.timeoutMs);
      if (serviceClient) {
        return runServiceCall(() => serviceClient.debugEvents(options.sessionId, eventOptions));
      }
      return run(argv);
    },

    async debugPause(options = {}) {
      const argv = ["dev", "debug", "pause"];
      pushFlag(argv, "--session", options.session);
      return callToolOrRun("debug_pause", options, argv);
    },

    async debugResume(options = {}) {
      const argv = ["dev", "debug", "resume"];
      pushFlag(argv, "--session", options.session);
      return callToolOrRun("debug_resume", options, argv);
    },

    async debugStep(debugStepOptions) {
      assertStringValue(debugStepOptions?.kind, "kind", "debugStep");
      const argv = ["dev", "debug", "step", "--kind", String(debugStepOptions.kind)];
      pushFlag(argv, "--session", debugStepOptions.session);
      pushFlag(argv, "--count", debugStepOptions.count);
      return callToolOrRun("debug_step", debugStepOptions, argv);
    },

    async debugWait(options) {
      assertStringValue(options?.sessionId, "sessionId", "debugWait");
      const argv = ["dev", "debug", "wait", "--session", String(options.sessionId)];
      pushFlag(argv, "--timeout-ms", options.timeoutMs);
      if (serviceClient) {
        return runServiceCall(() => serviceClient.debugWait(options.sessionId, options));
      }
      return run(argv);
    },

    async breakpointList(options = {}) {
      const argv = ["dev", "breakpoint", "list"];
      pushFlag(argv, "--session", options.session);
      return callToolOrRun("breakpoint_list", options, argv);
    },

    async breakpointAdd(breakpointOptions) {
      assertStringValue(breakpointOptions?.path, "path", "breakpointAdd");
      const argv = ["dev", "breakpoint", "add", "--path", String(breakpointOptions.path)];
      pushFlag(argv, "--session", breakpointOptions.session);
      pushFlag(argv, "--kind", breakpointOptions.kind);
      pushFlag(argv, "--name", breakpointOptions.name);
      pushFlag(argv, "--min-hit-count", breakpointOptions.minHitCount);
      if (breakpointOptions?.autoRemoveOnHit) {
        argv.push("--auto-remove-on-hit");
      }
      if (String(breakpointOptions?.kind ?? "match") !== "change") {
        argv.push(...predicateArgv(breakpointOptions ?? {}, "breakpointAdd"));
      }
      return callToolOrRun("breakpoint_add", breakpointOptions, argv);
    },

    async breakpointRemove(breakpointOptions) {
      assertStringValue(breakpointOptions?.id, "id", "breakpointRemove");
      const argv = ["dev", "breakpoint", "remove", "--id", String(breakpointOptions.id)];
      pushFlag(argv, "--session", breakpointOptions.session);
      return callToolOrRun(
        "breakpoint_remove",
        breakpointOptions,
        argv,
      );
    },

    async http(httpOptions) {
      assertStringValue(httpOptions?.url, "url", "http");
      const argv = ["dev", "http", "--url", String(httpOptions.url)];
      pushFlag(argv, "--method", httpOptions.method);
      pushRepeatedFlag(argv, "--header", httpOptions.headers);
      pushFlag(argv, "--body", httpOptions.body);
      pushFlag(argv, "--timeout-ms", httpOptions.timeoutMs);
      pushFlag(argv, "--expect-status", httpOptions.expectStatus);
      pushRepeatedFlag(argv, "--expect-header", httpOptions.expectHeaders);
      pushFlag(argv, "--query", httpOptions.query);
      argv.push(...optionalPredicateArgv(httpOptions, "http"));
      return callToolOrRun("http", httpOptions, argv);
    },

    async httpWait(httpWaitOptions) {
      assertStringValue(httpWaitOptions?.url, "url", "httpWait");
      const argv = ["dev", "http-wait", "--url", String(httpWaitOptions.url)];
      pushFlag(argv, "--method", httpWaitOptions.method);
      pushRepeatedFlag(argv, "--header", httpWaitOptions.headers);
      pushFlag(argv, "--body", httpWaitOptions.body);
      pushFlag(argv, "--timeout-ms", httpWaitOptions.timeoutMs);
      pushFlag(argv, "--expect-status", httpWaitOptions.expectStatus);
      pushRepeatedFlag(argv, "--expect-header", httpWaitOptions.expectHeaders);
      pushFlag(argv, "--query", httpWaitOptions.query);
      argv.push(...optionalPredicateArgv(httpWaitOptions, "httpWait"));
      pushFlag(argv, "--interval-ms", httpWaitOptions.intervalMs);
      return callToolOrRun("http_wait", httpWaitOptions, argv);
    },

    async fetch(fetchOptions) {
      assertStringValue(fetchOptions?.source, "source", "fetch");
      const argv = ["dev", "fetch", "--source", String(fetchOptions.source)];
      pushFlag(argv, "--output", fetchOptions.output);
      pushFlag(argv, "--expect-sha256", fetchOptions.expectSha256);
      pushFlag(argv, "--query", fetchOptions.query);
      argv.push(...optionalPredicateArgv(fetchOptions, "fetch"));
      return callToolOrRun("fetch", fetchOptions, argv);
    },

    async websocket(websocketOptions) {
      assertStringValue(websocketOptions?.url, "url", "websocket");
      const argv = ["dev", "websocket", "--url", String(websocketOptions.url)];
      pushRepeatedFlag(argv, "--header", websocketOptions.headers);
      pushRepeatedFlag(argv, "--send-text", websocketOptions.sendText);
      pushRepeatedFlag(argv, "--expect-text", websocketOptions.expectText);
      pushFlag(argv, "--timeout-ms", websocketOptions.timeoutMs);
      return callToolOrRun("websocket", websocketOptions, argv);
    },

    async projectHookList() {
      return callToolOrRun("project_hook_list", {}, ["project", "hook", "list"]);
    },

    async projectProfileList() {
      return callToolOrRun("project_profile_list", {}, ["project", "profile", "list"]);
    },

    async projectProfileShow(projectProfileOptions) {
      assertStringValue(projectProfileOptions?.name, "name", "projectProfileShow");
      return callToolOrRun(
        "project_profile_show",
        projectProfileOptions,
        ["project", "profile", "show", String(projectProfileOptions.name)],
      );
    },

    async projectProfileRun(projectProfileOptions) {
      assertStringValue(projectProfileOptions?.name, "name", "projectProfileRun");
      return callToolOrRun(
        "project_profile_run",
        projectProfileOptions,
        ["project", "profile", "run", String(projectProfileOptions.name)],
      );
    },

    async projectHookShow(projectHookOptions) {
      assertStringValue(projectHookOptions?.name, "name", "projectHookShow");
      return callToolOrRun(
        "project_hook_show",
        projectHookOptions,
        ["project", "hook", "show", String(projectHookOptions.name)],
      );
    },

    async projectHookRun(projectHookOptions) {
      assertStringValue(projectHookOptions?.name, "name", "projectHookRun");
      const argv = ["project", "hook", "run", String(projectHookOptions.name)];
      const serializedInput = serializeOptionalJsonInput(projectHookOptions.input, "projectHookRun");
      pushFlag(argv, "--input", serializedInput);
      return callToolOrRun("project_hook_run", projectHookOptions, argv);
    },

    async skillInstall(skillInstallOptions = {}) {
      const argv = ["skill", "install"];
      pushFlag(argv, "--path", skillInstallOptions.path);
      return callToolOrRun("skill_install", skillInstallOptions, argv);
    },

    async screenshot(screenshotOptions = {}) {
      const argv = ["dev", "screenshot"];
      pushFlag(argv, "--preset", screenshotOptions.preset);
      pushFlag(argv, "--width", screenshotOptions.width);
      pushFlag(argv, "--height", screenshotOptions.height);
      pushRepeatedFlag(argv, "--preset-catalog", screenshotOptions.presetCatalogs);
      pushFlag(argv, "--rpc-timeout-ms", screenshotOptions.rpcTimeoutMs);
      pushFlag(argv, "--output", screenshotOptions.output);
      return callToolOrRun("screenshot", screenshotOptions, argv);
    },

    async screenshotDiff(screenshotDiffOptions) {
      assertStringValue(screenshotDiffOptions?.baseline, "baseline", "screenshotDiff");
      const argv = [
        "dev",
        "screenshot-diff",
        "--baseline",
        String(screenshotDiffOptions.baseline),
      ];
      pushFlag(argv, "--actual", screenshotDiffOptions.actual);
      pushFlag(argv, "--preset", screenshotDiffOptions.preset);
      pushFlag(argv, "--width", screenshotDiffOptions.width);
      pushFlag(argv, "--height", screenshotDiffOptions.height);
      pushRepeatedFlag(argv, "--preset-catalog", screenshotDiffOptions.presetCatalogs);
      pushFlag(argv, "--rpc-timeout-ms", screenshotDiffOptions.rpcTimeoutMs);
      pushFlag(argv, "--bundle-dir", screenshotDiffOptions.bundleDir);
      pushFlag(argv, "--max-diff-pixels", screenshotDiffOptions.maxDiffPixels);
      pushFlag(argv, "--max-diff-ratio", screenshotDiffOptions.maxDiffRatio);
      return callToolOrRun("screenshot_diff", screenshotDiffOptions, argv);
    },

    async snapshotExport(snapshotExportOptions) {
      assertStringValue(snapshotExportOptions?.spec, "spec", "snapshotExport");
      assertStringValue(snapshotExportOptions?.output, "output", "snapshotExport");
      const argv = [
        "dev",
        "snapshot",
        "export",
        "--spec",
        String(snapshotExportOptions.spec),
        "--output",
        String(snapshotExportOptions.output),
      ];
      pushRepeatedFlag(argv, "--preset-catalog", snapshotExportOptions.presetCatalogs);
      return callToolOrRun("snapshot_export", snapshotExportOptions, argv);
    },

    async snapshotCompare(snapshotCompareOptions) {
      assertStringValue(snapshotCompareOptions?.spec, "spec", "snapshotCompare");
      assertStringValue(snapshotCompareOptions?.baseline, "baseline", "snapshotCompare");
      const argv = [
        "dev",
        "snapshot",
        "compare",
        "--spec",
        String(snapshotCompareOptions.spec),
        "--baseline",
        String(snapshotCompareOptions.baseline),
      ];
      pushRepeatedFlag(argv, "--preset-catalog", snapshotCompareOptions.presetCatalogs);
      pushFlag(argv, "--bundle-dir", snapshotCompareOptions.bundleDir);
      return callToolOrRun("snapshot_compare", snapshotCompareOptions, argv);
    },

    async testStress(testStressOptions) {
      assertStringValue(testStressOptions?.path, "path", "testStress");
      const hasIterations = testStressOptions?.iterations !== undefined;
      const hasDuration = testStressOptions?.durationMs !== undefined;
      if (hasIterations === hasDuration) {
        throw new TypeError("testStress requires exactly one of iterations or durationMs");
      }

      const argv = ["test", "stress"];
      pushFlag(argv, "--artifacts-dir", testStressOptions.artifactsDir);
      pushFlag(argv, "--failure-artifacts", testStressOptions.failureArtifacts);
      pushFlag(argv, "--iterations", testStressOptions.iterations);
      pushFlag(argv, "--duration-ms", testStressOptions.durationMs);
      pushFlag(argv, "--max-failures", testStressOptions.maxFailures);
      pushFlag(argv, "--cooldown-ms", testStressOptions.cooldownMs);
      argv.push(String(testStressOptions.path));
      return callToolOrRun("test_stress", testStressOptions, argv);
    },

    async waitFor(waitOptions) {
      assertPath(waitOptions?.path, "waitFor");
      const argv = [
        "dev",
        "wait-for",
        waitOptions.path,
        ...predicateArgv(waitOptions, "waitFor"),
      ];

      pushFlag(argv, "--timeout-ms", waitOptions.timeoutMs);
      pushFlag(argv, "--interval-ms", waitOptions.intervalMs);
      pushPerspectiveArgs(argv, waitOptions);

      return callToolOrRun("wait_for", waitOptions, argv);
    },

    async assert(assertOptions) {
      assertPath(assertOptions?.path, "assert");
      const argv = [
        "dev",
        "assert",
        assertOptions.path,
        ...predicateArgv(assertOptions, "assert"),
      ];

      pushPerspectiveArgs(argv, assertOptions);

      return callToolOrRun("assert", assertOptions, argv);
    },

    async codeLocate(codeOptions) {
      assertRequired(codeOptions?.subject, "subject", "codeLocate");
      assertRequired(codeOptions?.query, "query", "codeLocate");

      const argv = ["code", "locate", codeOptions.subject, codeOptions.query];
      pushFlag(argv, "--limit", codeOptions.limit);
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_locate", codeOptions, argv);
    },

    async codeDescribe(codeOptions) {
      assertRequired(codeOptions?.subject, "subject", "codeDescribe");
      assertRequired(codeOptions?.query, "query", "codeDescribe");

      const argv = ["code", "describe", codeOptions.subject, codeOptions.query];
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_describe", codeOptions, argv);
    },

    async codeRefs(codeOptions) {
      assertRequired(codeOptions?.subject, "subject", "codeRefs");
      assertRequired(codeOptions?.query, "query", "codeRefs");

      const argv = ["code", "refs", codeOptions.subject, codeOptions.query];
      pushFlag(argv, "--limit", codeOptions.limit);
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_refs", codeOptions, argv);
    },

    async codeDerived(codeOptions) {
      assertRequired(codeOptions?.subject, "subject", "codeDerived");
      assertRequired(codeOptions?.query, "query", "codeDerived");

      const argv = ["code", "derived", codeOptions.subject, codeOptions.query];
      pushFlag(argv, "--limit", codeOptions.limit);
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_derived", codeOptions, argv);
    },

    async codeDecompile(codeOptions) {
      assertRequired(codeOptions?.subject, "subject", "codeDecompile");
      assertRequired(codeOptions?.query, "query", "codeDecompile");

      const argv = ["code", "decompile", codeOptions.subject, codeOptions.query];
      pushBooleanFlag(argv, "--full", codeOptions.full === true);
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_decompile", codeOptions, argv);
    },

    async codeHooks(codeOptions = {}) {
      const argv = ["code", "hooks"];
      if (codeOptions.query !== undefined && codeOptions.query !== null) {
        argv.push(codeOptions.query);
      }
      pushFlag(argv, "--limit", codeOptions.limit);
      pushFlag(argv, "--offset", codeOptions.offset);
      pushFlag(argv, "--source", codeOptions.source);
      pushFlag(argv, "--assembly", codeOptions.assembly);
      for (const form of codeOptions.form ?? []) {
        pushFlag(argv, "--form", form);
      }
      pushBooleanFlag(argv, "--has-script", codeOptions.hasScript === true);
      pushFlag(argv, "--sort", codeOptions.sort);
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_hooks", codeOptions, argv);
    },

    async codeHookInfo(codeOptions) {
      assertRequired(codeOptions?.query, "query", "codeHookInfo");

      const argv = ["code", "hook-info", codeOptions.query];
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_hook_info", codeOptions, argv);
    },

    async codeSceneSearch(codeOptions) {
      assertRequired(codeOptions?.query, "query", "codeSceneSearch");

      const argv = ["code", "scene-search", codeOptions.query];
      pushFlag(argv, "--limit", codeOptions.limit);
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_scene_search", codeOptions, argv);
    },

    async codeSceneTree(codeOptions) {
      assertRequired(codeOptions?.scene, "scene", "codeSceneTree");

      const argv = ["code", "scene-tree", codeOptions.scene];
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_scene_tree", codeOptions, argv);
    },

    async codeSceneNode(codeOptions) {
      assertRequired(codeOptions?.scene, "scene", "codeSceneNode");
      assertRequired(codeOptions?.nodePath, "nodePath", "codeSceneNode");

      const argv = ["code", "scene-node", codeOptions.scene, codeOptions.nodePath];
      pushCodeSearchRootArgs(argv, codeOptions);
      return callToolOrRun("code_scene_node", codeOptions, argv);
    },

    async testRun(runOptions) {
      if (serviceClient) {
        return runServiceCall(() => serviceClient.testRun(runOptions));
      }
      const argv = ["test", "run"];
      pushFlag(argv, "--artifacts-dir", runOptions.artifactsDir);
      pushFlag(argv, "--failure-artifacts", runOptions.failureArtifacts);
      argv.push(...testRunInputArgv(runOptions));
      return run(argv);
    },

    async submitTestRun(runOptions) {
      if (!serviceClient) {
        throw new TypeError("submitTestRun requires serviceUrl.");
      }
      return runServiceCall(() => serviceClient.submitTestRun(runOptions));
    },

    async waitForTestRun(runId) {
      if (!serviceClient) {
        throw new TypeError("waitForTestRun requires serviceUrl.");
      }
      return runServiceCall(() => serviceClient.waitForTestRun(runId));
    },

    async listTestRunArtifacts(runId) {
      if (!serviceClient) {
        throw new TypeError("listTestRunArtifacts requires serviceUrl.");
      }
      return runServiceCall(() => serviceClient.listTestRunArtifacts(runId));
    },

    async downloadTestRunArtifact(runId, relativePath) {
      if (!serviceClient) {
        throw new TypeError("downloadTestRunArtifact requires serviceUrl.");
      }
      return runServiceCall(() => serviceClient.downloadTestRunArtifact(runId, relativePath));
    },

    async devConsole(consoleOptions) {
      assertStringValue(consoleOptions?.command, "command", "devConsole");
      const mode = consoleOptions.mode;
      if (mode !== undefined && mode !== "dangerous") {
        throw new TypeError("devConsole mode must be 'dangerous' when supplied.");
      }

      const args = Array.isArray(consoleOptions.args)
        ? consoleOptions.args.map(String)
        : [];
      const argv = ["dev", "console", String(consoleOptions.command), ...args];
      if (mode === "dangerous") {
        argv.unshift("dangerous");
        argv.unshift("--mode");
      }
      return callToolOrRun("console", { ...consoleOptions, args }, argv);
    },

    async act(action) {
      const normalized = normalizeAction(action);
      if (!normalized?.kind) {
        throw new TypeError("act requires an action object with a kind.");
      }

      if (serviceClient) {
        return runServiceCall(() =>
          serviceClient.callTool("act", normalizedActionToolArgs(normalized)),
        );
      }

      switch (normalized.kind) {
        case "choose": {
          const choice = normalized.choiceId;
          assertRequired(choice, "choice or choiceId", "act({ kind: 'choose' })");
          const argv = ["act", "choose", "--choice", choice];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "confirm-selection": {
          const argv = ["act", "confirm-selection"];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "cancel-selection": {
          const argv = ["act", "cancel-selection"];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "mouse-click": {
          if (normalized.x === undefined || normalized.x === null) {
            throw new TypeError("act({ kind: 'mouse-click' }) requires x.");
          }
          if (normalized.y === undefined || normalized.y === null) {
            throw new TypeError("act({ kind: 'mouse-click' }) requires y.");
          }

          const argv = [
            "act",
            "mouse",
            "click",
            "--x",
            normalized.x,
            "--y",
            normalized.y,
          ];
          pushFlag(argv, "--button", normalized.button);
          return run(argv);
        }
        case "select-map-node": {
          const node = normalized.mapNodeId;
          assertRequired(node, "node or mapNodeId", "act({ kind: 'select-map-node' })");
          const argv = ["act", "select-map-node", "--node", node];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "end-turn": {
          const argv = ["act", "end-turn"];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "ready": {
          const argv = ["act", "ready"];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "unready": {
          const argv = ["act", "unready"];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "select-character": {
          const character = normalized.characterId;
          assertRequired(
            character,
            "character or characterId",
            "act({ kind: 'select-character' })",
          );
          const argv = ["act", "select-character", "--character", character];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "claim-reward": {
          const reward = normalized.rewardId;
          assertRequired(reward, "reward or rewardId", "act({ kind: 'claim-reward' })");
          const argv = ["act", "claim-reward", "--reward", reward];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "skip-rewards": {
          const argv = ["act", "skip-rewards"];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "select-card": {
          const card = normalized.cardId;
          assertRequired(card, "card or cardId", "act({ kind: 'select-card' })");
          const argv = ["act", "select-card", "--card", card];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "skip-card-selection": {
          const argv = ["act", "skip-card-selection"];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "select-bundle": {
          const bundle = normalized.bundleId;
          assertRequired(bundle, "bundle or bundleId", "act({ kind: 'select-bundle' })");
          const argv = ["act", "select-bundle", "--bundle", bundle];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "buy-card":
        case "buy-relic":
        case "buy-potion":
        case "remove-card": {
          const shopItem = normalized.shopItemId;
          assertRequired(shopItem, "shopItem or shopItemId", `act({ kind: '${normalized.kind}' })`);
          const argv = ["act", normalized.kind, "--shop-item", shopItem];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "leave-shop":
        case "close-shop-inventory":
        case "rest":
        case "proceed-rest-site":
        case "open-chest":
        case "proceed-treasure-room":
        case "back-from-map":
        case "proceed-event":
        case "toggle-map":
        case "toggle-deck":
        case "toggle-settings": {
          const argv = ["act", normalized.kind];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "smith": {
          const card = normalized.cardId;
          const argv = ["act", "smith"];
          pushFlag(argv, "--card", card);
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "use-rest-site-option": {
          const restOption = normalized.restOptionId;
          assertRequired(
            restOption,
            "restOption or restOptionId",
            "act({ kind: 'use-rest-site-option' })",
          );
          const argv = ["act", "use-rest-site-option", "--rest-option", restOption];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "take-relic": {
          const relic = normalized.relicId;
          assertRequired(relic, "relic or relicId", "act({ kind: 'take-relic' })");
          const argv = ["act", "take-relic", "--relic", relic];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "select-event-option":
        case "open-event-shop": {
          const eventOption = normalized.eventOptionId;
          assertRequired(
            eventOption,
            "eventOption or eventOptionId",
            `act({ kind: '${normalized.kind}' })`,
          );
          const argv = ["act", normalized.kind, "--event-option", eventOption];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "use-crystal-sphere-control": {
          const control = normalized.controlId;
          assertRequired(
            control,
            "control or controlId",
            "act({ kind: 'use-crystal-sphere-control' })",
          );
          const argv = ["act", "use-crystal-sphere-control", "--control", control];
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "play-card": {
          const card = normalized.cardId;
          assertRequired(card, "card or cardId", "act({ kind: 'play-card' })");

          const argv = ["act", "play-card", "--card", card];
          pushFlag(argv, "--target", normalized.targetId);
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        case "use-potion": {
          const potion = normalized.potionId;
          assertRequired(potion, "potion or potionId", "act({ kind: 'use-potion' })");

          const argv = ["act", "use-potion", "--potion", potion];
          pushFlag(argv, "--target", normalized.targetId);
          pushActionPlayerArgs(argv, normalized);
          return run(argv);
        }
        default:
          throw new TypeError(`Unsupported action kind: ${normalized.kind}`);
      }
    },
  };
}

export { Sts2CliError, Sts2ServiceError };
