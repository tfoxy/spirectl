# Commits and releases

How a change gets from a working tree into a release people can read. One convention across the
three published repos — `spirectl`, `sts2-couch-coop` and `godot-scene-web` share this document and
the same hook; only the changelog voice differs. Registry and trusted-publisher specifics stay in
[releasing.md](releasing.md).

## The short version

```
type(scope)!: imperative subject, <=72 chars, no trailing period

Body prose: what changed and why, wrapped at <=100 columns. Optional.

Changelog: one sentence an integrator would read — feat/fix/perf only, or `none`
```

- Enforced by `scripts/githooks/commit-msg` **on `main` only**. Feature branches are free.
- Installed by `scripts/install-agent-config.sh` (`core.hooksPath`), so it holds in every worktree.
- Emergency bypass: `git commit --no-verify`.

## Types

Nine, closed. If none fits, the commit is probably two commits.

| Type | For | Trailer |
| --- | --- | --- |
| `feat` | a capability a consumer can use | required |
| `fix` | a defect a consumer could hit | required |
| `perf` | a measured speed or memory win | required |
| `refactor` | no behaviour change — including retiring a shipped flag or experiment | — |
| `docs` | documentation only | — |
| `test` | tests, benches, harnesses, fixtures, scenarios | — |
| `build` | build, dependencies, packaging | — |
| `ci` | workflows and the release pipeline | — |
| `chore` | everything else | — |

Earlier history used a wider, drifting set. Those fold in: `cleanup` → `refactor`, `bench` → `test`
(or `perf` when a number moved), and `presentation` / `bridge` / `catalog` / `fixtures` / `ai`
become **scopes**, e.g. `fix(bridge): …`.

Scope is optional, lowercase, free-form. `!` before the colon marks a breaking change, with a
`BREAKING CHANGE:` footer saying what breaks — this repo ships a Rust CLI, an npm wrapper and the
`Spirectl.Sts2` NuGet package, so a break is somebody's build.

## The `Changelog:` trailer

Release notes are not assembled from subjects. A release spans hundreds of commits whose subjects
are correct and unreadable, so the note is written *once, at commit time*, by whoever still has the
context:

```
fix(bridge): bound the node lookup to the mounted scene

Resolve node paths against the scene that is actually mounted rather than the last
one seen, so a lookup after a scene change cannot return a stale node.

Changelog: Node lookups no longer return a stale node after a scene change.
```

Rules that keep it cheap:

- Only `feat`, `fix` and `perf` need one. The other six types — where most agent commits land —
  need nothing at all.
- `Changelog: none` is a complete answer, and the hook accepts it. Writing the word is the point:
  it makes "is this visible to a consumer?" a decision rather than an omission.
- Write it for someone integrating this, not for the reviewer of the diff: what changes in their
  build, their call site, their output. Name the flag, the command, the export.
- `Refs: sts2-couch-coop@<sha>` is an optional second trailer for work that spans the repos.

## Branches and merging

`main` gets **one commit per coherent change**. A self-contained change commits straight to `main`;
longer work goes on a branch, where nothing is enforced, and comes back as a single squash commit:

```bash
git switch main
git merge --squash my-branch
git commit            # the .gitmessage template opens; this message is the one that is checked
```

The reason for squashing is legibility: `git log --oneline` on `main` should read as a list of
changes, and `git blame` should land on a commit whose body explains the whole change.

## Cutting a release

1. Draft the notes from the trailers:

   ```bash
   scripts/collect-changelog.sh            # since the last tag
   ```

   It prints the trailers grouped under Keep a Changelog headings, then lists every `feat`/`fix`/
   `perf` commit with **no** trailer. Decide about each of those before moving on.

2. Rewrite the draft under a new `## [x.y.z] - YYYY-MM-DD` heading in `CHANGELOG.md`. The
   `release-notes` skill does this step.

3. Bump the three versions that [releasing.md](releasing.md) requires to agree — `cli/Cargo.toml`,
   `bridge-mod/Directory.Build.props` (`Version` and `AssemblyVersion`), `npm-wrapper/package.json`
   — and commit them with the changelog:

   ```bash
   scripts/verify-release-version.sh v0.1.0   # also checks the changelog section exists
   git commit -m 'chore(release): v0.1.0'
   ```

4. Tag and push the tag. Tags are annotated and signed (`tag.gpgsign`, set by the installer when a
   signing key exists) because the release workflow triggers on them:

   ```bash
   git tag v0.1.0 -m 'spirectl v0.1.0'
   git push origin main
   git push origin v0.1.0
   ```

`release.yml` then publishes npm, NuGet and the GitHub Release, whose body is
`scripts/changelog-section.sh` output — the release fails if that version has no section. Pushing is
always explicit; no agent pushes a branch or a tag on its own.

## Reading the history

`git log` on `main` is the history. **`priv` is not.** It is a frozen local archive of the
development history from before this repo was published — 2078 commits, unrelated to `main`'s
history, in a mix of styles that predates this document. It is kept for archaeology, never
published, never committed to, and never a model for how to write a commit here.
