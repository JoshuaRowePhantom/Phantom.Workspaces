namespace Phantom.Workspaces.Tests;

public sealed class RepositorySourceInventoryTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(
        "artifacts", "source-inventory-tests", Guid.NewGuid().ToString("N")));

    public RepositorySourceInventoryTests()
    {
        this.Write(".git", "gitdir: isolated-source-inventory-fixture");
    }

    [Fact]
    public void Inventory_ForeignRepositoriesAndWorktrees_DoNotContributeProjectsOrPolicyViolations()
    {
        this.Write("Owned.csproj", "<Project />");
        this.Write("Owned.cs", "class Owned {}");
        this.Write(@"worktrees\15\.git", "gitdir: external-worktree-metadata");
        this.Write(@"worktrees\15\Foreign.csproj", """<Project><ItemGroup><ProjectReference Include="Missing.csproj" /></ItemGroup></Project>""");
        this.Write(@"worktrees\15\Foreign.Tests\Test.cs", "AgentDefinition.FromJson(json);");
        this.Write(@"clones\foreign\.git\config", "[core]");
        this.Write(@"clones\foreign\Foreign.csproj", "<Project />");
        this.Write(@"clones\foreign\Production.cs", "AgentManifest.FromYaml(yaml);");
        this.Write("Phantom.Workspaces.slnx", """<Solution><Project Path="Owned.csproj" /></Solution>""");

        var inventory = new RepositorySourceInventory(this.root);

        Assert.Equal(Path.Combine(this.root, "Owned.csproj"), Assert.Single(inventory.ProjectFiles));
        Assert.Equal(Path.Combine(this.root, "Owned.cs"), Assert.Single(inventory.SourceFiles));
        Assert.Empty(SolutionFileTests.FindMissingProjectReferences(inventory));
        Assert.Empty(PhantomAgentSchemaCentralizationTests.FindViolations(inventory));
    }

    [Fact]
    public void Inventory_RegisteredSubmoduleProjectsAndReferences_RemainInScope()
    {
        this.Write(".gitmodules", "[submodule \"vendor\"]\n path = vendor\n");
        this.Write(@"vendor\.git", "gitdir: registered-module");
        this.Write(@"vendor\Vendor.csproj", """<Project><ItemGroup><ProjectReference Include="support\Support.csproj" /></ItemGroup></Project>""");
        this.Write(@"vendor\support\Support.csproj", "<Project />");
        this.Write(@"vendor\.gitmodules", "[submodule \"nested\"]\n path = nested\n");
        this.Write(@"vendor\nested\.git", "gitdir: registered-nested-module");
        this.Write(@"vendor\nested\Nested.csproj", "<Project />");
        this.Write(@"vendor\foreign\.git", "gitdir: foreign-worktree");
        this.Write(@"vendor\foreign\Foreign.csproj", "<Project />");
        this.Write("Phantom.Workspaces.slnx", """
            <Solution>
              <Project Path="vendor\Vendor.csproj" />
              <Project Path="vendor\nested\Nested.csproj" />
            </Solution>
            """);

        var inventory = new RepositorySourceInventory(this.root);

        Assert.Equal(3, inventory.ProjectFiles.Count);
        Assert.Equal("vendor/support/Support.csproj", Assert.Single(SolutionFileTests.FindMissingProjectReferences(inventory)));
    }

    [Fact]
    public void Inventory_IgnoredButOwnedSourceAndProjects_StillEnforceBothGuards()
    {
        this.Write(".gitignore", "ignored-owned/\n");
        this.Write(@"ignored-owned\Owned.csproj", """<Project><ItemGroup><ProjectReference Include="missing\Missing.csproj" /></ItemGroup></Project>""");
        this.Write(@"ignored-owned\Production.cs", "AgentDefinition.FromJson(json);");
        this.Write(@"ignored-owned\obj\Generated.cs", "AgentDefinition.FromJson(json);");
        this.Write(@"ignored-owned\bin\Generated.csproj", "<Project />");
        this.Write("Phantom.Workspaces.slnx", """<Solution><Project Path="ignored-owned\Owned.csproj" /></Solution>""");

        var inventory = new RepositorySourceInventory(this.root);

        Assert.Single(inventory.ProjectFiles);
        Assert.Equal("ignored-owned/missing/Missing.csproj", Assert.Single(SolutionFileTests.FindMissingProjectReferences(inventory)));
        Assert.Contains("Production.cs", Assert.Single(PhantomAgentSchemaCentralizationTests.FindViolations(inventory)));
    }

    [Theory]
    [InlineData("<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>")]
    [InlineData("<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup>")]
    public void Inventory_NestedOwnedTestProject_UsesProjectOwnershipNotOuterPathSegment(string projectMetadata)
    {
        this.Write(@"src\Production\Production.csproj", "<Project />");
        this.Write(@"src\Production\Production.cs", "McpTool.FromJson(json);");
        this.Write(@"src\NestedTests\ArbitraryProjectName.csproj", $"<Project>{projectMetadata}</Project>");
        this.Write(@"src\NestedTests\Test.cs", "McpTool.FromJson(json);");

        var inventory = new RepositorySourceInventory(this.root);

        Assert.True(inventory.IsTestSource(Path.Combine(this.root, @"src\NestedTests\Test.cs")));
        Assert.False(inventory.IsTestSource(Path.Combine(this.root, @"src\Production\Production.cs")));
        Assert.Contains("Production.cs", Assert.Single(PhantomAgentSchemaCentralizationTests.FindViolations(inventory)));
    }

    [Theory]
    [InlineData("AgentDefinition.FromJson(json);")]
    [InlineData("AgentDefinition.FromYaml(yaml);")]
    [InlineData("AgentManifest.FromJson(json);")]
    [InlineData("AgentManifest.FromYaml(yaml);")]
    [InlineData("McpTool.FromJson(json);")]
    [InlineData("Tool.FromJson(json);")]
    [InlineData("new TrackingLoadContext();")]
    [InlineData("new LoadContext();")]
    public void Inventory_ProductionBypass_AllOriginalPolicyPatternsRemainEnforced(string source)
    {
        this.Write("Production.csproj", "<Project />");
        this.Write("Production.cs", source);
        this.Write("PhantomAgentSchema.cs", source);
        var inventory = new RepositorySourceInventory(this.root);

        Assert.Contains("Production.cs", Assert.Single(PhantomAgentSchemaCentralizationTests.FindViolations(inventory)));
    }

    [Fact]
    public void Inventory_UnownedSourceAndMixedProjectDirectory_DoNotReceiveTestExemption()
    {
        this.Write("Loose.cs", "AgentDefinition.FromJson(json);");
        this.Write(@"mixed\Production.csproj", "<Project />");
        this.Write(@"mixed\Z.Tests.csproj", "<Project />");
        this.Write(@"mixed\Shared.cs", "AgentDefinition.FromJson(json);");

        var inventory = new RepositorySourceInventory(this.root);

        Assert.False(inventory.IsTestSource(Path.Combine(this.root, "Loose.cs")));
        Assert.False(inventory.IsTestSource(Path.Combine(this.root, @"mixed\Shared.cs")));
        Assert.Equal(2, PhantomAgentSchemaCentralizationTests.FindViolations(inventory).Count);
    }

    [Theory]
    [InlineData(".Tests")]
    [InlineData(".Test")]
    [InlineData(".WebViewTests")]
    [InlineData(".IntegrationTests")]
    public void Inventory_ConventionalTestProjectNames_PreserveOriginalPolicyExemptions(string suffix)
    {
        this.Write(@"src\nested\First" + suffix + ".csproj", "<Project />");
        this.Write(@"src\nested\Second" + suffix + ".csproj", "<Project />");
        this.Write(@"src\nested\Test.cs", "AgentDefinition.FromJson(json);");

        var inventory = new RepositorySourceInventory(this.root);

        Assert.True(inventory.IsTestSource(Path.Combine(this.root, @"src\nested\Test.cs")));
        Assert.Empty(PhantomAgentSchemaCentralizationTests.FindViolations(inventory));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(this.root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
