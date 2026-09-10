#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

BRIDGE_MOD_DIR_NAME="spirectlbridge"
BRIDGE_MANIFEST_NAME="${BRIDGE_MOD_DIR_NAME}.json"
DEFAULT_ARTIFACTS_DIR="$REPO_ROOT/.sts2/artifacts/live-bridge"
DEFAULT_SOCKET_PATH="/tmp/spirectl-bridge.sock"
DEFAULT_TIMEOUT_SECONDS=30

usage() {
    cat <<'EOF'
Usage:
  scripts/live-bridge.sh deploy --game-path <path> [--assemblies-dir <path>] [--mods-dir <path>] [--socket-path <path>] [--configuration Debug|Release] [--artifacts-dir <path>]
  scripts/live-bridge.sh verify [--config <path>] [--socket-path <path>] [--timeout-seconds <n>]
EOF
}

info() {
    printf '[live-bridge] %s\n' "$*"
}

die() {
    printf '[live-bridge] error: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "Required command '$1' is not installed."
}

bridge_semver() {
    local semver
    semver="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$REPO_ROOT/bridge-mod/Directory.Build.props" | head -n 1)"
    [ -n "$semver" ] || die "Unable to read <Version> from bridge-mod/Directory.Build.props."
    printf '%s\n' "$semver"
}

yaml_section_value() {
    local file_path="$1"
    local section="$2"
    local key="$3"

    [ -f "$file_path" ] || return 1

    awk -v section="$section" -v key="$key" '
        function trim(value) {
            sub(/^[[:space:]]+/, "", value)
            sub(/[[:space:]]+$/, "", value)
            return value
        }

        /^[[:space:]]*#/ { next }

        /^[^[:space:]][^:]*:[[:space:]]*$/ {
            current = $0
            sub(/:.*/, "", current)
            in_section = (current == section)
            next
        }

        in_section {
            pattern = "^[[:space:]]*" key ":[[:space:]]*(.*)$"
            if (match($0, pattern, parts)) {
                value = trim(parts[1])
                gsub(/^'\''|'\''$/, "", value)
                gsub(/^"|"$/, "", value)
                print value
                exit
            }

            if ($0 ~ /^[^[:space:]]/) {
                in_section = 0
            }
        }
    ' "$file_path"
}

resolve_default_config_path() {
    if [ -f "$REPO_ROOT/sts2.config.yaml" ]; then
        printf '%s\n' "$REPO_ROOT/sts2.config.yaml"
    fi
}

resolve_assemblies_dir() {
    local game_path="$1"
    local assemblies_dir="${2:-}"

    if [ -n "$assemblies_dir" ]; then
        printf '%s\n' "$assemblies_dir"
        return 0
    fi

    local candidate
    candidate="$(find "$game_path" -maxdepth 1 -mindepth 1 -type d -name 'data_sts2_*' | sort | head -n 1 || true)"
    [ -n "$candidate" ] || die "Could not find a data_sts2_* directory under '$game_path'."
    printf '%s\n' "$candidate"
}

validate_assemblies_dir() {
    local assemblies_dir="$1"
    [ -d "$assemblies_dir" ] || die "Assemblies directory '$assemblies_dir' does not exist."
    [ -f "$assemblies_dir/sts2.dll" ] || die "Expected '$assemblies_dir/sts2.dll'."
    [ -f "$assemblies_dir/GodotSharp.dll" ] || die "Expected '$assemblies_dir/GodotSharp.dll'."
}

copy_if_present() {
    local source_path="$1"
    local target_dir="$2"

    if [ -f "$source_path" ]; then
        cp -f "$source_path" "$target_dir/"
    fi
}

stage_publish_artifacts() {
    local publish_dir="$1"
    local target_dir="$2"

    while IFS= read -r source_path; do
        cp -f "$source_path" "$target_dir/"
    done < <(find "$publish_dir" -maxdepth 1 -type f \( -name '*.dll' -o -name '*.pdb' \) | sort)
}

write_manifest() {
    local target_dir="$1"
    local semver="$2"

    cat >"$target_dir/$BRIDGE_MANIFEST_NAME" <<EOF
{
  "id": "$BRIDGE_MOD_DIR_NAME",
  "version": "$semver",
  "has_pck": false,
  "has_dll": true,
  "affects_gameplay": false
}
EOF
}

validate_staged_layout() {
    local target_dir="$1"

    [ -f "$target_dir/$BRIDGE_MANIFEST_NAME" ] || die "Missing $BRIDGE_MANIFEST_NAME in '$target_dir'."
    [ -f "$target_dir/${BRIDGE_MOD_DIR_NAME}.dll" ] || die "Missing ${BRIDGE_MOD_DIR_NAME}.dll in '$target_dir'."
    [ -f "$target_dir/Spirectl.BridgeMod.Sts2Host.dll" ] || die "Missing Spirectl.BridgeMod.Sts2Host.dll in '$target_dir'."
    [ -f "$target_dir/Spirectl.BridgeMod.dll" ] || die "Missing Spirectl.BridgeMod.dll in '$target_dir'."

    local extra_json
    extra_json="$(find "$target_dir" -maxdepth 1 -type f -name '*.json' ! -name "$BRIDGE_MANIFEST_NAME" -print -quit || true)"
    [ -z "$extra_json" ] || die "Unexpected JSON artifact '$extra_json' staged for deploy."

    local forbidden_json
    forbidden_json="$(find "$target_dir" -maxdepth 1 -type f \( -name '*.deps.json' -o -name '*.runtimeconfig.json' \) -print -quit || true)"
    [ -z "$forbidden_json" ] || die "Forbidden runtime metadata '$forbidden_json' staged for deploy."
}

build_sts2_cli() {
    require_command cargo
    cargo build -p sts2 --manifest-path "$REPO_ROOT/Cargo.toml" >/dev/null
}

run_sts2() {
    "$REPO_ROOT/target/debug/sts2" "$@"
}

deploy_command() {
    local game_path=""
    local assemblies_dir=""
    local mods_dir=""
    local socket_path="$DEFAULT_SOCKET_PATH"
    local configuration="Debug"
    local artifacts_dir="$DEFAULT_ARTIFACTS_DIR"

    while [ $# -gt 0 ]; do
        case "$1" in
            --game-path)
                game_path="${2:-}"
                shift 2
                ;;
            --assemblies-dir)
                assemblies_dir="${2:-}"
                shift 2
                ;;
            --mods-dir)
                mods_dir="${2:-}"
                shift 2
                ;;
            --socket-path)
                socket_path="${2:-}"
                shift 2
                ;;
            --configuration)
                configuration="${2:-}"
                shift 2
                ;;
            --artifacts-dir)
                artifacts_dir="${2:-}"
                shift 2
                ;;
            --help|-h)
                usage
                exit 0
                ;;
            *)
                die "Unknown deploy argument '$1'."
                ;;
        esac
    done

    [ -n "$game_path" ] || die "--game-path is required for deploy."
    [ -d "$game_path" ] || die "Game path '$game_path' does not exist."
    case "$configuration" in
        Debug|Release) ;;
        *) die "--configuration must be Debug or Release." ;;
    esac

    require_command dotnet

    assemblies_dir="$(resolve_assemblies_dir "$game_path" "$assemblies_dir")"
    validate_assemblies_dir "$assemblies_dir"
    if [ -z "$mods_dir" ]; then
        mods_dir="$game_path/mods"
    fi
    if [ -e "$mods_dir" ] && [ ! -d "$mods_dir" ]; then
        die "Mods directory '$mods_dir' exists but is not a directory."
    fi

    local semver
    semver="$(bridge_semver)"
    local publish_dir="$artifacts_dir/host-publish"
    local stage_dir="$artifacts_dir/staging/$BRIDGE_MOD_DIR_NAME"
    local deploy_dir="$mods_dir/$BRIDGE_MOD_DIR_NAME"
    local loader_project="$REPO_ROOT/bridge-mod/src/Spirectl.BridgeMod.Sts2Host.Loader/Spirectl.BridgeMod.Sts2Host.Loader.csproj"
    local host_project="$REPO_ROOT/bridge-mod/src/Spirectl.BridgeMod.Sts2Host/Spirectl.BridgeMod.Sts2Host.csproj"
    local loader_output_dir="$REPO_ROOT/bridge-mod/src/Spirectl.BridgeMod.Sts2Host.Loader/bin/$configuration/net9.0"

    rm -rf "$publish_dir" "$stage_dir"
    mkdir -p "$publish_dir" "$stage_dir" "$mods_dir"

    info "Building bootstrap loader against '$assemblies_dir'..."
    dotnet build "$loader_project" \
        --configuration "$configuration" \
        -p:Sts2AssembliesDir="$assemblies_dir" \
        -p:EnableSts2LiveHost=true

    info "Publishing live bridge host..."
    dotnet publish "$host_project" \
        --configuration "$configuration" \
        --output "$publish_dir" \
        -p:Sts2AssembliesDir="$assemblies_dir" \
        -p:EnableSts2LiveHost=true

    info "Staging deployable artifacts under '$artifacts_dir'..."
    write_manifest "$stage_dir" "$semver"
    copy_if_present "$loader_output_dir/${BRIDGE_MOD_DIR_NAME}.dll" "$stage_dir"
    copy_if_present "$loader_output_dir/${BRIDGE_MOD_DIR_NAME}.pdb" "$stage_dir"
    stage_publish_artifacts "$publish_dir" "$stage_dir"
    validate_staged_layout "$stage_dir"

    rm -rf "$deploy_dir"
    mkdir -p "$deploy_dir"
    cp -f "$stage_dir"/* "$deploy_dir/"

    info "Deployed bridge artifacts into '$deploy_dir'."
    info "Bridge socket path: $socket_path"
    if [ "$socket_path" != "$DEFAULT_SOCKET_PATH" ]; then
        info "Before starting STS2, set SPIRECTL_BRIDGE_SOCKET_PATH='$socket_path' in the game launch environment."
    fi
    info "Next steps:"
    printf '  1. Restart STS2 manually so the loader picks up the new bridge artifacts.\n'
    printf '  2. Ensure sts2.config.yaml uses transport.kind=ipc'
    if [ "$socket_path" != "$DEFAULT_SOCKET_PATH" ]; then
        printf ' and transport.ipcPath=%s' "$socket_path"
    fi
    printf '.\n'
    printf '  3. Run %s verify' "$0"
    if [ "$socket_path" != "$DEFAULT_SOCKET_PATH" ]; then
        printf ' --socket-path %s' "$socket_path"
    fi
    printf '\n'
}

verify_deployed_layout() {
    local mod_dir="$1"
    [ -d "$mod_dir" ] || die "Expected deployed mod directory '$mod_dir'."
    validate_staged_layout "$mod_dir"
}

verify_command() {
    local config_path=""
    local socket_path=""
    local timeout_seconds="$DEFAULT_TIMEOUT_SECONDS"

    while [ $# -gt 0 ]; do
        case "$1" in
            --config)
                config_path="${2:-}"
                shift 2
                ;;
            --socket-path)
                socket_path="${2:-}"
                shift 2
                ;;
            --timeout-seconds)
                timeout_seconds="${2:-}"
                shift 2
                ;;
            --help|-h)
                usage
                exit 0
                ;;
            *)
                die "Unknown verify argument '$1'."
                ;;
        esac
    done

    if ! [[ "$timeout_seconds" =~ ^[0-9]+$ ]] || [ "$timeout_seconds" -le 0 ]; then
        die "--timeout-seconds must be a positive integer."
    fi

    if [ -z "$config_path" ]; then
        config_path="$(resolve_default_config_path || true)"
    fi
    if [ -n "$config_path" ] && [ ! -f "$config_path" ]; then
        die "Config file '$config_path' does not exist."
    fi

    local config_game_path=""
    local config_mods_dir=""
    if [ -n "$config_path" ]; then
        config_game_path="$(yaml_section_value "$config_path" game path || true)"
        config_mods_dir="$(yaml_section_value "$config_path" game modsDir || true)"
    fi
    if [ -z "$config_mods_dir" ] && [ -n "$config_game_path" ] && [ "$config_game_path" != "auto" ]; then
        config_mods_dir="$config_game_path/mods"
    fi

    if [ -n "$config_mods_dir" ]; then
        info "Checking deployed layout under '$config_mods_dir/$BRIDGE_MOD_DIR_NAME'..."
        verify_deployed_layout "$config_mods_dir/$BRIDGE_MOD_DIR_NAME"
    else
        info "Skipping deployed layout check because game.path/modsDir are not configured."
    fi

    if [ -z "$socket_path" ] && [ -n "$config_path" ]; then
        socket_path="$(yaml_section_value "$config_path" transport ipcPath || true)"
    fi
    if [ -z "$socket_path" ]; then
        socket_path="$DEFAULT_SOCKET_PATH"
    fi

    build_sts2_cli

    local verify_config
    verify_config="$(mktemp)"
    trap 'rm -f "$verify_config"' EXIT
    cat >"$verify_config" <<EOF
transport:
  kind: ipc
  ipcPath: $socket_path
EOF

    local info_output=""
    local state_output=""
    local last_output=""
    local deadline=$(( $(date +%s) + timeout_seconds ))

    info "Polling live bridge over '$socket_path' for up to ${timeout_seconds}s..."
    while [ "$(date +%s)" -le "$deadline" ]; do
        if info_output="$(run_sts2 --json --config "$verify_config" game info 2>&1)"; then
            if state_output="$(run_sts2 --json --config "$verify_config" state 2>&1)"; then
                printf '%s\n' "$info_output"
                printf '%s\n' "$state_output"
                info "Live IPC verification succeeded."
                return 0
            fi
            last_output="$state_output"
        else
            last_output="$info_output"
        fi

        sleep 1
    done

    die "Timed out waiting for a successful IPC handshake/state check on '$socket_path'. Last output: $last_output"
}

main() {
    local subcommand="${1:-}"
    case "$subcommand" in
        deploy)
            shift
            deploy_command "$@"
            ;;
        verify)
            shift
            verify_command "$@"
            ;;
        --help|-h|"")
            usage
            ;;
        *)
            die "Unknown subcommand '$subcommand'."
            ;;
    esac
}

main "$@"
