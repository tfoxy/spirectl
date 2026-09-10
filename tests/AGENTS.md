# tests/AGENTS.md

## Purpose

Tests and fixtures are the primary executable specification for developer workflows.

---

## Rules

### YAML-first authored artifacts

Prefer YAML for human-authored tests and fixtures.

### Declarative fixtures first

Prefer scenario setup over giant opaque snapshots for authored fixtures.

### Determinism matters

Prefer seeds, explicit setup, and repeatable conditions where possible.

### Test real workflows

Good tests should reflect actual user or AI workflows:

- deploy
- load/setup
- act
- assert
- inspect logs/artifacts

### Artifact clarity

On failure, prefer artifacts that help debugging:

- logs
- screenshots
- traces
- diffs
- relevant state excerpts

### External-runner friendliness

Some tests should be designed with Playwright/Node integration in mind.

---

## Avoid

- unreadable giant fixtures when a small declarative one would do
- assertions that depend on unstable incidental ordering unless necessary
- hiding key test assumptions
