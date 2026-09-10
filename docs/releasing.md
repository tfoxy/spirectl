# Releasing

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
