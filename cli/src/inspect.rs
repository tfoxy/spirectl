#![allow(private_interfaces)]

use super::*;

mod commands;
pub(crate) use commands::*;

pub(crate) fn schema_string(description: &str) -> Value {
    json!({
        "type": "string",
        "description": description
    })
}

pub(crate) fn schema_integer(description: &str, minimum: u64) -> Value {
    json!({
        "type": "integer",
        "minimum": minimum,
        "description": description
    })
}

pub(crate) fn schema_boolean(description: &str) -> Value {
    json!({
        "type": "boolean",
        "description": description
    })
}

pub(crate) fn schema_enum(description: &str, values: &[&str]) -> Value {
    json!({
        "type": "string",
        "enum": values,
        "description": description
    })
}

pub(crate) fn object_schema(properties: Map<String, Value>, required: &[&str]) -> Value {
    let mut schema = Map::new();
    schema.insert("type".to_string(), Value::String("object".to_string()));
    schema.insert("properties".to_string(), Value::Object(properties));
    schema.insert("additionalProperties".to_string(), Value::Bool(false));

    if !required.is_empty() {
        schema.insert(
            "required".to_string(),
            Value::Array(
                required
                    .iter()
                    .map(|name| Value::String((*name).to_string()))
                    .collect(),
            ),
        );
    }

    Value::Object(schema)
}

pub(crate) fn insert_perspective_properties(properties: &mut Map<String, Value>) {
    properties.insert(
        "perspective".to_string(),
        schema_enum(
            "Optional runtime perspective scope for multiplayer-aware state queries.",
            &["local", "omniscient"],
        ),
    );
    properties.insert(
        "playerId".to_string(),
        schema_string(
            "Optional stable player id used with multiplayer-aware perspective selection.",
        ),
    );
}

pub(crate) fn insert_code_search_root_properties(properties: &mut Map<String, Value>) {
    properties.insert(
        "gamePath".to_string(),
        schema_string("Optional override for the STS2 install root used to derive assemblies, resources, and mods."),
    );
    properties.insert(
        "assembliesDir".to_string(),
        schema_string("Optional override for the managed assembly search root."),
    );
    properties.insert(
        "resourcesDir".to_string(),
        schema_string("Optional override for the Godot scene/resource search root."),
    );
    properties.insert(
        "modsDir".to_string(),
        schema_string("Optional override for the mod search root used when includeMods is true."),
    );
    properties.insert(
        "includeMods".to_string(),
        schema_boolean(
            "Include mod assemblies and resources in addition to the base game search roots.",
        ),
    );
    properties.insert(
        "includeDependencies".to_string(),
        schema_boolean(
            "Include managed dependency DLLs in static inspection; dependencies are excluded by default.",
        ),
    );
}

pub(crate) fn empty_object_schema() -> Value {
    object_schema(Map::new(), &[])
}

pub(crate) fn console_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "command".to_string(),
        schema_string("STS2 in-game developer console command name."),
    );
    properties.insert(
        "args".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" },
            "description": "Arguments passed to the STS2 in-game developer console command."
        }),
    );
    properties.insert(
        "mode".to_string(),
        schema_enum(
            "Optional CLI mode for persisted-change console commands. Use dangerous only for achievement, cloud, or unlock.",
            &["dangerous"],
        ),
    );
    object_schema(properties, &["command"])
}

pub(crate) fn wait_or_assert_schema(include_wait_controls: bool) -> Value {
    let mut properties = Map::new();
    properties.insert(
        "path".to_string(),
        schema_string("Dotted JSON path into the queried document, including numeric indexes, array wildcards, nested or object-wide array filter selectors, or by-id maps."),
    );
    properties.insert(
        "source".to_string(),
        schema_enum(
            "Document to query. Omitted queries the plain state snapshot; `actions` queries the resolved `spirectl.state-actions/v0` envelope instead (the same document `sts2 state actions` returns).",
            &["actions"],
        ),
    );
    properties.insert(
        "equals".to_string(),
        json!({
            "type": ["string", "number", "boolean"]
        }),
    );
    properties.insert(
        "contains".to_string(),
        json!({
            "type": ["string", "number"]
        }),
    );
    properties.insert(
        "regex".to_string(),
        schema_string("Rust-style regular expression matched against a string state value."),
    );
    properties.insert("gt".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("gte".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("lt".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("lte".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert(
        "exists".to_string(),
        json!({
            "type": "boolean",
            "const": true
        }),
    );
    properties.insert(
        "notExists".to_string(),
        json!({
            "type": "boolean",
            "const": true
        }),
    );
    insert_perspective_properties(&mut properties);

    if include_wait_controls {
        properties.insert(
            "timeoutMs".to_string(),
            schema_integer("Maximum total wait duration in milliseconds.", 1),
        );
        properties.insert(
            "intervalMs".to_string(),
            schema_integer("Polling interval in milliseconds between state reads.", 1),
        );
    }

    let mut schema = object_schema(properties, &["path"]);
    if let Value::Object(schema_object) = &mut schema {
        schema_object.insert(
            "oneOf".to_string(),
            Value::Array(vec![
                json!({ "required": ["equals"] }),
                json!({ "required": ["contains"] }),
                json!({ "required": ["regex"] }),
                json!({ "required": ["gt"] }),
                json!({ "required": ["gte"] }),
                json!({ "required": ["lt"] }),
                json!({ "required": ["lte"] }),
                json!({ "required": ["exists"] }),
                json!({ "required": ["notExists"] }),
            ]),
        );
    }
    schema
}

pub(crate) fn code_locate_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "subject".to_string(),
        schema_enum(
            "Static inspection subject to search for.",
            &["type", "method", "symbol"],
        ),
    );
    properties.insert(
        "query".to_string(),
        schema_string("Search query or exact id/name understood by the helper tool."),
    );
    properties.insert(
        "limit".to_string(),
        schema_integer("Maximum number of matches to return.", 1),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["subject", "query"])
}

pub(crate) fn code_resolve_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "subject".to_string(),
        schema_enum(
            "Exact static inspection subject to resolve.",
            &["type", "method"],
        ),
    );
    properties.insert(
        "query".to_string(),
        schema_string("Exact type or method query, usually a stable id returned by code_locate."),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["subject", "query"])
}

pub(crate) fn code_decompile_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "subject".to_string(),
        schema_enum(
            "Exact static inspection subject to resolve.",
            &["type", "method"],
        ),
    );
    properties.insert(
        "query".to_string(),
        schema_string("Exact type or method query, usually a stable id returned by code_locate."),
    );
    properties.insert(
        "full".to_string(),
        schema_boolean(
            "Request embedded ILSpy output instead of the default metadata summary view.",
        ),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["subject", "query"])
}

pub(crate) fn code_derived_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "subject".to_string(),
        schema_enum("Derived relationship subject.", &["type"]),
    );
    properties.insert(
        "query".to_string(),
        schema_string("Exact type query or stable type id returned by code_locate."),
    );
    properties.insert(
        "limit".to_string(),
        schema_integer("Maximum number of derived relationships to return.", 1),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["subject", "query"])
}

pub(crate) fn code_hooks_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "query".to_string(),
        schema_string(
            "Optional hook-oriented method search query; omit it to browse the static catalog.",
        ),
    );
    properties.insert(
        "limit".to_string(),
        schema_integer("Maximum number of hook candidates to return.", 1),
    );
    properties.insert(
        "offset".to_string(),
        schema_integer("Zero-based catalog offset for paginated hook browsing.", 0),
    );
    properties.insert(
        "source".to_string(),
        schema_enum(
            "Restrict hook candidates to game or mod assemblies.",
            &["game", "mod"],
        ),
    );
    properties.insert(
        "assembly".to_string(),
        schema_string("Restrict hook candidates to one assembly name."),
    );
    properties.insert(
        "form".to_string(),
        json!({
            "type": "array",
            "items": {
                "type": "string",
                "enum": ["managed-prefix", "managed-postfix", "managed-override", "managed-interface-contract"]
            },
            "description": "Restrict hook candidates to one or more advisory hook forms."
        }),
    );
    properties.insert(
        "hasScript".to_string(),
        schema_boolean("Only return hook candidates with a resolved Godot script path."),
    );
    properties.insert(
        "sort".to_string(),
        schema_enum(
            "Hook catalog sort order.",
            &["relevance", "name", "reference-count", "assembly"],
        ),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &[])
}

pub(crate) fn code_hook_info_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "query".to_string(),
        schema_string("Exact method id or exact lookup signature returned by code_hooks."),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["query"])
}

pub(crate) fn code_scene_search_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "query".to_string(),
        schema_string("Scene, node, or resource search query for static Godot text, binary, and packed assets."),
    );
    properties.insert(
        "limit".to_string(),
        schema_integer("Maximum number of matches to return.", 1),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["query"])
}

pub(crate) fn code_scene_tree_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "scene".to_string(),
        schema_string("Exact static scene path or stable scene id returned by code_scene_search."),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["scene"])
}

pub(crate) fn code_scene_node_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "scene".to_string(),
        schema_string("Exact static scene path or stable scene id returned by code_scene_search."),
    );
    properties.insert(
        "nodePath".to_string(),
        schema_string("Exact node path within the resolved static scene."),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["scene", "nodePath"])
}

pub(crate) fn lifecycle_wait_schema(timeout_description: &str) -> Value {
    let mut properties = Map::new();
    properties.insert(
        "timeoutMs".to_string(),
        schema_integer(timeout_description, 1),
    );
    properties.insert(
        "intervalMs".to_string(),
        schema_integer("Polling interval between attachment attempts.", 1),
    );
    properties.insert(
        "rpcTimeoutMs".to_string(),
        schema_integer(
            "Maximum time to wait for each live bridge RPC during lifecycle polling.",
            1,
        ),
    );
    object_schema(properties, &[])
}

pub(crate) fn game_launch_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "timeoutMs".to_string(),
        schema_integer("Maximum time to wait for live attachment after launch.", 1),
    );
    properties.insert(
        "intervalMs".to_string(),
        schema_integer("Polling interval between attachment attempts.", 1),
    );
    properties.insert(
        "rpcTimeoutMs".to_string(),
        schema_integer(
            "Maximum time to wait for each live bridge RPC during launch attachment.",
            1,
        ),
    );
    properties.insert(
        "verifyStableMs".to_string(),
        schema_integer(
            "Optional post-attach stability window; zero disables the extra check.",
            0,
        ),
    );
    properties.insert(
        "noDetachSession".to_string(),
        schema_boolean(
            "Unix-only opt-out: keep the launched game in the CLI process session instead of detaching it.",
        ),
    );
    properties.insert(
        "disableBackgroundThrottle".to_string(),
        schema_boolean(
            "Developer opt-in: keep the launched instance responsive while its window is backgrounded (skips the game's background FPS limit and pins vertical sync on, which stops the engine parking its main loop while hidden).",
        ),
    );
    properties.insert(
        "launchArgs".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" },
            "description": "Optional Godot/game arguments appended after configured game.launchArgs for this launch."
        }),
    );
    object_schema(properties, &[])
}

pub(crate) fn game_deploy_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "path".to_string(),
        schema_string("Path to the authored mod project root to build and deploy."),
    );
    properties.insert(
        "build".to_string(),
        schema_boolean("Run the configured mod build command before copying deploy artifacts."),
    );
    properties.insert(
        "restart".to_string(),
        schema_boolean(
            "Stop the current game process and relaunch after copying the bridge and mod payload.",
        ),
    );
    properties.insert(
        "verify".to_string(),
        schema_boolean("Wait for a successful live bridge attachment after deploy or restart."),
    );
    properties.insert(
        "timeoutMs".to_string(),
        schema_integer(
            "Maximum time to wait for launch/attach verification steps.",
            1,
        ),
    );
    properties.insert(
        "intervalMs".to_string(),
        schema_integer(
            "Polling interval between verification attachment attempts.",
            1,
        ),
    );
    properties.insert(
        "rpcTimeoutMs".to_string(),
        schema_integer(
            "Maximum time to wait for each live bridge RPC during deploy verification.",
            1,
        ),
    );
    properties.insert(
        "verifyStableMs".to_string(),
        schema_integer(
            "Optional post-attach stability window for restart or verification attachment; zero disables the extra check.",
            0,
        ),
    );
    object_schema(properties, &["path"])
}

pub(crate) fn assets_extract_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "query".to_string(),
        schema_string(
            "Asset query routed through the existing `assets extract` CLI surface. Accepts filesystem/packed asset path or substring queries, raw font files, direct `res://...` paths, typed model keys such as `model://characters/<id>/visuals`, `model://characters/<id>/iconOutline`, `model://relics/<id>/bigIcon`, and `model://monsters/<id>/visuals`, and composed keys such as `composed://combat-background/<id>/image` or `composed://encounters/<id>/background/image`.",
        ),
    );
    properties.insert(
        "execution".to_string(),
        schema_enum(
            "Execution strategy for extraction: use offline-only, force live-only, or auto-export readable configured resources offline before escalating to live IPC when needed.",
            &["auto", "offline", "live"],
        ),
    );
    properties.insert(
        "format".to_string(),
        schema_enum(
            "Optional artifact format for raster outputs. `auto` preserves offline raster bytes and uses PNG for live previews.",
            &["auto", "png", "webp"],
        ),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["query"])
}

pub(crate) fn assets_explain_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "query".to_string(),
        schema_string(
            "Composed asset query to explain. Supports `composed://combat-background/<id>/image` and checked-in encounter scene packages such as `composed://encounters/kaiser_crab_boss/scene-package`, `composed://encounters/ovicopter_normal/scene-package`, and `composed://encounters/knowledge_demon_boss/scene-package`.",
        ),
    );
    properties.insert(
        "execution".to_string(),
        schema_enum(
            "Execution strategy for explanation. Asset explain currently supports live only.",
            &["live"],
        ),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["query"])
}

pub(crate) fn assets_extract_batch_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "manifest".to_string(),
        schema_string("Path to a generic JSON asset batch manifest with version 0 and assets[]."),
    );
    properties.insert(
        "output".to_string(),
        schema_string(
            "Optional output directory for the batch artifact tree. Defaults to <artifacts.dir>/assets-batch.",
        ),
    );
    properties.insert(
        "execution".to_string(),
        schema_enum(
            "Default extraction strategy for manifest requests that omit execution.",
            &["auto", "offline", "live"],
        ),
    );
    properties.insert(
        "format".to_string(),
        schema_enum(
            "Default artifact format for manifest requests that omit format. `auto` preserves offline raster bytes and uses PNG for live previews.",
            &["auto", "png", "webp"],
        ),
    );
    properties.insert(
        "failFast".to_string(),
        schema_boolean(
            "Stop after the first failed or no-match request and mark later requests skipped.",
        ),
    );
    properties.insert(
        "dryRun".to_string(),
        schema_boolean(
            "Validate the batch request surface without resolving roots, launching the game, or writing artifacts.",
        ),
    );
    insert_code_search_root_properties(&mut properties);
    object_schema(properties, &["manifest"])
}

pub(crate) fn load_fixture_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "path".to_string(),
        schema_string(
            "Path to one authored .sts2.fixture.yaml recipe file. The CLI validates and normalizes the recipe locally, then sends canonical JSON to the bridge.",
        ),
    );
    object_schema(properties, &["path"])
}

pub(crate) fn scenario_export_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "output".to_string(),
        schema_string("Path where the shareable .sts2.scenario.yaml artifact should be written."),
    );
    properties.insert(
        "includeExact".to_string(),
        schema_boolean(
            "Also write an opaque exact sidecar when native save-backed or fixture-backed continuation data is available. The sidecar is hash/size validated and remains separate from the reviewable sparse recipe.",
        ),
    );
    object_schema(properties, &["output"])
}

pub(crate) fn scenario_load_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "path".to_string(),
        schema_string("Path to one shareable .sts2.scenario.yaml artifact."),
    );
    properties.insert(
        "restart".to_string(),
        schema_boolean("Restart the local game before restoring the scenario."),
    );
    properties.insert(
        "timeoutMs".to_string(),
        schema_integer(
            "Maximum time to wait for lifecycle and restore validation steps.",
            1,
        ),
    );
    properties.insert(
        "intervalMs".to_string(),
        schema_integer("Polling interval between validation attempts.", 1),
    );
    properties.insert(
        "allowDegradedLocalMultiplayer".to_string(),
        schema_boolean(
            "Explicitly allow active multiplayer artifacts to restore as degraded local-only state and report omitted remote clients.",
        ),
    );
    object_schema(properties, &["path"])
}

pub(crate) fn skill_install_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "path".to_string(),
        schema_string(
            "Optional skill root where the checked-in spirectl skill pack should be installed.",
        ),
    );
    object_schema(properties, &[])
}

pub(crate) fn screenshot_schema() -> Value {
    let mut properties = Map::new();
    insert_viewport_properties(&mut properties);
    properties.insert(
        "output".to_string(),
        schema_string(
            "Optional explicit PNG output path. When omitted, the CLI writes into the configured artifacts directory.",
        ),
    );
    properties.insert(
        "rpcTimeoutMs".to_string(),
        schema_integer(
            "Maximum bridge RPC time in milliseconds before screenshot capture fails with bridge_rpc_timeout.",
            1,
        ),
    );
    object_schema(properties, &[])
}

pub(crate) fn insert_viewport_properties(properties: &mut Map<String, Value>) {
    properties.insert(
        "preset".to_string(),
        schema_string("Optional viewport preset name from inspect_viewport_presets."),
    );
    properties.insert(
        "width".to_string(),
        schema_integer(
            "Explicit viewport width in pixels when not using a preset.",
            1,
        ),
    );
    properties.insert(
        "height".to_string(),
        schema_integer(
            "Explicit viewport height in pixels when not using a preset.",
            1,
        ),
    );
    properties.insert(
        "presetCatalogs".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" },
            "description": "Optional explicit viewport preset catalog paths loaded in addition to built-ins."
        }),
    );
}

pub(crate) fn screenshot_diff_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "baseline".to_string(),
        schema_string("Path to the baseline PNG file to compare against a live screenshot or explicit actual PNG."),
    );
    properties.insert(
        "actual".to_string(),
        schema_string("Optional explicit PNG path for offline diff mode; when omitted the CLI captures a fresh live screenshot."),
    );
    properties.insert(
        "rpcTimeoutMs".to_string(),
        schema_integer(
            "Maximum bridge RPC time in milliseconds for live screenshot capture when actual is omitted.",
            1,
        ),
    );
    insert_viewport_properties(&mut properties);
    properties.insert(
        "bundleDir".to_string(),
        schema_string("Optional directory where comparison.json, baseline.png, actual.png, diff.png, and any requested foreground/ROI diff PNGs should be written."),
    );
    properties.insert(
        "maxDiffPixels".to_string(),
        schema_integer(
            "Maximum differing pixels allowed before the comparison fails.",
            0,
        ),
    );
    properties.insert(
        "maxDiffRatio".to_string(),
        json!({
            "type": "number",
            "minimum": 0.0
        }),
    );
    properties.insert(
        "pixelTolerance".to_string(),
        json!({
            "type": "integer",
            "minimum": 0,
            "maximum": 255,
            "description": "Maximum absolute per-channel delta that still counts as an identical pixel. Absorbs renderer anti-aliasing jitter; maxDiffPixels and maxDiffRatio still count and threshold whole pixels. Default 0 = exact RGBA."
        }),
    );
    properties.insert(
        "ignoreAlpha".to_string(),
        json!({
            "type": "boolean",
            "description": "Compare RGB only, ignoring the alpha channel."
        }),
    );
    properties.insert(
        "mask".to_string(),
        schema_string("Optional foreground mask PNG path. Nonzero mask pixels select foreground pixels for the foreground comparison."),
    );
    properties.insert(
        "regions".to_string(),
        schema_string("Optional ROI regions JSON path. Accepts either an array of {id,x,y,width,height} regions or an object with a regions array."),
    );
    properties.insert(
        "foregroundMaxDiffRatio".to_string(),
        json!({
            "type": "number",
            "minimum": 0.0,
            "exclusiveMaximum": 1.0
        }),
    );
    properties.insert(
        "roiMaxDiffRatio".to_string(),
        json!({
            "type": "number",
            "minimum": 0.0,
            "exclusiveMaximum": 1.0
        }),
    );
    properties.insert(
        "requiredComparisons".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string", "enum": ["full", "foreground", "roi"] },
            "description": "Comparison modes that must be present. Missing foreground mask or ROI regions produce structured notices and a failed comparison."
        }),
    );
    object_schema(properties, &["baseline"])
}

pub(crate) fn snapshot_export_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "spec".to_string(),
        schema_string("Path to the authored .sts2.snapshot.yaml spec file."),
    );
    properties.insert(
        "output".to_string(),
        schema_string("Directory where the exported baseline bundle should be written."),
    );
    object_schema(properties, &["spec", "output"])
}

pub(crate) fn snapshot_compare_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "spec".to_string(),
        schema_string("Path to the authored .sts2.snapshot.yaml spec file."),
    );
    properties.insert(
        "baseline".to_string(),
        schema_string("Directory containing an exported baseline bundle."),
    );
    properties.insert(
        "bundleDir".to_string(),
        schema_string("Optional directory where current comparison captures should be written."),
    );
    object_schema(properties, &["spec", "baseline"])
}

pub(crate) fn tool_name_schema(description: &str) -> Value {
    let mut properties = Map::new();
    properties.insert("name".to_string(), schema_string(description));
    object_schema(properties, &["name"])
}

pub(crate) fn project_hook_run_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "name".to_string(),
        schema_string("Exact repo-local hook name from project_hook_list."),
    );
    properties.insert(
        "input".to_string(),
        json!({
            "description": "Optional JSON-serializable input payload passed through the hook stdin contract.",
            "type": ["string", "object", "array"]
        }),
    );
    object_schema(properties, &["name"])
}

pub(crate) fn insert_probe_query_properties(properties: &mut Map<String, Value>) {
    properties.insert(
        "query".to_string(),
        schema_string("Optional JSON path used to validate the probe response body, headers, or derived fields."),
    );
    properties.insert(
        "equals".to_string(),
        json!({ "type": ["string", "number", "boolean"] }),
    );
    properties.insert(
        "contains".to_string(),
        json!({ "type": ["string", "number"] }),
    );
    properties.insert("gt".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("gte".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("lt".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("lte".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert(
        "exists".to_string(),
        json!({
            "type": "boolean",
            "const": true
        }),
    );
    properties.insert(
        "notExists".to_string(),
        json!({
            "type": "boolean",
            "const": true
        }),
    );
}

pub(crate) fn http_probe_schema(include_interval: bool) -> Value {
    let mut properties = Map::new();
    properties.insert(
        "url".to_string(),
        schema_string("HTTP or HTTPS URL to request."),
    );
    properties.insert(
        "method".to_string(),
        schema_string("HTTP method to use; defaults to GET."),
    );
    properties.insert(
        "headers".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" }
        }),
    );
    properties.insert(
        "body".to_string(),
        schema_string("Optional raw request body string."),
    );
    properties.insert(
        "timeoutMs".to_string(),
        schema_integer("Maximum request or total wait time in milliseconds.", 1),
    );
    properties.insert(
        "expectStatus".to_string(),
        schema_integer(
            "Optional exact HTTP status code expected from the response.",
            100,
        ),
    );
    properties.insert(
        "expectHeaders".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" }
        }),
    );
    insert_probe_query_properties(&mut properties);
    if include_interval {
        properties.insert(
            "intervalMs".to_string(),
            schema_integer(
                "Polling interval in milliseconds between repeated HTTP checks.",
                1,
            ),
        );
    }
    object_schema(properties, &["url"])
}

pub(crate) fn fetch_probe_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "source".to_string(),
        schema_string("Filesystem path or supported source argument for dev fetch."),
    );
    properties.insert(
        "output".to_string(),
        schema_string("Optional filesystem path where the fetched bytes should be written."),
    );
    properties.insert(
        "expectSha256".to_string(),
        schema_string("Optional expected SHA-256 digest for the fetched bytes."),
    );
    insert_probe_query_properties(&mut properties);
    object_schema(properties, &["source"])
}

pub(crate) fn websocket_probe_schema() -> Value {
    let mut properties = Map::new();
    properties.insert("url".to_string(), schema_string("WebSocket URL to open."));
    properties.insert(
        "headers".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" }
        }),
    );
    properties.insert(
        "sendText".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" }
        }),
    );
    properties.insert(
        "expectText".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" }
        }),
    );
    properties.insert(
        "timeoutMs".to_string(),
        schema_integer("Maximum session duration in milliseconds.", 1),
    );
    object_schema(properties, &["url"])
}

pub(crate) fn log_health_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "limit".to_string(),
        schema_integer("Maximum number of recent log entries to inspect.", 1),
    );
    properties.insert(
        "tail".to_string(),
        schema_integer(
            "Inspect the latest matching entries instead of the default recent window size.",
            1,
        ),
    );
    properties.insert(
        "afterCursor".to_string(),
        schema_integer(
            "Inspect only entries whose cursor is greater than this value.",
            0,
        ),
    );
    properties.insert(
        "level".to_string(),
        schema_enum(
            "Optional minimum log level filter.",
            &["trace", "debug", "info", "warn", "error"],
        ),
    );
    properties.insert(
        "target".to_string(),
        schema_string("Optional substring filter applied to structured log targets."),
    );
    properties.insert(
        "excludeTargets".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" }
        }),
    );
    properties.insert(
        "excludeMessageRegexes".to_string(),
        json!({
            "type": "array",
            "items": { "type": "string" }
        }),
    );
    object_schema(properties, &[])
}

pub(crate) fn diagnostics_schema() -> Value {
    let mut properties = match log_health_schema() {
        Value::Object(schema) => schema
            .get("properties")
            .and_then(Value::as_object)
            .cloned()
            .unwrap_or_default(),
        _ => Map::new(),
    };
    insert_viewport_properties(&mut properties);
    properties.insert(
        "bundleDir".to_string(),
        schema_string("Optional directory where diagnostics.json and sibling evidence artifacts should be written."),
    );
    object_schema(properties, &[])
}

pub(crate) fn hot_reload_status_schema() -> Value {
    object_schema(
        Map::from_iter([(
            "project".to_string(),
            schema_string(
                "Explicit local M57/M60 hot-reload project root containing sts2.hot-reload.yaml.",
            ),
        )]),
        &["project"],
    )
}

pub(crate) fn hot_reload_schema() -> Value {
    object_schema(
        Map::from_iter([
            (
                "project".to_string(),
                schema_string(
                    "Explicit local M57/M60 hot-reload project root containing sts2.hot-reload.yaml.",
                ),
            ),
            (
                "build".to_string(),
                schema_boolean("Run the project's logicBuildProfile before requesting reload."),
            ),
            (
                "wait".to_string(),
                schema_boolean(
                    "Wait for the shell to complete the reload request and return the final report.",
                ),
            ),
            (
                "timeoutMs".to_string(),
                schema_integer("Maximum reload wait duration in milliseconds.", 1),
            ),
            (
                "intervalMs".to_string(),
                schema_integer(
                    "Polling interval in milliseconds for wait-oriented reload flows.",
                    1,
                ),
            ),
        ]),
        &["project"],
    )
}

pub(crate) fn debug_step_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "session".to_string(),
        schema_string("Optional leased debugger session id that should own this step request."),
    );
    properties.insert(
        "kind".to_string(),
        schema_enum("Requested live debug step kind.", &["frame", "action"]),
    );
    properties.insert(
        "count".to_string(),
        schema_integer(
            "Optional positive step count; defaults to 1 when omitted.",
            1,
        ),
    );
    object_schema(properties, &["kind"])
}

pub(crate) fn debug_status_schema() -> Value {
    object_schema(
        Map::from_iter([(
            "session".to_string(),
            schema_string(
                "Optional leased debugger session id used to explain ownership or conflict in the returned status.",
            ),
        )]),
        &[],
    )
}

pub(crate) fn debug_session_start_schema() -> Value {
    object_schema(
        Map::from_iter([
            (
                "name".to_string(),
                schema_string("Optional human-readable debugger session label."),
            ),
            (
                "role".to_string(),
                schema_enum(
                    "Requested debugger session role; controller is the compatibility default.",
                    &["controller", "observer"],
                ),
            ),
            (
                "pause".to_string(),
                schema_boolean("Pause immediately after the debugger session is leased."),
            ),
            (
                "leaseTimeoutMs".to_string(),
                schema_integer(
                    "Lease duration in milliseconds before the bridge expires the session.",
                    1,
                ),
            ),
        ]),
        &[],
    )
}

pub(crate) fn debug_session_status_schema() -> Value {
    object_schema(
        Map::from_iter([(
            "id".to_string(),
            schema_string("Stable leased debugger session id returned by debug_session_start."),
        )]),
        &["id"],
    )
}

pub(crate) fn debug_session_end_schema() -> Value {
    object_schema(
        Map::from_iter([
            (
                "id".to_string(),
                schema_string("Stable leased debugger session id returned by debug_session_start."),
            ),
            (
                "resume".to_string(),
                schema_boolean("Resume the runtime before releasing the session lease."),
            ),
        ]),
        &["id"],
    )
}

pub(crate) fn debug_events_schema() -> Value {
    object_schema(
        Map::from_iter([
            (
                "session".to_string(),
                schema_string("Optional debugger session id used to describe caller role."),
            ),
            (
                "fromSequence".to_string(),
                schema_integer(
                    "Replay cursor sequence to start from; 0 asks the bridge for its retained window.",
                    0,
                ),
            ),
            (
                "limit".to_string(),
                schema_integer("Maximum events to return per bounded replay request.", 1),
            ),
            (
                "follow".to_string(),
                schema_boolean("Poll until at least one event is available or timeoutMs elapses."),
            ),
            (
                "timeoutMs".to_string(),
                schema_integer("Bounded follow timeout in milliseconds.", 1),
            ),
        ]),
        &[],
    )
}

pub(crate) fn debug_wait_schema() -> Value {
    object_schema(
        Map::from_iter([
            (
                "session".to_string(),
                schema_string("Leased debugger session id that owns the wait operation."),
            ),
            (
                "timeoutMs".to_string(),
                schema_integer(
                    "Maximum wait duration in milliseconds before the CLI reports a timeout result.",
                    1,
                ),
            ),
        ]),
        &["session"],
    )
}

pub(crate) fn debug_breakpoint_add_schema() -> Value {
    let mut properties = Map::new();
    properties.insert(
        "session".to_string(),
        schema_string(
            "Optional leased debugger session id that should own this breakpoint registration.",
        ),
    );
    properties.insert(
        "path".to_string(),
        schema_string("Observable state query path to evaluate for the debug breakpoint."),
    );
    properties.insert(
        "kind".to_string(),
        schema_enum(
            "Breakpoint behavior: match evaluates a predicate, while change fires when the observed JSON value changes.",
            &["match", "change"],
        ),
    );
    properties.insert(
        "name".to_string(),
        schema_string("Optional human-readable breakpoint label."),
    );
    properties.insert(
        "equals".to_string(),
        json!({
            "type": ["string", "number", "boolean"]
        }),
    );
    properties.insert(
        "contains".to_string(),
        json!({
            "type": ["string", "number"]
        }),
    );
    properties.insert(
        "regex".to_string(),
        schema_string("Regular expression matched against a string state value."),
    );
    properties.insert("gt".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("gte".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("lt".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert("lte".to_string(), json!({ "type": ["string", "number"] }));
    properties.insert(
        "exists".to_string(),
        json!({
            "type": "boolean",
            "const": true
        }),
    );
    properties.insert(
        "notExists".to_string(),
        json!({
            "type": "boolean",
            "const": true
        }),
    );
    properties.insert(
        "minHitCount".to_string(),
        schema_integer(
            "Minimum hit count before the breakpoint is reported as matched; defaults to 1.",
            1,
        ),
    );
    properties.insert(
        "autoRemoveOnHit".to_string(),
        schema_boolean("Automatically remove the breakpoint after the first reported hit."),
    );
    object_schema(properties, &["path"])
}

pub(crate) fn debug_breakpoint_remove_schema() -> Value {
    object_schema(
        Map::from_iter([
            (
                "session".to_string(),
                schema_string(
                    "Optional leased debugger session id that should own this breakpoint removal.",
                ),
            ),
            (
                "id".to_string(),
                schema_string(
                    "Stable breakpoint id returned by breakpoint_list or breakpoint_add.",
                ),
            ),
        ]),
        &["id"],
    )
}

mod ai_tools;
pub(crate) use ai_tools::*;
