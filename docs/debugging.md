# Debugging

`spirectl` keeps live debugging explicit, leased, and dev-only.

Use it when you need to pause the live runtime, inspect state transitions, or stop on observable breakpoint conditions. Do not treat it as a baseline requirement for `state`, `act`, `diagnostics`, or `test run`.

## Before You Start

- Attach to a live host first with `sts2 game attach` or `sts2 game launch`.
- Check `sts2 --json dev debug status` before mutating debugger state.
- Expect honest unsupported responses on non-live or compile-gated hosts. The CLI, automation service, wrapper, and AI tool catalog all preserve that boundary instead of pretending the debugger exists.

## Session Model

The debugger separates the mutating controller lease from passive observer sessions.

- Start the controller lease with `sts2 --json dev debug session start --role controller`.
- Start a passive observer with `sts2 --json dev debug session start --role observer --name transcript-watcher`.
- Inspect either role with `sts2 --json dev debug session status --id <session-id>`.
- End either role with `sts2 --json dev debug session end --id <session-id>`.
- Mutating operations such as `pause`, `resume`, `step`, `wait`, and session-aware breakpoint management require the current controller session id when a host enforces ownership.

Only one controller lease can exist at a time. Multiple observers can attach without stealing the controller lease, and their session ids are valid for status/event reads only. If another caller owns the controller lease, mutating operations return structured session-conflict or session-expired notices instead of best-effort mutation. If an observer attempts to pause, resume, step, wait, or mutate breakpoints, the host returns a structured observer-mutation-forbidden notice such as `debug_observer_mutation_forbidden`.

`dev debug status` reports `sessionOwnership`, `activeSession`, `observerSessions`, and the caller role when the supplied session id is known. Non-live, mock, or compile-gated hosts preserve explicit unsupported/not-applicable notices rather than fabricating sessions or streams.

## Event Streams

`sts2 --json dev debug events` reads a bounded debugger event stream. Use it for transcript capture, reconnect replay, and passive monitoring of debugger lifecycle changes without assuming full history exists.

Replay examples:

```bash
sts2 --json dev debug session start --role observer --name transcript-watcher
sts2 --json dev debug events --session dbg:observer --from-sequence 1 --limit 50
sts2 --json dev debug events --session dbg:observer --from-sequence 42 --limit 50 --follow --timeout-ms 1000
```

Events can include controller and observer attach/detach, lease changes, breakpoint hits, pause/resume/step transitions, disconnects, dropped clients, and selected state-change notifications when the host supports them. Every response is bounded by the bridge retention window and by the request `limit`; `--follow` waits only up to `--timeout-ms` and then returns the same finite JSON envelope.

Callers should treat sequence cursors as loss-detection metadata, not as a durable audit log:

- `fromSequence` is the requested replay cursor.
- `nextSequence` is the next cursor to pass on a later request.
- `oldestRetainedSequence` / `newestSequence` and `retention.oldestSequence` / `retention.newestSequence` describe the current in-memory window.
- `retention.limit` is the maximum retained event count for that host.
- `expired` means the requested cursor is older than the retained window.
- `overflow` means the requested range could not be returned completely under the retention/limit boundary.
- `notices[]` carries structured explanations such as unsupported hosts, expired replay, client disconnects, or dropped-client reporting.

When `expired` or `overflow` is true, downstream tools must surface event loss to users and AI agents. They must not present the transcript as complete.

## Common CLI Flow

```bash
sts2 --json game attach
sts2 --json dev debug session start --role controller --name smoke-investigation --pause
sts2 --json dev debug session start --role observer --name transcript-watcher
sts2 --json dev breakpoint add --session dbg:1 --path screen.id --equals '"combat"' --name combat-match
sts2 --json dev debug wait --session dbg:1 --timeout-ms 5000
sts2 --json dev debug events --session dbg:observer --from-sequence 1 --limit 100
sts2 --json dev debug step --session dbg:1 --kind frame --count 1
sts2 --json dev debug events --session dbg:observer --from-sequence 42 --follow --timeout-ms 1000
sts2 --json dev debug session end --id dbg:1 --resume
sts2 --json dev debug session end --id dbg:observer
```

Typical flow:

1. Start a session, optionally pausing immediately with `--pause`.
2. Add one or more breakpoints bound to that session.
3. Use `dev debug wait` to resume until a breakpoint hit or timeout.
4. Use `dev debug step` for bounded follow-up inspection while paused.
5. End the session explicitly when the investigation is done.

## Breakpoints

`dev breakpoint add` supports two additive breakpoint kinds:

- `match`: evaluate a query path plus predicate and pause when it matches.
- `change`: watch a query path and pause when the observed JSON value changes between debugger checkpoints.

Breakpoint metadata includes:

- `minHitCount`
- `hitCount`
- `autoRemoveOnHit`
- `lastObservedJson`

Checkpoints are explicit and bounded. The runtime evaluates breakpoints during status refresh while paused, during `step`, and during `debug wait`. M45 does not add a hidden background watch loop for ordinary runtime execution.

## `debug wait`

`sts2 --json dev debug wait --session <id> --timeout-ms <n>` is the session-oriented resume-until helper.

Use it when the runtime should continue until:

- a breakpoint fires
- the timeout expires
- the host reports unsupported or not-applicable debug behavior
- the session ends or expires

`debug wait` is debugger-specific. It does not replace `dev wait-for`, assertions, or `test run`.

## JSON Contract Notes

- `dev debug status` reports `sessionOwnership`, `activeSession`, `observerSessions`, and caller role so callers can see whether the runtime is unowned, owned by them, watched by observers, or leased elsewhere.
- `dev debug wait` returns `completed`, `timedOut`, `status`, and `notices`.
- `dev debug events` returns `events`, `fromSequence`, `nextSequence`, `oldestRetainedSequence`, `newestSequence`, `retention`, `expired`, `overflow`, `follow`, `timedOut`, `timeoutMs`, and `notices`.
- Each event includes a stable sequence number, kind, timestamp, and role/session context when relevant. Lease-change events expose controller/observer role metadata; disconnect/drop events should be preserved as loss and lifecycle evidence.
- `dev breakpoint list` and `dev debug status` both expose breakpoint hit counts and the last observed value when the host can provide it.

For machine-facing discovery, use:

- `sts2 --json inspect commands`
- `sts2 --json inspect examples`
- `sts2 --json inspect ai-tools`

## Automation Service

The automation service exposes the same debugger contract over HTTP/JSON:

- `POST /v0/debug-sessions`
- `GET /v0/debug-sessions/{id}`
- `DELETE /v0/debug-sessions/{id}`
- `GET /v0/debug-sessions/{id}/events`
- `POST /v0/debug-sessions/{id}/events`
- `POST /v0/debug-sessions/{id}/wait`

The generic tool-call path also keeps the same session-aware debugger tools and payload shapes. See [automation-service](./automation-service.md) for the HTTP endpoint details.

## Wrapper And AI Surface

- The npm wrapper exposes `debugSessionStart`, `debugSessionStatus`, `debugSessionEnd`, `debugEvents`, and `debugWait`, plus session-aware debug and breakpoint helpers.
- The AI catalog exposes `debug_status`, `debug_session_start`, `debug_session_status`, `debug_session_end`, `debug_events`, `debug_pause`, `debug_resume`, `debug_step`, `debug_wait`, `breakpoint_list`, `breakpoint_add`, and `breakpoint_remove`.

See [ai-tools](./ai-tools.md) for the machine-facing guidance on when those tools are stable enough to use and when they remain partial because they still depend on live host support.
