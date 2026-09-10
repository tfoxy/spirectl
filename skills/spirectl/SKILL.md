---
name: spirectl
description: Use for Slay the Spire 2 automation with spirectl as a CLI, Node/MCP/service adapter, or embedded .NET runtime. Start here for live state/actions, tests, asset extraction and visual evidence, static modding inspection, debugging, lifecycle, and repo-local skill installation.
---

# spirectl

Use this skill for Slay the Spire 2 automation through `sts2`, the Node wrapper, MCP/service adapters, or the embedded .NET runtime.

Keep this file as the router. Load only one reference file unless the task clearly spans areas:

- `references/cli-runtime.md`: command resolution, game lifecycle, compact `state`, `actions[]`, semantic `act`, fallback choose.
- `references/library-integration.md`: Node `createSts2Client`, service backend, MCP preservation rules, embedded .NET `ISpirectlRuntime`.
- `references/render-assets.md`: asset keys/extraction, screenshots, viewport presets, screenshot diff.
- `references/testing-debugging.md`: `test run|stress`, fixtures/scenarios, waits/asserts, diagnostics, hot reload, debugger sessions, breakpoints.
- `references/static-modding.md`: `inspect reference-topics`, staged code inspection, hook discovery, static scenes/resources, project recover/scaffold.

## Command Resolution

Resolve the command prefix before examples. In this repository, prefer the repo-local Rust binary when `sts2` is not already installed:

```bash
if command -v sts2 >/dev/null 2>&1; then
  STS2_BIN=(sts2)
elif [ -f Cargo.toml ] && cargo metadata --no-deps >/dev/null 2>&1; then
  STS2_BIN=(cargo run -q -p sts2 --)
elif command -v npx >/dev/null 2>&1; then
  STS2_BIN=(npx @spirectl/sts2)
else
  echo "spirectl requires an installed 'sts2', a repo-local Cargo workspace, or npx." >&2
  exit 1
fi
```

Use `"${STS2_BIN[@]}"` as the command prefix in later shell examples.

## First Discovery

Prefer self-description before repo docs:

```bash
"${STS2_BIN[@]}" --json inspect ai-tools
"${STS2_BIN[@]}" --json inspect actions
"${STS2_BIN[@]}" --json inspect state-schema
"${STS2_BIN[@]}" --json inspect reference-topics
```

Use `--json` for anything consumed by tools, scripts, agents, MCP, or tests. In the repo, read `docs/agent-quickstart.md` before broader docs, then only the relevant `docs/maps/` file.

## Safety

- Read compact `state` and `inspect actions` before `act`; choose from `actions[].kind` plus `actions[].args`.
- Use fallback `choose` only for generic, modded, or unmodeled visible controls that have no modeled semantic action.
- Raw mouse input requires explicit dangerous mode (`--mode dangerous`) and should not be used as an implicit recovery path.
- Preserve structured errors, `actionFailure.reasonCode`, notices, restore diagnostics, and adapter payloads; do not collapse them into prose.
- Do not silently mutate game files or shell startup files.
- Machine-specific game and mods paths belong in ignored local config such as `sts2.local.yaml`.
