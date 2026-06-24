---
name: rollback-release
description: Use this guarded skill to take a bad Phantom.Workspaces release out of the stable channel by marking it pre-release/draft and, if needed, re-promoting the previous version.
---

# Skill: Rollback release

Conservatively remove a bad release from the stable channel so the in-app updater
stops offering it. This skill never force-pushes or rewrites history and always
asks for explicit confirmation before changing a release.

## Commands

Inspect the affected and previous releases:

```powershell
gh release list --limit 5
gh release view <bad-tag> --json tagName,isDraft,isPrerelease,assets
```

Demote the bad release out of the stable channel (after confirmation):

```powershell
gh release edit <bad-tag> --prerelease
```

If the previous release must become latest again (after confirmation):

```powershell
gh release edit <previous-tag> --latest
```

## Rules

1. Always ask for explicit user confirmation before editing any release.
2. Never force-push, delete tags, or rewrite Git history.
3. Marking the bad release as pre-release (or draft) is the primary remedy: the stable-channel updater ignores non-published/pre-release releases.
4. Use `gh` for all GitHub operations.
5. Surface exactly which release/tag is being changed before acting.
6. Never print secrets or tokens.
