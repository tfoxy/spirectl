# Automation Service

`sts2 service serve` runs a CLI-owned HTTP/JSON automation service on top of the existing Rust command and runner helpers.

This service extends the current CLI-first stack for remote access. It does not replace `sts2 --json ...`, it does not introduce a second JS runner, and it does not turn `sts2-mcp` into a hosted MCP product.

## Start The Service

Loopback-only without auth:

```bash
sts2 --json service serve
sts2 --json service serve --listen 127.0.0.1:4317
```

Explicit bearer auth:

```bash
sts2 --json service serve --listen 127.0.0.1:4317 --auth-token local-dev-token
```

Non-loopback binds require `--auth-token`.

Startup prints one JSON line in `--json` mode with:

- `service`
- `version`
- `sourceOfTruth`
- `baseUrl`
- `listenAddress`
- `artifactRoot`
- `jobStore`
- `recovery`
- `auth`

Example:

```json
{"service":"sts2","version":"0.1.0","sourceOfTruth":"sts2 CLI","baseUrl":"http://127.0.0.1:4317","listenAddress":"127.0.0.1:4317","artifactRoot":"/abs/path/.sts2/artifacts","jobStore":{"mode":"durable","root":"/abs/path/.sts2/artifacts/remote-jobs"},"recovery":{"recoveredJobs":0,"orphanedJobs":0,"unknownJobs":0,"warnings":[]},"auth":{"mode":"bearer","required":true}}
```

## Auth And Transport

- Default docs and tests use loopback binds only.
- When `--auth-token` is set, requests must send `Authorization: Bearer <token>`.
- The service does not implement TLS. Use a trusted reverse proxy or tunnel if remote transport needs TLS.
- Local `sts2-mcp` stdio remains the default safe MCP path for one-machine AI use.
- Network MCP is an explicit opt-in service posture for remote/team use. Its threat model is a long-lived process that can expose AI-approved game automation, dev diagnostics, and remote runner entrypoints to any client that can reach the bind address and satisfy auth.
- Network MCP defaults to loopback. Non-loopback binds require both bearer auth and an operator acknowledgement that the service is reachable beyond the local host.
- Bearer tokens are transport credentials. Pass them through process environment, a local secret manager, or service supervisor configuration; do not commit them to config examples.
- The service does not implement TLS for MCP. If MCP leaves loopback, TLS termination, firewall rules, request logging, rate limiting, and reverse-proxy access policy are operator responsibilities.
- Network MCP reuses the same approved AI-tool catalog and service contracts as `GET /v0/ai-tools`, `/v0/tools/call`, and the durable remote `test run` endpoints. It does not create a second action, runner, artifact, or restore schema.

Loopback network MCP:

```bash
sts2 --json service serve --mcp-mode network --mcp-listen 127.0.0.1:4318
```

Non-loopback network MCP with explicit auth and threat-model acknowledgement:

```bash
sts2 --json service serve \
  --listen 127.0.0.1:4317 \
  --mcp-mode network \
  --mcp-listen 0.0.0.0:4318 \
  --mcp-auth-token "$STS2_MCP_AUTH_TOKEN" \
  --acknowledge-non-loopback-mcp-threat-model
```

Config may set the same posture without machine-specific game paths:

```yaml
service:
  mcp:
    mode: network
    listen: 127.0.0.1:4318
    authToken: ${STS2_MCP_AUTH_TOKEN}
    acknowledgeNonLoopbackThreatModel: false
```

## Endpoints

Automation metadata and tool calls:

- `GET /v0/service`
- `GET /v0/ai-tools`
- `POST /v0/tools/call`
- `GET|POST|DELETE /v0/mcp` when `--mcp-mode network` is enabled

Remote runner jobs:

- `POST /v0/test-runs`
- `GET /v0/test-runs/{runId}`
- `GET /v0/test-runs/{runId}/artifacts`
- `GET /v0/test-runs/{runId}/artifacts/{relativePath...}`

Debugger session workflows:

- `POST /v0/debug-sessions`
- `GET /v0/debug-sessions/{id}`
- `DELETE /v0/debug-sessions/{id}`
- `GET /v0/debug-sessions/{id}/events`
- `POST /v0/debug-sessions/{id}/events`
- `POST /v0/debug-sessions/{id}/wait`

## AI Tool Catalog And Calls

- `GET /v0/ai-tools` returns the same catalog shape as `sts2 --json inspect ai-tools`.
- `POST /v0/tools/call` uses the same AI-tool names and JSON input shapes described by that catalog.
- `POST /v0/tools/call` dispatches the shipped non-streaming AI-tool catalog through the same CLI-owned command helpers, except `test_run`.
- Runtime callers should use the returned `state` typed sections and `preferredAction` references before calling `act`; S86 reward/card, shop, rest-site, treasure/relic, map, and event-room controls are modeled as intent verbs. Reserve fallback `choose` for generic, modded, or unmodeled visible controls with no modeled first-party action.
- Remote `test_run` is intentionally async-only on the service: `/v0/tools/call` rejects `name: "test_run"` and points callers at `POST /v0/test-runs` so polling and bounded artifact download stay on one contract.
- The dedicated debugger-session endpoints exist because leased-session workflows are easier to model as stable REST resources than as opaque free-form tool calls alone. They reuse the same underlying CLI-owned debug helpers and response payloads. See [debugging](./debugging.md) for the leased-session operator workflow behind those endpoints.
- Tool-call success returns the normal structured JSON payload from the underlying CLI-owned helper path.
- Validation or execution failures return structured JSON errors; they are not translated into a second business schema.

Example:

```bash
curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/ai-tools

curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/tools/call \
  -d '{"name":"game_info","arguments":{}}'
```

Hot-reload calls use the same catalog-backed tool-call endpoint; there is no separate hot-reload HTTP API:

```bash
curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/tools/call \
  -d '{"name":"hot_reload","arguments":{"project":"./mods/MyHotMod","build":true,"wait":true}}'
```

`hot_reload` and `hot_reload_status` preserve the developer CLI boundary for M57/M60 shell-supported projects. They do not deploy shells, restart the game, edit source files, or mutate arbitrary game files; failed reload payloads keep the CLI report that says whether the previous generation remains active and whether a restart is required.

Console execution also uses `/v0/tools/call` through the AI catalog:

```bash
curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/tools/call \
  -d '{"name":"console","arguments":{"command":"help","args":["draw"]}}'
```

`console` preserves the CLI mode boundary: use normal mode for normal developer-console commands, and dangerous mode only for persisted-file commands such as `achievement`, `cloud`, and `unlock`.

Debugger-session example:

```bash
curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/debug-sessions \
  -d '{"name":"smoke-investigation","pause":true,"leaseTimeoutMs":30000}'

curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/debug-sessions/dbg:1

curl -H 'Authorization: Bearer local-dev-token' \
  'http://127.0.0.1:4317/v0/debug-sessions/dbg:1/events?fromSequence=42&limit=50&follow=false&timeoutMs=1000'

curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/debug-sessions/dbg:1/events \
  -d '{"fromSequence":42,"limit":50,"follow":true,"timeoutMs":1000}'

curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/debug-sessions/dbg:1/wait \
  -d '{"timeoutMs":5000}'
```

`POST /v0/debug-sessions` accepts `role` as `controller` or `observer`; omitting it preserves the controller default used by the CLI. The event endpoint accepts `fromSequence`, `limit`, `follow`, and `timeoutMs` either as GET query parameters or POST JSON body fields. `follow` is still bounded by `timeoutMs`, so service requests do not become unbounded streams.

Event responses are the same JSON shape as `sts2 --json dev debug events` and `/v0/tools/call` with `name: "debug_events"`: `events`, `fromSequence`, `nextSequence`, `oldestRetainedSequence`, `newestSequence`, `retention.oldestSequence`, `retention.newestSequence`, `retention.limit`, `expired`, `overflow`, `follow`, `timedOut`, `timeoutMs`, and `notices`. Unsupported or mock hosts return structured notice payloads such as `debug_event_stream_unavailable`; bridge validation failures, including controller lease conflicts when a host supports leased debugger sessions, remain structured JSON errors rather than transport failures.

## Remote `test run`

`POST /v0/test-runs` accepts the same scenario-source contract already supported by `sts2 test run`:

- `path`
- `inline`
- `artifactsDir`
- `failureArtifacts`
- `durable`

By default the service keeps job state in memory for the lifetime of that service process only. Start with `--job-store durable` to persist remote job metadata under the configured artifact root:

```bash
sts2 --json service serve --job-store durable
sts2 --json service serve --job-store durable --job-store-dir remote-jobs
```

The same default can be set in config:

```yaml
service:
  jobStore:
    mode: durable
    dir: remote-jobs
```

`--job-store-dir` and `service.jobStore.dir` are resolved under `artifacts.dir`; absolute paths are accepted only when they still stay under that artifact root. Paths containing `..` are rejected.

Durable requests may also set `"durable": true`. That flag is a guardrail for callers that require restart recovery: the service rejects the request with `durable_job_store_not_enabled` unless the current service was started with durable storage.

Submission response:

- `runId`
- `status`

Polling response:

- `queued` or `running` while work is still in progress
- `completed` plus `summary` when the run finished
- `failed` plus `error` if the service task itself failed before it could produce a normal runner summary
- `orphaned` plus `error` and `recovery.previousStatus` when a durable `queued` or `running` job was found after service restart
- `unknown` plus `error` when durable metadata could not be parsed

Completed summaries preserve the existing runner JSON and add top-level `remoteArtifacts[]` entries with:

- `relativePath`
- `downloadUrl`

Example:

```bash
curl -X POST \
  -H 'Authorization: Bearer local-dev-token' \
  -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/test-runs \
  -d '{"inline":"{name: remote-smoke, steps: [game.info]}"}'

curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/test-runs/run-1

curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/test-runs/run-1/artifacts

curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/test-runs/run-1/artifacts/summary.json
```

Restart discovery uses the same startup metadata and polling endpoints. The restarted process reports aggregate recovery counts, then previously durable run ids remain addressable:

```bash
curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/service

curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/test-runs/run-1

curl -H 'Authorization: Bearer local-dev-token' \
  http://127.0.0.1:4317/v0/test-runs/run-1/artifacts
```

Durable job directories contain diff-friendly JSON metadata:

- `job.json`: job status, timestamps, command/config summary, summary reference, runner-root reference, final summary or error, and recovery metadata when applicable
- `artifacts.json`: the remote artifact index with stable runner-relative paths, sizes, and modification timestamps

`summary.json`, per-scenario `result.json`, and step/failure artifacts stay in the existing runner output tree. Durable metadata references those files rather than copying or rewriting them.

## Artifact Boundary

- The service does not change the on-disk `test run` artifact tree.
- `summary.json`, per-scenario `result.json`, and all existing step/failure artifacts stay where the CLI runner already writes them.
- First-class remote artifact download support applies only to remote `test run`.
- `GET /v0/test-runs/{runId}/artifacts` returns only the indexed files for completed jobs.
- `GET /v0/test-runs/{runId}/artifacts/{relativePath...}` serves only paths recorded in that job artifact index and only after path decoding stays inside the runner output tree.
- Path traversal and unindexed artifact requests return structured JSON errors instead of reading arbitrary server-local files.
- Other file-writing commands may still report server-local paths rather than downloadable service URLs.

## Wrapper And MCP Composition

- `createSts2Client({ serviceUrl, serviceToken })` uses this service for the catalog-backed wrapper methods and keeps the same public method names; `testRun()` stays on the dedicated async remote runner API.
- `STS2_SERVICE_URL` and `STS2_SERVICE_TOKEN` provide the same backend selection through environment variables.
- `sts2-mcp` stdio stays the default local adapter. When `STS2_SERVICE_URL` and `STS2_SERVICE_TOKEN` are set it can route catalog loading and catalog-backed tool execution through the automation service instead of the local CLI spawn path.
- Network MCP mode exposes the same tool catalog over Streamable HTTP at the advertised MCP endpoint. Tool results preserve the same JSON envelopes as local stdio MCP.
- Remote `test_run` remains durable-job based in network MCP: callers create jobs through the service-backed contract, poll status, and read `remoteArtifacts[]` / artifact download metadata instead of receiving a long-running inline runner stream.

## Verification

Rust:

- `cargo test -p sts2 --test automation_service`
- `cargo test -p sts2 --test automation_service durable_remote_jobs`
- `cargo test -p sts2 --test test_runner remote_artifact_index`
- `cargo test -p sts2 --test cli_snapshots`

Node:

- `scripts/validate.sh npm-wrapper-tests --json`

Manual smoke:

```bash
cargo run -p sts2 -- --json service serve --listen 127.0.0.1:4317 --auth-token local-dev-token
curl -H 'Authorization: Bearer local-dev-token' http://127.0.0.1:4317/v0/service
curl -H 'Authorization: Bearer local-dev-token' http://127.0.0.1:4317/v0/ai-tools
curl -X POST -H 'Authorization: Bearer local-dev-token' -H 'Content-Type: application/json' \
  http://127.0.0.1:4317/v0/test-runs -d '{"inline":"{name: remote-smoke, steps: [game.info]}"}'
```
