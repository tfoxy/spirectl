# proto/AGENTS.md

## Purpose

Protobuf contracts define the typed transport and core schema boundaries across the repo.

---

## Rules

### Additive evolution first

Prefer adding fields over changing meaning of existing ones.

### Never reuse field numbers

Deprecated fields stay reserved.

### Be explicit about unstable surfaces

If something is provisional, mark/document it rather than silently treating it as stable.

### Keep contracts focused

Do not create giant grab-bag messages when smaller, screen- or domain-specific messages are clearer.

### Reflect architecture

Runtime state, actions, logs, inspect metadata, and static inspection should each have coherent contract groupings.

---

## When changing proto

Update:

- generated code if tracked
- CLI output adapters
- bridge/runtime implementations
- docs
- tests
