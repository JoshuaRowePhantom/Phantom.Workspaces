---
name: create-release
description: Use this skill to cut a new versioned release of Phantom.Workspaces. Orchestrates the green-tests gate, version bump, annotated tag, and watches the release pipeline.
---

# Skill: Create release

Ship a new `vX.Y.Z` release by preparing and triggering the `release.yml` pipeline.
Tagging is the single trigger; GitHub Actions does the build, sign, package, and publish.

## Commands

Confirm a clean, up-to-date release branch:

```powershell
git status --porcelain
git rev-parse --abbrev-ref HEAD
git pull --ff-only
```

Gate on a green test run (use the run-tests skill):

```powershell
.\scripts\run-tests.ps1
```

Confirm the next version is strictly greater than the latest release:

```powershell
gh release list --limit 5
git tag --list "v*" --sort=-v:refname | Select-Object -First 5
```

Create and push the annotated tag (only after explicit user confirmation):

```powershell
git tag -a vX.Y.Z -m "Phantom.Workspaces vX.Y.Z"
git push origin vX.Y.Z
```

Watch the release pipeline and verify assets:

```powershell
gh run watch --workflow release.yml
gh release view vX.Y.Z
```

## Rules

1. Never tag or push without explicit user instruction.
2. Always gate the release on a green `.\scripts\run-tests.ps1` (reuse the run-tests skill); never release on a failing or untested tree.
3. The working tree must be clean and on the release branch (`main`), pulled up to date.
4. Versions are SemVer and strictly increasing; confirm the major/minor/patch bump with the user and that it exceeds the latest release tag.
5. Use `gh` for all GitHub operations (releases, runs, tags).
6. Surface the tag name, version, and workflow run URL before any destructive action.
7. Never put secrets in commands or logs; signing/license secrets live only in Actions secrets.
8. Never push to `microsoft/winget-pkgs` (winget is deferred).
9. Verify the published release has the per-arch `win-x64`/`win-arm64` zips plus their `.sha256` files and auto-generated notes.
