# Library and internal design review

This review follows responsibility and dependency boundaries, rather than imposing a file-size limit. Source
constructor changes are coordinated with `sts2-couch-coop`. Request/result DTOs, protobuf field numbers,
multiplayer perspective rules, asset identities, and HTTP error envelopes remain compatibility constraints.

## Export and consumer inventory

| Surface | Consumers reviewed | Decision |
| --- | --- | --- |
| `Spirectl.Sts2.Embedding.ISpirectlRuntime` and factories | Couch runtime composition, bridge tests | Keep the focused reusable aggregate: capability, asset, state, combat-event, animation-hint, scene-delta/watch controls, model, reference, Spine catalog/baking, and semantic-action ports. Profiling and bridge-development ports are intentionally absent. |
| `BridgeRuntime` | Bridge factories, protocol adapters, transport tests | `Spirectl.BridgeMod` owns the immutable bridge composition grouped into observation, actions, assets, development, and lifecycle. Named scaffold options explicitly construct fallbacks. |
| Asset operation interfaces | Runtime dispatch, both embedded asset adapters, extraction tests | Extraction, explanation, catalog, Spine discovery, and baking can be supplied independently. An extraction-only fake need not implement baking or catalogs. The aggregate remains a composition convenience. |
| Scene-watch controls | Couch browser server, hot-reload lifecycle, unanimity tests | The producer owns coupled settings writes. Couch owns viewer policy, aggregation, defaults, and reassertion on generation changes. Scope remains process-wide. Unsupported providers are explicit. |
| Node `.` export | Local callers, service client, MCP adapter, examples | Preserve factories, public method names, declarations, and `Sts2CliError`. Normalize `testRun` once before transport-specific serialization. |
| Node `./playwright` export | Browser automation and wrapper tests | No change: cohesive adapter with its own optional host dependency. |
| Presentation `./render` | Couch source aliases and render consumers | Exactly six exports retained: `applyAnimationBinding`, `ensureAnimationStyles`, `PresentationAnimationBinding`, `PresentationAnimationOptions`, `DEFAULT_BBCODE_TAGS`, `playZoneThreshold`. |
| Presentation `./spine` | Couch raster/geoclip players, presentation tests | Share DOM-free parsing, decoding, timing, and sampling. Add explicit recovery APIs and distinct recovered types; keep strict validation. |
| Parsed Rust `Cli` / `Commands` and reachable argument types | CLI entrypoint, integration tests, completion, AI/service adapters | Keep public parsed-command API. Replace command `include!` fragments and ambient imports with ordinary modules and explicit dependencies. |

Couch's state observer, animation collector, input executor, HTTP asset adapter, and geoclip baker accept focused
ports. Capability policy stays in the host adapter so narrowed consumers preserve unsupported-operation errors.
Host-level composition still legitimately combines multiple ports. Model/reference and Spine catalog/baking are
separate contracts; no unrelated methods are supplied through default interface implementations.

## Internal responsibility boundaries

| Area | Implementation boundary | Invariants |
| --- | --- | --- |
| Scene streaming | Capture owns retained nodes; an animation coordinator owns endpoint resolution, pending flight resolution, and suppression windows. One binding owns callbacks into process-lifetime hooks. | Same registry and capture clock; disposal compares callback identity and cannot clear a newer binding. Subscriptions and scene signals are released without unpatching hooks. |
| Runtime lifetime | Facade subscription scope and disposable watcher | Explicitly disposed subscriptions are removed from the scope; runtime disposal releases remaining callbacks and completes idle async streams. |
| State production | Character-selection, run-shell/overlay, combat, ordinary-room, and map builders under one main-thread coordinator | Stable IDs and shared projection helpers; the coordinator remains the observation/dispatch boundary. |
| Fixture loading | Typed menu/lobby, combat/selection, and room/map families | Canonical validation, cross-screen cleanup, and lobby-host ownership remain coordinated. Family implementations receive explicit run/lobby operations. |
| Assets | Extraction/render context, explanation renderer, general catalog, and Spine catalog/bake services | Dispatch and structured failures retained; rendering primitives shared where resource lifetime and frame waits require it. |
| Automation service | Durable storage/recovery, remote execution, artifact access, debug endpoints, and HTTP routing modules | Startup composes the service; authentication, job transitions, recovery, paths, and endpoint envelopes retain their behavior. |
| Static scene inspection | Resource loader, Godot text parser, document builder/path helpers, and query index behind the existing catalog facade | Authoritative source precedence, packed resources, cache ownership, and query output retained. |

## Deliberate compatibility details

`testRun` accepts exactly one input source. Both transports use the same validation and combine `tags` followed
by `tag`, preserving order and duplicates. This repairs the service's dropped singular tag. `durable` remains
service-only; a local CLI invocation ignores it, as documented. Transport selection and error wrapping remain
outside normalization.

Geoclip recovery preserves the downstream policy for defaults, skipped entries, incomplete draw order, hidden
slots, unknown versions, transform/reference-pose fallback, placement, and truncated vertex buffers. A finite
numeric declared frame-count mismatch is fatal. Strict parsing never opts into these recoveries. Both clip types
use shared timing/geometry primitives; recovering frame sampling retains its original ordering policy. The
recovery report carries structured diagnostics and a recovery flag. Fetching, URLs, caches, warnings, GPU
resources, and raster fallback remain host responsibilities.

## Justified no-change decisions

- DTOs, enum/catalog declarations, protocol projections, and animation vocabulary tables are cohesive even when
  large. Splitting them into services would add indirection without separating a reason to change.
- Main-thread scheduling, fixture cleanup, and multiplayer host ownership need one coordinator. Family builders
  do not acquire independent lifecycle ownership or use a general service locator.
- The rendering implementation remains substantial: viewport setup, frame waits, image encoding, and resource
  release share a real lifetime. Catalog/explanation orchestration no longer requires the entire renderer API.
- Presentation remains two package subpaths and is not a browser renderer. No product routing or loading policy
  moves upstream.
- Implementation helpers introduced by this work are internal. Existing public runtime entrypoints and data
  types remain public where their consumers rely on them; visibility changes are not used as a substitute for
  separating responsibilities.

## Verification and baseline findings

Focused checks cover Node transport parity, strict/recovering geoclip behavior, narrow asset and bake consumers,
unsupported capabilities, coupled controls, hook replacement/disposal, stream lifetime, fixture cleanup, and
static resource precedence. Couch uses executable .NET runners as well as frontend typecheck/Vitest gates.
Live verification uses both deployed bridge assemblies, representative single/multiplayer fixtures, assets,
semantic actions, browser evidence, and identical repeated scene-watcher workloads.

Baseline issues were investigated separately:

- The standalone C# host used frame version 1 while the Rust client used 0. Both now use the existing client
  version; a cross-language regression check prevents drift.
- The Rust unsupported-latest-fixture test used the supported `v0` value; its negative case now uses `v999`.
  Two runner failure tests used obsolete schema-less fixture inputs; canonical inputs now reach their intended
  semantic validation errors.
- Node packaging assertions still expected placeholder installation text and an obsolete direct skill link;
  they now check the README's concrete install command and AI-skill documentation link.
- The isolated live fixture-model unit test fails while initializing the installed model catalog. The same
  failure was reproduced against unchanged fixture code; it is distinct from successful in-game fixture loads.
- An older authored event fixture names an unavailable installed event. Live parity uses a working event
  fixture and retains the original failure as baseline evidence.

Integrated validation:

| Gate | Result |
| --- | --- |
| Full Rust suite | 957 passed; one existing ignored test. |
| Full Node wrapper suite | 133 passed after building the CLI prerequisite. |
| Presentation suite | 77 passed. |
| Static inspection suite | 77 passed before and after decomposition. |
| Full bridge suite | Passed. |
| Final selected live-host bridge tests | 35 passed; the independently reproduced model-catalog initialization failure remains. |
| Couch .NET executable runners | Mod/hosted routes and mirror protocol passed. |
| Couch frontend | Typecheck/build passed; 5,041 Vitest tests passed. |

Both bridge copies were deployed from integrated `main`; the standalone assembly matched its publish output.
Eight representative fixture states retained their field structure. Seven matched baseline values exactly; two
event descriptions contained resolved numbers where the baseline captured placeholders (the formatting code is
unchanged). Consecutive load-run lobbies, three-player treasure, and a combat card-selection overlay succeeded.
Live catalog, extraction, explanation, semantic actions, and scene subscriptions succeeded.

Three identical combat workloads included fixture load, scene subscription, card play, and end turn. The first
profile window of each run was excluded because it includes the preceding idle interval and cold capture.
Warm mean capture time was 8.06 ms before and 6.31 ms after; p95 was 19.47 ms before and 12.79 ms after. Every
final repeat had a lower mean than its baseline counterpart. These measurements show no repeatable regression;
they do not isolate an optimization attributable to this refactor.

Browser evidence covers ordinary rendering, live geoclip rendering, recovery from an unknown version, malformed
fps, absent draw order and invalid vertices, and fatal frame-count mismatch falling back to a successful raster
request. A card play produced browser animations and updated hand, energy, and enemy HP. Initial baseline and
final default screenshots had missing raster assets; the later geoclip/action capture rendered the creatures and
background. Asset readiness remains a tooling limitation, recorded alongside the fresh-checkout Node setup race
and the repaired cross-language frame-version drift.

Machine-specific logs, captures, and performance reports are retained in ignored `.sts2/solid-r2/`.
