---
name: draft-release-notes
description: Use this skill to generate and preview release notes for the pending range (last tag to HEAD) so a human can review before tagging.
---

# Skill: Draft release notes

Preview the changelog for the unreleased range before cutting a release. This
complements GitHub's auto-generated notes configured in `.github/release.yml`.

## Commands

Find the last release tag and the pending commit range:

```powershell
git describe --tags --abbrev=0 --match "v*"
git log "$(git describe --tags --abbrev=0 --match 'v*')..HEAD" --oneline
```

Preview merged pull requests since the last release:

```powershell
gh pr list --state merged --search "merged:>=<last-release-date>" --limit 50
```

Generate notes for a prospective tag (without publishing):

```powershell
gh api repos/:owner/:repo/releases/generate-notes -f tag_name=vX.Y.Z -f previous_tag_name=<last-tag> --jq '.body'
```

## Rules

1. This skill only previews notes; it never creates tags or releases.
2. Use `gh` for all GitHub operations.
3. Categorization follows `.github/release.yml`; surface the categories so the human can adjust PR labels before tagging.
4. Never print secrets or tokens.
