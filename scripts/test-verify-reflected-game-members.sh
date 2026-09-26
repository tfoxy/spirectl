#!/usr/bin/env bash
# Self-test for scripts/verify-reflected-game-members.sh and its runner, `validate.sh reflected-members`.
#
# Two-sided, like scripts/test-claude-guard.sh: a check that never fails is worse than none, so every
# clean case has a drifting twin, and the runner's SKIP (77) is asserted distinct from drift (3) and
# from a refusal (2).
#
# Everything is synthetic. The fixture "games" are a few invented member names in a fixture decompile
# corpus pair and a fixture assemblies pair; nothing here reads or contains game code, and the real
# corpora are not needed (`validate.sh reflected-members` without arguments is the run that uses them).
#
# usage: scripts/test-verify-reflected-game-members.sh      (or: scripts/validate.sh reflected-members --self-test)
# exit:  0 every case passed, 1 a case failed, 77 a tool the script needs is missing

set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
SCRIPT="$PWD/scripts/verify-reflected-game-members.sh"
VALIDATE="$PWD/scripts/validate.sh"

for tool in rg strings comm md5sum; do
  command -v "$tool" >/dev/null || { echo "SKIP: the self-test needs $tool" >&2; exit 77; }
done

pass=0; fail=0
TMP="$(mktemp -d)" || { echo "mktemp failed" >&2; exit 1; }
trap 'rm -rf "$TMP"' EXIT
# The runner and the script must not pick up the caller's couch checkout or a stray corpus.
unset SPIRECTL_COUCH_ROOT

out=""; rc=0
run() { out="$("$@" 2>&1)"; rc=$?; }

expect_exit() { # expect_exit <desc> <want> <cmd...>
  local desc="$1" want="$2"; shift 2
  run "$@"
  if [ "$rc" = "$want" ]; then pass=$((pass+1)); else
    fail=$((fail+1)); printf 'FAIL  %s: want exit %s, got %s\n' "$desc" "$want" "$rc" >&2
    printf '%s\n' "$out" | sed 's/^/        /' >&2
  fi
}
expect_out() { # expect_out <desc> <fixed string>  — against the last run
  if printf '%s' "$out" | grep -qF -- "$2"; then pass=$((pass+1)); else
    fail=$((fail+1)); printf 'FAIL  %s: output lacks: %s\n' "$1" "$2" >&2
    printf '%s\n' "$out" | sed 's/^/        /' >&2
  fi
}
expect_no_out() { # expect_no_out <desc> <fixed string>
  if printf '%s' "$out" | grep -qF -- "$2"; then
    fail=$((fail+1)); printf 'FAIL  %s: output has: %s\n' "$1" "$2" >&2
    printf '%s\n' "$out" | sed 's/^/        /' >&2
  else pass=$((pass+1)); fi
}

# --- fixtures ---------------------------------------------------------------------------------------

# Source trees: one reads through the four readers (with one wrapped call, one nested under a lane seam
# directory that must be excluded), one is a CouchCoop-shaped tree.
mkdir -p "$TMP/src/Pkg/GameApi/V107" "$TMP/couch/src/Patches"
cat > "$TMP/src/Pkg/Reads.cs" <<'EOF'
class Reads
{
    void Go(object node)
    {
        var a = Sts2LiveIntrospection.GetMemberValue(node, "AlphaMember");
        Sts2LiveIntrospection.InvokeMethod(
            node,
            "BetaMethod");
        Sts2LiveIntrospection.TrySetMemberValue(node, "GammaField", 1);
        var g = Sts2LiveIntrospection.GetMemberValue(node, "GhostName");
    }
}
EOF
cat > "$TMP/src/Pkg/GameApi/V107/Seam.cs" <<'EOF'
class Seam { object Go(object n) => Sts2LiveIntrospection.GetMemberValue(n, "LaneOnlyName"); }
EOF
cat > "$TMP/couch/src/Patches/Patch.cs" <<'EOF'
class Patch
{
    internal const string ThetaFieldName = "_thetaField";
    static readonly (System.Type Type, string Name)[] Targets =
    [
        (typeof(Foo), "IotaMethod"),
    ];

    void Apply()
    {
        var m = AccessTools.Method(typeof(Foo), "EpsilonMethod", []);
        var f = AccessTools.Field(
            typeof(Foo),
            "_zetaField");
        var g = AccessTools.PropertyGetter(typeof(Foo), "get_Delta");
    }
}
EOF

# A decompile corpus is <root>/decompile/{sts2,GodotSharp}/*.cs. `corpus <dir> <words...>` writes one whose
# sources mention exactly those words (as whole words, in ordinary code), plus a dir-unique line so no two
# corpora are byte-identical unless a case wants that.
corpus() {
  local dir="$1"; shift
  mkdir -p "$dir/sts2" "$dir/GodotSharp"
  : > "$dir/sts2/Types.cs"
  local w
  for w in "$@"; do printf 'public object %s;\n' "$w" >> "$dir/sts2/Types.cs"; done
  printf '// %s\n' "$dir" > "$dir/GodotSharp/Engine.cs"
}
# The readers' names and the couch names, all present.
common=(AlphaMember BetaMethod GammaField IotaMethod _thetaField _zetaField Delta)
# A has the lane-seam name, clean-b does not: the pair is clean only because the seam is excluded.
corpus "$TMP/corpus-a/decompile" "${common[@]}" EpsilonMethod LaneOnlyName
corpus "$TMP/corpus-clean-b/decompile" "${common[@]}" EpsilonMethod
# One-sided reader name: B has BetaMethodExtra (a longer name containing BetaMethod), not BetaMethod itself.
corpus "$TMP/corpus-drift-b/decompile" AlphaMember BetaMethodExtra GammaField IotaMethod _thetaField _zetaField Delta EpsilonMethod
# One-sided couch name: only B has EpsilonMethod.
corpus "$TMP/corpus-couchdrift-a/decompile" "${common[@]}"
corpus "$TMP/corpus-couchdrift-b/decompile" "${common[@]}" EpsilonMethod

# Assemblies: strings the binary carries, NUL separated, the way a metadata string heap is.
assemblies() {
  local dir="$1"; shift
  mkdir -p "$dir"
  local w
  { printf 'BSJB\0'; for w in "$@"; do printf '%s\0' "$w"; done; printf 'get_Delta\0'; } > "$dir/sts2.dll"
  printf 'GodotStub\0%s\0' "$dir" > "$dir/GodotSharp.dll"
}
assemblies "$TMP/dll-a" AlphaMember BetaMethod GammaField IotaMethod _thetaField _zetaField EpsilonMethod
assemblies "$TMP/dll-clean-b" AlphaMember BetaMethod GammaField IotaMethod _thetaField _zetaField EpsilonMethod
assemblies "$TMP/dll-drift-b" AlphaMember GammaField IotaMethod _thetaField _zetaField EpsilonMethod

# Every case scans the fixture tree, not spirectl's own, unless it says otherwise.
own=(--no-default-root --root "$TMP/src")

# --- the script -------------------------------------------------------------------------------------

echo "== corpus pairs =="
expect_exit 'clean corpus pair (the toolchain dir, not decompile/, also resolves)' 0 \
  "$SCRIPT" "${own[@]}" "$TMP/corpus-a" "$TMP/corpus-clean-b/decompile"
expect_out  'the clean run says so' 'clean: every by-name member read'
expect_out  'a name in neither build is information, not a failure' 'GhostName [root '
expect_no_out 'the lane seam is excluded, wherever it nests (else LaneOnlyName would diverge)' 'LaneOnlyName'
# B has BetaMethodExtra: a longer name that contains BetaMethod must not count as BetaMethod in a corpus.
expect_exit 'one-sided reader name: 3' 3 "$SCRIPT" "${own[@]}" "$TMP/corpus-a/decompile" "$TMP/corpus-drift-b/decompile"
expect_out  'the drift names the missing name' 'BetaMethod [root '
expect_out  'the drift names the direction' 'MISSING from'
expect_exit 'the drift is one-sided both ways' 3 "$SCRIPT" "${own[@]}" "$TMP/corpus-drift-b/decompile" "$TMP/corpus-a/decompile"

echo "== assemblies pairs =="
expect_exit 'clean assemblies pair' 0 "$SCRIPT" "${own[@]}" "$TMP/dll-a" "$TMP/dll-clean-b"
expect_exit 'assemblies pair: one-sided name is 3' 3 "$SCRIPT" "${own[@]}" "$TMP/dll-a" "$TMP/dll-drift-b"
expect_out  'the assemblies drift names the name' 'BetaMethod [root '

echo "== couch root =="
expect_exit 'couch names are opt-in: not scanned without --couch-root' 0 \
  "$SCRIPT" "${own[@]}" "$TMP/corpus-couchdrift-a/decompile" "$TMP/corpus-couchdrift-b/decompile"
expect_exit 'a one-sided couch name is 3 with --couch-root' 3 \
  "$SCRIPT" "${own[@]}" --couch-root "$TMP/couch" "$TMP/corpus-couchdrift-a/decompile" "$TMP/corpus-couchdrift-b/decompile"
expect_out  'the couch name is attributed to couch src' 'EpsilonMethod [couch src]'
expect_out  'every couch shape is swept (AccessTools wrapped, typeof table, const pin, accessor)' 'swept 5 by-name member reads under couch src'
expect_exit 'SPIRECTL_COUCH_ROOT opts in as well' 3 \
  env SPIRECTL_COUCH_ROOT="$TMP/couch" "$SCRIPT" "${own[@]}" "$TMP/corpus-couchdrift-a/decompile" "$TMP/corpus-couchdrift-b/decompile"
expect_exit 'an accessor name is probed by its property name in a corpus (get_Delta vs Delta)' 0 \
  "$SCRIPT" "${own[@]}" --couch-root "$TMP/couch" "$TMP/corpus-a/decompile" "$TMP/corpus-clean-b/decompile"
expect_no_out 'the accessor is not reported as in neither build' 'get_Delta'
mkdir -p "$TMP/emptycouch/src" && printf 'class C {}\n' > "$TMP/emptycouch/src/C.cs"
expect_exit 'a couch tree with no by-name reads is not a refusal (zero is the finding)' 0 \
  "$SCRIPT" "${own[@]}" --couch-root "$TMP/emptycouch" "$TMP/corpus-a/decompile" "$TMP/corpus-clean-b/decompile"
expect_exit 'couch alone with zero reads compares nothing and is clean' 0 \
  "$SCRIPT" --no-default-root --couch-root "$TMP/emptycouch" "$TMP/corpus-a/decompile" "$TMP/corpus-clean-b/decompile"
expect_out  'the zero is reported' 'swept 0 by-name member reads under couch src'

echo "== spirectl's own tree is the default =="
expect_exit 'default root sweeps bridge-mod/src (names absent from the fixtures are info only)' 0 \
  "$SCRIPT" "$TMP/corpus-a/decompile" "$TMP/corpus-clean-b/decompile"
if printf '%s' "$out" | grep -Eq 'swept [1-9][0-9]* by-name member reads under spirectl bridge-mod/src'; then pass=$((pass+1)); else
  fail=$((fail+1)); echo 'FAIL  the default root found no by-name reads under bridge-mod/src' >&2; fi

echo "== refusals: 2 =="
expect_exit 'a build compared with itself' 2 "$SCRIPT" "${own[@]}" "$TMP/corpus-a/decompile" "$TMP/corpus-a"
expect_exit 'assemblies against a corpus' 2 "$SCRIPT" "${own[@]}" "$TMP/dll-a" "$TMP/corpus-a/decompile"
expect_out  'the mixed-kind reason' 'different kinds'
expect_exit 'a path that is not a build' 2 "$SCRIPT" "${own[@]}" "$TMP/nowhere" "$TMP/corpus-a"
expect_exit 'a --root tree with no reads is a renamed-readers refusal' 2 \
  "$SCRIPT" --no-default-root --root "$TMP/emptycouch/src" "$TMP/corpus-a" "$TMP/corpus-clean-b"
expect_exit 'no tree to sweep' 2 "$SCRIPT" --no-default-root "$TMP/corpus-a" "$TMP/corpus-clean-b"
expect_exit 'a --couch-root that is not a checkout' 2 "$SCRIPT" "${own[@]}" --couch-root "$TMP/nowhere" "$TMP/corpus-a" "$TMP/corpus-clean-b"
expect_exit 'one build only' 2 "$SCRIPT" "${own[@]}" "$TMP/corpus-a"
expect_exit 'unknown option' 2 "$SCRIPT" --frobnicate "$TMP/corpus-a" "$TMP/corpus-clean-b"
expect_exit '--help is not a refusal' 0 "$SCRIPT" --help

# --- the runner -------------------------------------------------------------------------------------

echo "== runner: validate.sh reflected-members =="
expect_exit 'explicit builds, clean' 0 "$VALIDATE" reflected-members "${own[@]}" --a "$TMP/corpus-a" --b "$TMP/corpus-clean-b"
expect_exit 'explicit builds, drift is 3' 3 "$VALIDATE" reflected-members "${own[@]}" --a "$TMP/corpus-a" --b "$TMP/corpus-drift-b"
expect_out  'drift names the name' 'BetaMethod'
expect_exit 'drift as json' 3 "$VALIDATE" reflected-members "${own[@]}" --a "$TMP/corpus-a" --b "$TMP/corpus-drift-b" --json
expect_out  'json says drift' '"status":"drift"'
expect_exit 'explicit builds that are not builds are a refusal (2), never a SKIP' 2 \
  "$VALIDATE" reflected-members "${own[@]}" --a "$TMP/nowhere" --b "$TMP/corpus-a"
expect_exit '--a without --b' 2 "$VALIDATE" reflected-members "${own[@]}" --a "$TMP/corpus-a"
expect_exit 'unknown leg argument' 2 "$VALIDATE" reflected-members --frobnicate

echo "== runner: discovery, SKIP and --strict =="
expect_exit 'no corpus pair on this machine: SKIP is 77, not 3 and not 2' 77 \
  env SPIRECTL_COUCH_ROOT="$TMP/nowhere" "$VALIDATE" reflected-members "${own[@]}"
expect_out  'the skip says SKIP' 'SKIP:'
expect_out  'the skip names the path it looked at' "$TMP/nowhere/.sts2/toolchain-public/decompile"
expect_out  'the skip says how to fix it' 'project recover --kind decompile'
expect_exit 'the skip as json' 77 env SPIRECTL_COUCH_ROOT="$TMP/nowhere" "$VALIDATE" reflected-members "${own[@]}" --json
expect_out  'json says skipped' '"status":"skipped"'
expect_exit '--strict turns the SKIP into a failure' 2 \
  env SPIRECTL_COUCH_ROOT="$TMP/nowhere" "$VALIDATE" reflected-members "${own[@]}" --strict
expect_out  'the strict failure says FAIL' 'FAIL:'
# A CouchCoop-shaped checkout: the two lane corpora under .sts2/, and a src/ (SPIRECTL_COUCH_ROOT also opts
# in to scanning it, and this src has no reads).
mkdir -p "$TMP/couch2/src" "$TMP/couch2/.sts2/toolchain-public" "$TMP/couch2/.sts2/toolchain-public-beta"
printf 'class C {}\n' > "$TMP/couch2/src/C.cs"
cp -r "$TMP/corpus-a/decompile" "$TMP/couch2/.sts2/toolchain-public/decompile"
cp -r "$TMP/corpus-clean-b/decompile" "$TMP/couch2/.sts2/toolchain-public-beta/decompile"
expect_exit 'default discovery finds the lane corpora under a couch checkout' 0 \
  env SPIRECTL_COUCH_ROOT="$TMP/couch2" "$VALIDATE" reflected-members "${own[@]}"
expect_out  'and compares them' 'A (corpus)'
expect_exit '--couch-root names the checkout too' 0 "$VALIDATE" reflected-members "${own[@]}" --couch-root "$TMP/couch2"
rm -rf "$TMP/couch2/.sts2/toolchain-public-beta/decompile"
cp -r "$TMP/corpus-drift-b/decompile" "$TMP/couch2/.sts2/toolchain-public-beta/decompile"
expect_exit 'discovered corpora that drift are 3' 3 env SPIRECTL_COUCH_ROOT="$TMP/couch2" "$VALIDATE" reflected-members "${own[@]}"

echo "== runner: --self-test is wired =="
# Not run recursively here (this IS the self-test); assert only that the leg resolves the script.
expect_exit 'the leg documents itself' 0 "$VALIDATE" reflected-members --help
expect_out  'help lists exit 77' '77  SKIP'

echo
echo "verify-reflected-game-members self-test: $pass passed, $fail failed"
[ "$fail" = 0 ]
