using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// Lints the release skills under <c>.github/skills</c> so they keep valid frontmatter and the
/// required sections. Guards against convention drift (e.g. a skill that forgets to gate releases
/// on the test suite or bypasses <c>gh</c>).
/// </summary>
public sealed class ReleaseSkillLintTests
{
    private static readonly string[] ReleaseSkills =
    {
        "create-release",
        "check-release-status",
        "draft-release-notes",
        "rollback-release",
    };

    public static TheoryData<string> SkillNames()
    {
        var data = new TheoryData<string>();
        foreach (var skill in ReleaseSkills)
        {
            data.Add(skill);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void Skill_HasValidFrontmatterAndRequiredSections(string skillName)
    {
        var path = Path.Combine(FindRepositoryRoot().FullName, ".github", "skills", skillName, "SKILL.md");
        Assert.True(File.Exists(path), $"Missing skill file: {path}");

        var content = File.ReadAllText(path).Replace("\r\n", "\n");

        var frontmatter = Regex.Match(content, "\\A---\\n(.*?)\\n---\\n", RegexOptions.Singleline);
        Assert.True(frontmatter.Success, $"{skillName}: missing YAML frontmatter block.");

        var frontmatterBody = frontmatter.Groups[1].Value;
        Assert.Matches($"(?m)^name:\\s*{Regex.Escape(skillName)}\\s*$", frontmatterBody);
        Assert.Matches("(?m)^description:\\s*\\S.*$", frontmatterBody);

        Assert.Contains("\n## Commands\n", content);
        Assert.Contains("\n## Rules\n", content);
    }

    [Fact]
    public void CreateReleaseSkill_GatesOnTestsAndUsesGh()
    {
        var path = Path.Combine(FindRepositoryRoot().FullName, ".github", "skills", "create-release", "SKILL.md");
        var content = File.ReadAllText(path);

        Assert.Contains("run-tests.ps1", content);
        Assert.Contains("gh ", content);
    }

    [Fact]
    public void ReleaseSkills_DoNotPushToWinget()
    {
        foreach (var skill in ReleaseSkills)
        {
            var path = Path.Combine(FindRepositoryRoot().FullName, ".github", "skills", skill, "SKILL.md");
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("git push origin master", content);
            Assert.DoesNotContain("winget-pkgs PR", content);
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
