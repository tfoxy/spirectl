fn main() {
    let proto_root = std::path::PathBuf::from("../proto");
    let bridge_props = std::path::PathBuf::from("../bridge-mod/Directory.Build.props");
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

    let bridge_semver = read_bridge_semver(&bridge_props);
    println!("cargo:rustc-env=SPIRECTL_BRIDGE_SEMVER={bridge_semver}");
    println!("cargo:rustc-env=SPIRECTL_BRIDGE_VERSION=spirectl-bridge/{bridge_semver}");

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

fn extract_xml_value(contents: &str, tag: &str) -> Option<String> {
    let start_tag = format!("<{tag}>");
    let end_tag = format!("</{tag}>");
    let start = contents.find(&start_tag)? + start_tag.len();
    let end = contents[start..].find(&end_tag)? + start;
    Some(contents[start..end].trim().to_string())
}
