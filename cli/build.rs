fn main() {
    let proto_root = std::path::PathBuf::from("../proto");
    let bridge_props = std::path::PathBuf::from("../bridge-mod/Directory.Build.props");
    let game_api_props = std::path::PathBuf::from("../bridge-mod/Sts2GameApi.props");
    let proto_files = [
        proto_root.join("spirectl/v0/common.proto"),
        proto_root.join("spirectl/v0/errors.proto"),
        proto_root.join("spirectl/v0/console.proto"),
        proto_root.join("spirectl/v0/actions.proto"),
        proto_root.join("spirectl/v0/asset.proto"),
        proto_root.join("spirectl/v0/combat_preview.proto"),
        proto_root.join("spirectl/v0/debug.proto"),
        proto_root.join("spirectl/v0/fixture.proto"),
        proto_root.join("spirectl/v0/handshake.proto"),
        proto_root.join("spirectl/v0/hot_reload.proto"),
        proto_root.join("spirectl/v0/lifecycle.proto"),
        proto_root.join("spirectl/v0/logs.proto"),
        proto_root.join("spirectl/v0/map_drawings.proto"),
        proto_root.join("spirectl/v0/mods.proto"),
        proto_root.join("spirectl/v0/models.proto"),
        proto_root.join("spirectl/v0/reference.proto"),
        proto_root.join("spirectl/v0/runtime.proto"),
        proto_root.join("spirectl/v0/runtime_scene.proto"),
        proto_root.join("spirectl/v0/scenario.proto"),
        proto_root.join("spirectl/v0/screenshot.proto"),
        proto_root.join("spirectl/v0/bridge.proto"),
    ];

    for file in &proto_files {
        println!("cargo:rerun-if-changed={}", file.display());
    }
    println!("cargo:rerun-if-changed={}", proto_root.display());
    println!("cargo:rerun-if-changed={}", bridge_props.display());
    println!("cargo:rerun-if-changed={}", game_api_props.display());

    let bridge_semver = read_bridge_semver(&bridge_props);
    println!("cargo:rustc-env=SPIRECTL_BRIDGE_SEMVER={bridge_semver}");
    println!("cargo:rustc-env=SPIRECTL_BRIDGE_VERSION=spirectl-bridge/{bridge_semver}");

    let lane_table = read_sts2_api_lane_table(&game_api_props);
    println!(
        "cargo:rustc-env=SPIRECTL_STS2_API_LANES={}",
        lane_table.lanes
    );
    println!(
        "cargo:rustc-env=SPIRECTL_STS2_API_LANE_TABLE={}",
        lane_table.table
    );
    println!(
        "cargo:rustc-env=SPIRECTL_STS2_API_RELEASABLE_LANES={}",
        lane_table.releasable_lanes
    );

    let protoc_path = protoc_bin_vendored::protoc_bin_path().expect("vendored protoc path");
    unsafe {
        std::env::set_var("PROTOC", protoc_path);
    }

    tonic_build::configure()
        .build_client(true)
        .build_server(true)
        .compile_protos(
            &proto_files
                .iter()
                .map(std::path::PathBuf::as_path)
                .collect::<Vec<_>>(),
            &[proto_root.as_path()],
        )
        .expect("compile protobuf contracts");
}

fn read_bridge_semver(path: &std::path::Path) -> String {
    let contents = std::fs::read_to_string(path).expect("read bridge version props");
    extract_xml_value(&contents, "Version")
        .expect("Version property in bridge Directory.Build.props")
}

struct Sts2ApiLaneTable {
    lanes: String,
    table: String,
    releasable_lanes: String,
}

/// Compile in `bridge-mod/Sts2GameApi.props`'s STS2 API lane table rather than
/// restating it in Rust. Four readers share that one file — MSBuild here,
/// MSBuild in the CouchCoop repo's root `Directory.Build.props`, this build
/// script, and `scripts/sts2-api-lanes.sh` — so a new supported game build is one
/// row in one place. Its header documents the shape every property is held to:
/// flat text, semicolon delimited, the table semicolon fenced at both ends.
fn read_sts2_api_lane_table(path: &std::path::Path) -> Sts2ApiLaneTable {
    let contents = std::fs::read_to_string(path).expect("read STS2 game API lane props");
    let lanes = extract_xml_value(&contents, "Sts2GameApiLanes")
        .expect("Sts2GameApiLanes property in Sts2GameApi.props");
    let table = extract_xml_value(&contents, "Sts2GameApiLaneTable")
        .expect("Sts2GameApiLaneTable property in Sts2GameApi.props");
    let releasable_lanes = extract_xml_value(&contents, "Sts2GameApiReleasableLanes")
        .expect("Sts2GameApiReleasableLanes property in Sts2GameApi.props");

    assert!(
        table.starts_with(';') && table.ends_with(';'),
        "Sts2GameApiLaneTable must be semicolon fenced at both ends so a row lookup is an exact \
         match on ';<version>='; got {table:?}"
    );
    let known = lanes
        .split(';')
        .filter(|lane| !lane.is_empty())
        .collect::<Vec<_>>();
    for row in table.split(';').filter(|row| !row.is_empty()) {
        let (version, lane) = row.split_once('=').unwrap_or_else(|| {
            panic!("Sts2GameApiLaneTable row {row:?} is not '<version>=<lane>'")
        });
        assert!(
            !version.is_empty() && known.contains(&lane),
            "Sts2GameApiLaneTable row {row:?} names a lane that is not in Sts2GameApiLanes \
             ({lanes:?})"
        );
    }
    // Releasable is a SUBSET of supported, never a separate list: a lane becomes
    // releasable when the pinned reference SDK can declare its game build.
    for lane in releasable_lanes.split(';').filter(|lane| !lane.is_empty()) {
        assert!(
            known.contains(&lane),
            "Sts2GameApiReleasableLanes names '{lane}', which is not in Sts2GameApiLanes ({lanes:?})"
        );
    }

    Sts2ApiLaneTable {
        lanes,
        table,
        releasable_lanes,
    }
}

fn extract_xml_value(contents: &str, tag: &str) -> Option<String> {
    let start_tag = format!("<{tag}>");
    let end_tag = format!("</{tag}>");
    let start = contents.find(&start_tag)? + start_tag.len();
    let end = contents[start..].find(&end_tag)? + start;
    Some(contents[start..end].trim().to_string())
}
