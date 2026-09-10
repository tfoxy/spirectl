# cli/AGENTS.md

## Purpose

The Rust CLI is the primary user-facing surface for `spirectl`.

It should be:

- fast
- composable
- JSON-friendly
- easy to script
- easy to complete in shells
- easy for AIs to inspect and use

---

## Rules

### Declarative command definitions

Use a command structure that makes:

- subcommands explicit
- argument types explicit
- help text rich
- completion generation easy

Do not hand-roll parsing.

### JSON is first-class

Any command likely to be consumed by tools should support structured output.
Do not make human-only formatting the only way to access data.

### Stable UX

Prefer predictable command patterns across namespaces.

Examples:

- `state --filter ...`
- `state --query ...`
- `dev assert ...`
- `inspect schema ...`

### Clear exit codes

Commands used in automation must fail clearly and consistently.

### Keep one-shot ergonomics

Even if the internal client keeps a transport/session abstraction, the UX should remain simple for shell use.

### Completion-friendly design

Do not add command shapes that make shell completion awkward unless there is a strong reason.

### Agent-tuned inspect/help

The CLI should expose self-description cleanly:

- commands
- schemas
- examples
- actions
- workflows if added

### Config behavior

Support explicit config files and overrides.
Avoid hidden environment-dependent surprises.

---

## When changing commands

Update:

- command definitions
- help text
- JSON output contracts
- inspect metadata
- examples/tests
- completion generation if needed

## Tooling feedback loop

If a tool is missing (for example `jq`) or can be improved (including `sts2`) for better validation or analysis, record the request with `scripts/tool-improvement.sh add ...` so `.ai/tool-improvements.md` gets a real timestamp.

---

## Avoid

- embedding business logic that belongs in bridge/runtime or helper tools
- output-only text that cannot be parsed
- inconsistent naming across namespaces
- brittle hidden defaults
