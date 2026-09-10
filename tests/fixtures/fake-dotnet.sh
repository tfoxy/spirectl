#!/usr/bin/env bash
printf '%s\n' "$*" >>"$FAKE_DOTNET_LOG"

if [[ "${FAKE_DOTNET_FAIL:-}" == "true" ]]; then
  exit 1
fi

if [[ "$1" == "build" ]]; then
  if [[ "${FAKE_DOTNET_BUILD_FAIL:-}" == "true" ]]; then
    printf '%s\n' "${FAKE_DOTNET_STDERR:-fake dotnet build failed}" >&2
    exit "${FAKE_DOTNET_EXIT_CODE:-1}"
  fi

  if [[ "${FAKE_DOTNET_BUILD_OUTPUT:-}" != "" ]]; then
    mkdir -p "$(dirname "$FAKE_DOTNET_BUILD_OUTPUT")"
    printf 'fake helper dll\n' >"$FAKE_DOTNET_BUILD_OUTPUT"
  fi

  exit 0
fi

if [[ "$1" == *".dll" ]]; then
  if [[ "${FAKE_DOTNET_STDOUT:-}" != "" ]]; then
    printf '%s\n' "$FAKE_DOTNET_STDOUT"
  else
    printf '{"command":"%s","status":"ok"}\n' "$2"
  fi
  if [[ "${FAKE_DOTNET_STDERR:-}" != "" ]]; then
    printf '%s\n' "$FAKE_DOTNET_STDERR" >&2
  fi
  exit "${FAKE_DOTNET_EXIT_CODE:-0}"
fi

if [[ "$1" == "run" && "${FAKE_DOTNET_REAL:-}" != "" ]]; then
  exec "$FAKE_DOTNET_REAL" "$@"
fi

if [[ "$1" == "run" && -x "$(dirname "$0")/dotnet-real" ]]; then
  exec -a dotnet "$(dirname "$0")/dotnet-real" "$@"
fi

if [[ "$1" == "run" ]]; then
  if [[ "${FAKE_DOTNET_STDOUT:-}" != "" ]]; then
    printf '%s\n' "$FAKE_DOTNET_STDOUT"
  else
    after_separator=false
    command_name="unknown"
    for arg in "$@"; do
      if [[ "$after_separator" == "true" ]]; then
        command_name="$arg"
        break
      fi
      if [[ "$arg" == "--" ]]; then
        after_separator=true
      fi
    done
    printf '{"command":"%s","status":"ok"}\n' "$command_name"
  fi
  if [[ "${FAKE_DOTNET_STDERR:-}" != "" ]]; then
    printf '%s\n' "$FAKE_DOTNET_STDERR" >&2
  fi
  exit "${FAKE_DOTNET_EXIT_CODE:-0}"
fi

if [[ "$1" == "format" && "$3" == "style" ]]; then
  includes=()
  previous=""
  for arg in "$@"; do
    if [[ "$previous" == "--include" ]]; then
      includes+=("$arg")
    fi
    previous="$arg"
  done

  if [[ "${FAKE_DOTNET_FORMAT_STYLE_TOUCH_BIN_OBJ:-}" == "true" ]]; then
    for include in "${includes[@]}"; do
      if [[ -d "$include" ]]; then
        mkdir -p "$include/bin/Debug" "$include/obj/Debug"
        printf 'generated\n' >"$include/bin/Debug/generated.txt"
        printf 'generated\n' >"$include/obj/Debug/generated.txt"
      fi
    done
  fi

  if [[ "${FAKE_DOTNET_FORMAT_STYLE_EDIT_SELECTED_SOURCE:-}" != "" ]]; then
    printf '\n// fake style formatter edit\n' >>"${FAKE_DOTNET_FORMAT_STYLE_EDIT_SELECTED_SOURCE}"
  fi

  if [[ "${FAKE_DOTNET_FORMAT_STYLE_FAIL_ON_NESTED_INCLUDE:-}" == "true" ]]; then
    for include in "${includes[@]}"; do
      nested="$include/$(basename "$include")"
      if [[ -e "$nested" ]]; then
        printf 'nested selected include copy exists: %s\n' "$nested" >&2
        exit 23
      fi
    done
  fi
fi

if [[ "${FAKE_DOTNET_FORMAT_REPORT:-}" != "" ]]; then
  report_dir=""
  report_name="${FAKE_DOTNET_FORMAT_REPORT_NAME:-format-report.json}"
  previous=""
  for arg in "$@"; do
    if [[ "$previous" == "--report" ]]; then
      report_dir="$arg"
      break
    fi
    previous="$arg"
  done
  if [[ "$report_dir" != "" ]]; then
    mkdir -p "$report_dir"
    cp "$FAKE_DOTNET_FORMAT_REPORT" "$report_dir/$report_name"
  fi
fi
