---
name: check-release-status
description: Use this read-only skill to inspect the latest Phantom.Workspaces release(s) and the most recent release.yml run without re-cutting a release.
---

# Skill: Check release status

Answer "did my release succeed?" by inspecting the GitHub Release and the latest
`release.yml` run. This skill is read-only and never tags, pushes, or publishes.

## Commands

Inspect the latest release and its assets:

```powershell
gh release view --json tagName,isDraft,isPrerelease,assets,publishedAt
gh release list --limit 5
```

Inspect the most recent release pipeline runs:

```powershell
gh run list --workflow release.yml --limit 5
gh run view --log
```

Confirm the expected assets are present:

```powershell
gh release view <tag> --json assets --jq '.assets[].name'
```

## Rules

1. This skill is strictly read-only: never create tags, releases, or push.
2. Use `gh` for all GitHub operations.
3. Report whether the in-app updater would see the release: only published, non-prerelease, non-draft releases count for the stable channel.
4. Verify both `win-x64` and `win-arm64` zips and their `.sha256` checksum files are attached.
5. Never print secrets or tokens.
