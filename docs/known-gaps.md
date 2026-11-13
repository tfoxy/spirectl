# Known Limitations

What `spirectl` does not do today, or does only partly. For what it does, see [capabilities](capabilities.md); for
file-level pointers, the subsystem maps under [docs/maps/](./maps/).

Each entry is marked **limitation** (may change) or **non-goal** (deliberately outside the product shape).

## CLI

- **Non-goal:** editing shell startup files or writing into shell-owned completion directories. Completion stays
  app-managed (`sts2 completion install`).

## Runtime state and actions

- **Limitation:** the v0.113.0 game build no longer exposes the mutable rest-site smith count, so `smithCount`
  remains unset on API lane `v113`; absence does not mean zero.
- **Limitation:** host-to-peer shop purchase propagation on v0.113.0 has not been live-verified. The local
  two-instance `-fastmp` check was skipped because its required UDP port 33771 was occupied by another game
  session outside the validation scope, which was left untouched. The lobby fixture can fall back to another
  port, but native `-fastmp join` targets 33771, so fixture success does not prove peer synchronization.
- **Limitation:** modded or unmodeled non-combat controls have no typed intent action. `sts2 act choose` is the
  explicit fallback for generic, modded or unmodeled visible choices that have no `preferredAction`.
- **Limitation:** live screen coverage is the documented set (see [capabilities](capabilities.md#runtime-state)).
  Other screen families, unvalidated overlay follow-through controls, and the bundle preview/confirm sub-screen
  (exposed as `previewActive` / `selectedBundleId`, not modelled separately) are not covered.
- **Limitation:** a newly supported screen needs explicit runtime and legality coverage before it can claim
  remote-owned execution. Full remote-client multiplayer orchestration is not supported: one local bridge does not
  recreate independent remote clients, and capability metadata says so.
- **Limitation:** `state actions` treasure-room quirk. The action table gates `open-chest` / `take-relic` on
  `treasure.currentRelicsActive`, a field `state` never emits, so both actions surface whenever a treasure room is
  shown, and `open-chest` is offered even when relics are already active. See `cli/src/state_actions.rs`
  `surface_treasure`.
- **Limitation:** selection affordances that are not game models, such as `RANDOM_CHARACTER`, cannot be looked up by
  id outside the lobby. `models characters` is the real game-data catalog; RANDOM is exposed through lobby `state`
  and the select-character action.
- **Non-goal:** raw scene-tree internals in default `state`. Gaps in typed fields are reported as structured
  notices; raw internals stay under `dev scene tree|node|children`.
- **Non-goal:** `dev console` as a semantic action. Its output is limited to the command's result message, it never
  appears in default `state`, and persisted-file commands (`achievement`, `cloud`, `unlock`) need dangerous mode.

## Rendering and assets

- **Non-goal:** screen rendering. `spirectl` publishes semantic state, live Godot scene state, models, localization
  and asset bytes; downstream renderers own layout, DOM/canvas output and product policy. The only TypeScript surface
  is `@spirectl/presentation`'s `./render` and `./spine` subpaths.
- **Limitation:** encounter visual packages cover the checked-in base-game catalog only. Arbitrary uncataloged
  encounters are not inferred, and exact animation/VFX timing and pixel-perfect framebuffer parity are out of scope.
  When the bridge cannot prove a render target is isolated, it returns structured isolation-failure diagnostics
  rather than a leaked full scene.
- **Limitation:** a Spine timeline extract buffers every frame and returns them in one payload; there is no
  per-frame streaming.
- **Limitation:** scene state does not report the viewport content-scale factor, which a renderer reproducing label
  metrics needs.

## Bridge deploy and releases

- **Limitation:** the quiescence wait (`--wait-quiescent-ms`, `dev wait-for-transitions`) does not prove the boot
  flow has finished after a restart, so it can report quiescent while the game is still settling. Keep an explicit
  delay before loading a fixture right after a restart.
- **Limitation:** `game install-bridge`'s `contentHash` skip does not fire after a real build, because the build
  stamps `SpirectlBridgeBuiltAtUtc` with the wall clock. `--no-build` installs do skip unchanged content.
- **Limitation:** released bridge payloads exist only for the game build the declaration-only reference SDK
  (`eng/Sts2.ReferenceSdk`) declares. The `v111` and `v113` API lanes require a source checkout; `install-bridge --no-build`
  refuses an unreleasable lane with `bridge_release_lane_unreleasable`.
- **Limitation:** the `Spirectl.Sts2` NuGet package carries one STS2 API lane. An embedder targeting another game
  build builds `Spirectl.Sts2` from source with `-p:Sts2GameApi=<lane>`.

## Embedded runtime

- **Limitation:** there is no cached latest-state read. The embedded state surface is `GetCurrentState`,
  `SubscribeCurrentState` and `WatchCurrentStateAsync`; an embedder that wants a last-known snapshot keeps one
  itself off the subscription.

## Mod development

- **Non-goal:** native hot reload of arbitrary STS2 mods. Mod loading is startup-bound; the
  `stable-harmony-trampoline` template and `sts2 dev mod-reload` provide restart-free iteration for developer-owned
  logic inside that boundary.

## Static inspection

- **Non-goal:** IDE-style source navigation or full project reconstruction. `code decompile --full` gives exact-match
  ILSpy text, not a project.

## Test runner and restore

- **Limitation:** fixtures are authored setup recipes. They do not patch hidden combat queues, RNG continuation,
  animation state, enemy AI history or transient UI internals, and fixture loading reports
  applied/inferred/omitted/unsupported fields honestly.
- **Limitation:** remote artifact transfer through the automation service covers `test run` only.
- **Non-goal:** arbitrary mid-screen checkpoint resume and unstructured runtime memory editing. Restore stays
  bounded, bridge-mediated and explicit.

## Visual validation

- **Non-goal:** hosted screenshot-baseline services or remote visual review. Visual validation stays CLI-local and
  filesystem-backed.

## Wrapper and external runners

- **Non-goal:** a second transport, a JS-native runtime protocol, or a second runner. The wrapper stays thin and
  shells back to the CLI.

## AI and MCP

- **Non-goal:** dangerous raw input as AI tools. `act mouse click` is for operators only.
- **Non-goal:** hosted debugger dashboards, accounts, durable debugger history, managed TLS or multi-tenant policy for
  MCP. The debugger event stream is an in-memory, loss-detectable transcript; network MCP is operator-configured, with
  stdio as the default.

## Platforms

- **Limitation:** the CLI integration suites that need a Unix-socket bridge stub do not run on Windows; Windows
  automated coverage is unit tests, fake SSH/SCP lifecycle tests, and a cross-target compile check
  (see [testing](testing.md#platform-coverage)). TCP transport, state, actions, screenshots, console
  commands, and the `sts2 game remote` install/launch/stop lifecycle have been exercised against a real
  Windows game. Remote Windows named pipes have not yet been exercised against a real game.
