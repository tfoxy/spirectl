# 🕸️ **spirectl --- STS2 Developer Automation & AI Integration Toolchain**

**Executable:** `sts2`\
**Repository:** `spirectl`

---

## 1\. Overview

`spirectl` is a developer-focused toolchain for **Slay the Spire 2** that enables:

- AI-assisted mod development
- deterministic testing and validation
- runtime introspection and control
- static code and scene inspection
- full end-to-end automation (including external clients like Playwright)

It is not just a CLI---it is a **bridge between the game, the codebase, and automation systems (human or AI)**.

---

## 2\. Core Principles

### 2.1 Observable-first state

Default state reflects **what a player can know**, plus stable IDs.

### 2.2 Semantic actions first

High-level actions (e.g. `play-card`) are primary. Raw input is fallback.

### 2.3 Stable machine interfaces

Everything important is:

- JSON-serializable
- schema-versioned
- self-describing

### 2.4 Multiplayer is first-class

All systems (state, actions, fixtures, tests) must support multiplayer.

### 2.5 Toolchain, not just CLI

Covers:

- runtime
- dev/test
- environment
- code inspection

### 2.6 AI-native design

The system must:

- be self-describing (`inspect`)
- work as an AI skill later
- minimize guesswork for agents

### 2.7 Safe by default

- no silent mutation of game files
- explicit dev/dangerous modes

---

## 3\. System Architecture

## 3.1 Components

```
[ Rust CLI (sts2) ]
        |
        | (local RPC: unix socket / named pipe / TCP fallback)
        |
[ Bridge Mod (.NET 9, in-game) ]
        |
        | (game runtime APIs)
        |
[ Slay the Spire 2 ]

[ .NET Helper Tools ]
  - assembly inspection
  - symbol indexing
  - decompilation integration
  - scene/resource parsing

[ npm wrapper ]
  - npx sts2
```

---

## 3.2 Responsibilities

### CLI (Rust)

- command parsing
- config handling
- output formatting (JSON/human)
- orchestration
- cross-platform game management
- shell completion support

### Bridge Mod (.NET)

- authoritative runtime state
- semantic action execution
- stable ID assignment
- multiplayer awareness
- logs/traces
- dev/test hooks

### .NET Tools

- inspect assemblies
- build symbol index
- locate/describe/decompile
- scene/resource introspection

---

## 3.3 Transport

- **Default:** local IPC
  - Unix socket (Linux/macOS)
  - named pipe (Windows)
- **Fallback:** localhost TCP
- **Protocol:** gRPC (protobuf contracts)

CLI abstracts transport completely.

---

## 4\. Command Model

## 4.1 Namespaces

```bash
sts2 state ...
sts2 act ...
sts2 dev ...
sts2 game ...
sts2 code ...
sts2 inspect ...
sts2 test ...   # later
```

---

## 4.2 `state` --- runtime inspection

```bash
sts2 state
sts2 state --filter combat
sts2 state --query 'combat.enemies[*].intent'
sts2 state --include ids
sts2 state --json
```

### Behavior

- screen-aware
- sparse
- includes:
  - `screen`
  - `run` (if applicable)
  - `choices`
  - `availableActions`
  - stable IDs

---

## 4.3 `act` --- interaction

Historical examples below predate S85. In current docs, choose-heavy calls are migration/fallback examples only; first-party modeled flows should read typed state and follow `preferredAction`.

```bash
sts2 act play-card --card c_17 --target e_2
sts2 act end-turn
sts2 act choose --choice reward:p1:0
sts2 act choose --choice reward-flow:skip
sts2 act choose --choice shop:p1:card:strike:0
sts2 act click --element confirm_button
```

Fallback:

```bash
sts2 act mouse click --x 100 --y 200
```

---

## 4.4 `dev` --- testing & debugging

```bash
sts2 dev new-run ...\
sts2 dev load-fixture ...\
sts2 dev logs\
sts2 dev assert ...\
sts2 dev wait-for ...\
sts2 dev screenshot\
sts2 dev trace start
```

---

## 4.5 `game` --- lifecycle & mods

```bash
sts2 game detect
sts2 game launch
sts2 game restart
sts2 game attach

sts2 game install-mod ./dist/MyMod
sts2 game list-mods

sts2 game deploy ./mods/MyMod --build --restart --verify
```

---

## 4.6 `code` --- static & runtime introspection

### Locate

```bash
sts2 code locate type CombatScreen
```

### Describe

```bash
sts2 code describe type MegaCrit.Sts2.CardState
```

### Decompile

```bash
sts2 code decompile type MegaCrit.Sts2.CardState
```

### Scene

```bash
sts2 code scene-tree
sts2 code scene-node /root/Main/Game
```

---

## 4.7 `inspect` --- self-description

```bash
sts2 inspect commands
sts2 inspect schema --command state
sts2 inspect actions
sts2 inspect examples --action play-card
```

---

## 5\. State Model

### 5.1 Default payload

```json
{
  "schemaVersion": "1.0",
  "gameVersion": "...",
  "bridgeVersion": "...",
  "screen": { "type": "combat" },
  "run": { ... },
  "combat": { ... },
  "choices": [],
  "availableActions": []
}
```

### 5.2 Rules

- screen-specific
- minimal but sufficient
- includes IDs
- debug fields only when requested

---

## 6\. Multiplayer Model

### Default

- **perspective-based view**

### Normal mode

- omniscient view allowed

### current priorities

1.  lobby inspection
2.  lobby actions
3.  run state
4.  per-player actions
5.  fixtures

---

## 7\. Testing System

## 7.1 Test files (`.sts2.yaml` / `.sts2.json`)

```yaml
name: basic combat test

steps:
  - game.deploy:
      path: ./mods/MyMod
      restart: true

  - dev.load_fixture:
      path: fixtures/combat.yaml

  - act.play_card:
      card: c_1
      target: e_1

  - assert.query:
      path: combat.enemies[0].hp
      lt: 20
```

---

## 7.2 Fixtures

### Declarative (primary)

```yaml
screen: combat
character: IRONCLAD
hand:
  - Strike
  - MyMod:Card
```

### Snapshot (generated)

```bash
sts2 dev snapshot export ...
```

---

## 7.3 Assertions

### Query

sts2 dev assert 'combat.hp' --gte 1

### Logs

sts2 dev assert-logs --level error --count 0

### Wait

sts2 dev wait-for 'screen.id' --equals combat

---

## 8\. External Test Integration (Playwright)

### Key requirement

`sts2` must be usable from Node-based test runners.

### Requirements

- JSON output (`--json`)
- stable exit codes
- deterministic behavior
- wait/assert commands
- structured logs
- artifact output

### Example E2E loop

1. Playwright clicks UI
2. mod sends action to game
3. `sts2 state` verifies result
4. `sts2 dev logs` checks errors

---

## 9\. Config

`sts2.config.yaml`

```yaml
game:
  path: auto

mode:
  default: dev

mod:
  projectPath: ./mods

test:
  artifactDir: ./.sts2/artifacts
```

---

## 10\. Modes

### Normal

- safe inspection + actions

### Dev

- fixtures, logs, traces

### Dangerous

- raw input
- file mutation

---

## 11\. Autocomplete

Must be supported via CLI design.

Future:

```bash
sts2 completion bash
sts2 completion zsh
```

---

## 12\. First spec (MVP)

Minimal vertical slice:

- game detect + launch
- bridge connection
- `state` (menu + combat)
- basic `act`
- logs
- `game deploy`
- minimal `code locate/describe`
- one fixture
- one test
- basic `inspect`

---

## 13\. Repo Structure

```
spirectl/
  cli/
  bridge-mod/
  dotnet-tools/
  proto/
  npm-wrapper/
  fixtures/
  tests/
  docs/
```

---

## 14\. Future Extensions

- MCP server
- Playwright helper package
- richer test runner
- replay system
- image diffing
- CI integration templates
