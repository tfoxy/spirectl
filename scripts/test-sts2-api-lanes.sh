#!/usr/bin/env bash
# Exercise the imported props directly without compiling or launching the game.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$repo_root/.local"
python3 - "$repo_root" "${1:-$repo_root/bridge-mod/Sts2GameApi.props}" <<'PY'
import json
import pathlib
import subprocess
import sys
import tempfile
from xml.sax.saxutils import quoteattr

repo_root, props = map(pathlib.Path, sys.argv[1:])
passed = 0
with tempfile.TemporaryDirectory(prefix="lane-test-", dir=repo_root / ".local") as scratch:
    project = pathlib.Path(scratch) / "lanes.proj"
    project.write_text(f'<Project><Import Project={quoteattr(str(props.resolve()))} /></Project>')

    def check(version, lane, untested=False, *, override=None, required=True, allow_mismatch=False):
        global passed
        args = ["dotnet", "msbuild", str(project), "-nologo", "-m:1",
                "-t:ValidateSts2GameApiLane", "-p:Sts2GameApiReleaseInfoPath=",
                f"-p:Sts2GameVersion={version}", f"-p:Sts2GameApiRequired={str(required).lower()}",
                "-getProperty:Sts2GameApi,Sts2GameApiUntested"]
        if override:
            args.append(f"-p:Sts2GameApi={override}")
        if allow_mismatch:
            args.append("-p:Sts2GameApiAllowVersionMismatch=true")
        result = subprocess.run(args, text=True, capture_output=True)
        output = result.stdout + result.stderr
        if lane is None:
            assert result.returncode != 0, (version, override, output)
            assert str(props.resolve()) in output, output
            assert "error MSB4184" not in output, output
        else:
            assert result.returncode == 0, (version, override, output)
            values = json.loads(result.stdout[result.stdout.index('{'):])["Properties"]
            assert values["Sts2GameApi"] == lane, (version, values)
            assert values["Sts2GameApiUntested"] == str(untested).lower(), (version, values)
            assert ("warning : Untested STS2 game build" in output) == (untested and required), output
            if untested and required:
                for text in [version, lane, str(props.resolve())]:
                    assert text in output, output
        passed += 1

    for version, lane, untested in [
        ("v0.111.0", "v111", False), ("v0.111.1", "v111", False),
        ("v0.111.2", "v111", True), ("v0.112.9", "v111", True),
        ("v0.113.0", "v113", False), ("v0.113.1", "v113", False),
        ("v0.113.2", "v113", True), ("v0.114.0", "v113", False),
        ("v0.114.1", "v113", True),
        ("v0.120.0", "v113", True), ("v1.0.0", "v113", True),
        ("v2147483647.0.0", "v113", True), ("v0.1000.0", "v113", True),
    ]:
        check(version, lane, untested)
    for version in ["v0.100.0", "", "junk", "0.113.0", "v0.113", "v0.113.0.1",
                    "v0.113.0-beta", "v00.113.0", "v0.-113.0", "v0.2147483648.0", "v2147483648.0.0",
                    "v0.113.2147483648", "v0.9999999999999999999.0"]:
        check(version, None)
    check("", "v111", override="v111")  # Reference SDK has no release_info.
    check("", None, override="v999")
    check("v0.114.0", None, override="v111")
    check("v0.114.0", "v111", False, override="v111", allow_mismatch=True)
    check("", "", required=False)  # Offline builds need no lane.
print(f"{passed} passed; 0 failed")
PY
