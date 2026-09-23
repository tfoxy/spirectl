# Native scene invalidation feasibility probe

This Linux x86-64 experiment tests two generic observation seams without wiring them into the shipped bridge:

- a fingerprint-gated shadow vtable for RenderingServer mutations;
- an exact-prefix-gated, reversible trampoline for synchronous `CanvasItem.queue_redraw`.

It also supplies a bounded dirty ring, RID-to-node mapping, public-signal events, overflow-to-full-capture
semantics, and strict restoration checks. Unknown fingerprints and unexpected target bytes are rejected before
memory is changed. The polling capture remains authoritative; an overflow or any unsafe state requires a full
capture and stops incremental use.

Run the generated-host probe with:

```sh
experiments/native-scene-invalidation/run-probe.sh
```

Set `GODOT_BIN` to additionally prove that `GDExtensionManager` can load the sidecar from an absolute path.
That load check proves the public loader path only; it does not establish ABI compatibility with another Godot
build. Exact-build slot and target recovery stays machine-local and is never compiled into a release default.

The generated-host test maps the requested mutation families to the two candidate seams: transforms and native
tweens to canvas transforms, camera and CanvasLayer movement to viewport canvas transforms, color/visibility/z
to their RenderingServer writes, shaders to material parameters, GPU particles to particle state, and redraw
requests to layout, same-size text, theme, texture/atlas/in-place texture changes, and CPU particles. Free and
reparent events use the public-signal supplement. These mappings exercise observer mechanics only. Each mapping
still needs same-capture validation against the authoritative poll in the exact game build before adoption.

No subscriber lifecycle or shipping watcher is implemented here. A caller must arm this experiment only for an
active scene-stream subscriber and disarm it when the last subscriber leaves. Detached gameplay peers are not a
reason to arm it. There is no timer, batching delay, or input-path integration in the sidecar.

The generated target is a single-threaded fixture with a deliberately relocatable instruction prefix.
This is not a general detour library: it does not prove concurrent callback quiescence, instruction relocation,
RTTI compatibility, a parent/subtree dependency graph, resource fan-out, or generation-safe reuse of node/RID
identities. Its shadow oracle compares fixture node sets; no current game capture is connected to it. These
remaining gaps and the absence of verified game targets block arming it in the game. Successful fixture tests
must not be reported as actual-game observation, latency, or zero-viewer teardown proof.
