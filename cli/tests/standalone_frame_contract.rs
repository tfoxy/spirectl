use sts2::bridge::ipc_protocol;

#[test]
fn standalone_frame_version_matches_dotnet_host() {
    let host_source = include_str!(
        "../../bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Common/StandaloneBridgeTransportProtocol.cs"
    );
    let version = host_source
        .lines()
        .find_map(|line| line.trim().strip_prefix("private const ushort Version = "))
        .expect("standalone host declares its frame version")
        .trim_end_matches(';')
        .parse::<u16>()
        .expect("host frame version is an unsigned integer");
    assert_eq!(
        version,
        ipc_protocol::VERSION,
        "CLI and standalone host must use the same frame header version"
    );
}
