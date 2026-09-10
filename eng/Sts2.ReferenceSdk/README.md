# STS2 reference SDK

This project pins the assemblies needed only to compile the live bridge in CI.
It copies a reviewed allowlist into its build output; those files are never
included in a bridge release archive or the `Spirectl.Sts2` NuGet package.

Run `scripts/verify-sts2-reference-sdk.sh` after changing the package pin or
allowlist. Keep the lock file checked in and restore it with `--locked-mode`.
