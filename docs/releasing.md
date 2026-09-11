# Releasing

Before tagging, write the release notes: `CHANGELOG.md` needs a section for the version, drafted
from the `Changelog:` trailers on the commits since the last tag. That is step 1–3 of
[commit-and-release.md](commit-and-release.md), and `scripts/verify-release-version.sh` now refuses
a version with no section — the workflow publishes that same section as the GitHub Release body
instead of generating notes from commit subjects.

Push a strict semantic-version tag such as `v0.1.0`. The tag-triggered
[`release.yml`](../.github/workflows/release.yml) first verifies that the Rust
CLI, bridge/NuGet package, and npm wrapper all declare that exact version. It
then produces Linux x64 and Windows x64 CLI downloads, a verified bridge ZIP,
and `Spirectl.Sts2` before publishing the registries and creating the GitHub
Release. It does not build or publish `presentation/web`.

Before the first tag, configure npm's trusted publisher for
`tfoxy/spirectl`, `.github/workflows/release.yml`, and the `release`
environment, following [npm trusted publishing](https://docs.npmjs.com/trusted-publishers/).
Configure the matching NuGet.org trusted publisher for `Spirectl.Sts2`, set the
repository workflow/environment claims, and add the `NUGET_USER` GitHub secret
required by `NuGet/login@v1`; see [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

The workflow refuses malformed or mismatched tags, an existing GitHub Release,
missing assets, and game/reference DLLs in the bridge or NuGet payload.
