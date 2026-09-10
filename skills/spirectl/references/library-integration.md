# Library Integration

Use this reference when consuming spirectl as a Node helper, MCP/service adapter, or embedded .NET runtime.

## Node Helper

The npm wrapper is thin and CLI-first:

```js
import { createSts2Client } from "@spirectl/sts2";

const sts2 = createSts2Client({
  cwd: process.cwd(),
  configPath: "./sts2.local.yaml",
});
```

It resolves the `sts2` binary from explicit `binaryPath`, `STS2_BINARY_PATH`, packaged `dist/<platform>/sts2`, then repo-local `target/debug` or `target/release`. It forces `--json`, returns parsed CLI payloads, and preserves structured nonzero CLI failures as `Sts2CliError`.

Use `inspectAiTools()` for the machine-facing catalog. Use `state()` for current state, `act(...)` for semantic mutation, and `testRun(...)` / `testStress(...)` for scenario execution. Do not add adapter-local business schemas when the CLI already owns the JSON contract.

## Automation Service And MCP

The optional service backend keeps the same catalog and payloads:

```bash
"${STS2_BIN[@]}" --json service serve --listen 127.0.0.1:4317 --auth-token local-dev-token
```

The Node helper can target it with `serviceUrl` / `serviceToken` or `STS2_SERVICE_URL` / `STS2_SERVICE_TOKEN`.

`sts2-mcp` is a thin MCP adapter over the same wrapper and CLI/service surfaces. Local stdio is the default. Network MCP is explicit opt-in, loopback by default, bearer-gated for non-loopback binds, and should preserve the same structured tool results.

Result preservation rules:

- Successful tool calls return the unchanged CLI/service JSON payload.
- Expected CLI failures keep exit code, argv, stderr, and structured payload.
- MCP must not wrap successful payloads in a second domain schema.
- Preserve action failures, notices, restore diagnostics, debugger lease errors, remote artifact metadata, and service errors exactly enough for callers to make retry or legality decisions.

Use `inspect ai-tools` / `GET /v0/ai-tools` for standalone AI tools. Do not expose full `inspect commands`, raw input, force kill, completion setup, recorded local fixtures, or dev scene internals as AI tools unless a later repo spec promotes them.

## Embedded .NET Runtime

Downstream STS2 mods can use the in-process runtime without starting CLI, IPC, service, or MCP:

```csharp
using Spirectl.Sts2;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Core.Models;

ISpirectlRuntime runtime = Sts2EmbeddableRuntimeFactory.Create();
var capabilities = runtime.GetCapabilities();
var state = runtime.GetCurrentState(new CurrentStateRequest());
var models = runtime.GetModels(new ModelCatalogRequestSnapshot());
```

Keep this aggregate at the composition root. Pass focused ports such as `IRuntimeStateSource`,
`IAnimationHintSource`, or `ISemanticActionSource` to services. Model/reference and Spine catalog/baking ports
are independent. Use `runtime.SceneWatchControls` for coupled producer settings; viewer policy remains in the host.

Stable facade surface:

- `GetCapabilities()`
- `GetCurrentState(CurrentStateRequest)`, plus `SubscribeCurrentState(...)` / `WatchCurrentStateAsync(...)`
- `GetModels(ModelCatalogRequestSnapshot)` and `GetReference(ReferenceRequestSnapshot)`
- `GetSpineCatalog(SpineCatalogRequestSnapshot)`
- `SubscribeRuntimeSceneDelta(...)` for live Godot scene state
- `GetPresentationAssets(PresentationAssetBatchRequest)`
- `ExecuteAction(EmbeddableActionRequest)`
- `Assets.GetAsset(...)` and `Assets.GetAssets(...)`

Call `GetCapabilities()` first. Unsupported features must be explicit and handled by downstream code.

Use `GetCurrentState` for semantic truth, `SubscribeRuntimeSceneDelta` for the renderable live scene tree, `GetPresentationAssets` or `Assets.GetAsset` for bytes behind opaque asset keys, and `ExecuteAction` for legality-checked mutation. Rendering is not provided here: product routes, sessions, auth, browser DTOs, cache policy, CSS, fallback art, visual thresholds, and asset URL shapes stay downstream-owned.

Do not copy game or Godot assemblies from this repository into downstream packages. Resolve `sts2.dll`, `GodotSharp.dll`, and native Godot dependencies from the running game/mod loader context.
