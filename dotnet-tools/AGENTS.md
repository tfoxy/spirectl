# dotnet-tools/AGENTS.md

## Purpose

These tools provide static inspection and helper functionality around the installed game and mod environment.

Typical responsibilities:

- locate assemblies/resources
- build symbol indexes
- describe types/methods
- integrate decompile workflows
- inspect scenes/resources where appropriate

---

## Rules

### Structured first

Prefer compact structured outputs for locate/describe operations.

### Stage outputs

Keep `locate`, `describe`, and `decompile` conceptually separate.

### Metadata before full raw decompile

Early usefulness matters more than maximal completeness.

### Do not silently mutate original game files

Helper generation is fine.
Unsafe mutation is not.

### Keep outputs AI-friendly

Symbol lookup, references, and summaries should be easy to consume programmatically.

## Tooling feedback loop

If a tool is missing (for example `jq`) or can be improved (including `sts2`) for better validation or analysis, record the request with `scripts/tool-improvement.sh add ...` so `.ai/tool-improvements.md` gets a real timestamp.

---

## Avoid

- making raw decompile the only usable output
- hard-coupling helper tools to CLI-only concerns
- writing directly into original game assets unless explicitly requested and mode-gated
