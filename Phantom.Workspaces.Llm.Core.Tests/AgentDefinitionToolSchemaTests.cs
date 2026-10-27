using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentDefinitionToolSchemaTests
{
    public static IEnumerable<object[]> BuiltinKinds =>
        new[] { "web_search", "web_request", "web", "filesystem", "workspace-entity", "workspace-gui", "current-session", "agent-session" }
            .Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(BuiltinKinds))]
    public void AgentDefinitionJsonSchema_BuiltinKinds_ValidatesSupportedConfigurations(string kind)
    {
        Assert.True(EvaluateTool($$"""{"kind":"{{kind}}","name":"arbitrary-label"}"""));
        Assert.False(EvaluateTool($$"""{"kind":"{{kind}}","options":false}"""));
    }

    [Theory]
    [MemberData(nameof(BuiltinKinds))]
    public void AgentDefinitionLoader_BuiltinMinimalSelections_RoundTripsSupportedRepresentation(string kind)
    {
        var original = AgentDefinitionLoader.LoadAgentFromJson(Definition($$"""{"kind":"{{kind}}"}"""));
        var reloaded = Assert.IsType<AgentSchema.PromptAgent>(
            AgentDefinitionLoader.LoadAgentFromJson(original.ToJson()));
        Assert.Equal(kind, Assert.Single(reloaded.Tools!).Kind);
    }

    [Theory]
    [InlineData("""{"kind":"vendor-toolset","name":"filesystem","options":{"connection":"vendor"},"extension":42}""", true)]
    [InlineData("""{"kind":"vendor-toolset","name":"web","options":{"vendor-setting":true}}""", true)]
    [InlineData("""{"kind":"Filesystem","connection":{"vendor":true}}""", true)]
    [InlineData("""{"description":"No kind supplied"}""", true)]
    [InlineData("""{"kind":"filesystem","name":"vendor-label","connection":{"provider":"mongodb"}}""", false)]
    [InlineData("""{"kind":"filesystem","options":{"connection":{}}}""", false)]
    [InlineData("""{"kind":"agent-definition","name":"helper"}""", false)]
    [InlineData("""{"kind":"mcp","serverName":"server","connection":{}}""", false)]
    [InlineData("""{"kind":"github-cli-builtin-tools","client-mode":"unknown"}""", false)]
    [InlineData("""{"kind":"chat-history","options":{"connection":{}}}""", false)]
    [InlineData("""{"kind":"vendor","name":"chat-history","options":{"connection":{}}}""", false)]
    [InlineData("""{"kind":"filesystem","name":"chat-history","connection":{}}""", false)]
    [InlineData("""{"kind":"vendor","name":"Chat-History","options":{"connection":{}}}""", true)]
    public void AgentDefinitionJsonSchema_ActualSelectionDiscriminators_EnforceOnlySelectedContract(
        string tool,
        bool expected)
        => Assert.Equal(expected, EvaluateTool(tool));

    [Theory]
    [InlineData("container", 1)]
    [InlineData("container", 65535)]
    [InlineData("external", 27017)]
    public void AgentDefinitionLoader_FilesystemConnection_RoundTripsProviderConfiguration(string provider, int port)
    {
        var connection = ProviderConnection(provider, port);
        var definition = AgentDefinitionLoader.LoadAgentFromJson(Definition(
            $$"""{"kind":"filesystem","name":"persistent-files","connection":{{connection}}}"""));
        var saved = definition.ToJson();
        Assert.True(Evaluate(ParseElement(saved)).IsValid, saved);
        var reloaded = Assert.IsType<AgentSchema.PromptAgent>(AgentDefinitionLoader.LoadAgentFromJson(saved));
        var tool = Assert.IsType<FilesystemTool>(Assert.Single(reloaded.Tools!));
        var extracted = ToolsetFactory.GetFilesystemConnectionJson(tool);
        Assert.NotNull(extracted);
        Assert.True(JsonElement.DeepEquals(ParseElement(connection), ParseElement(extracted)));
        Assert.IsType<MongoDbChatHistoryProviderDefinition>(ChatHistoryProviderDefinition.FromJson(extracted));
    }

    [Theory]
    [InlineData("""{"provider":"mongodb","mongoProvider":"external","database-name":"","collection-name":"edits","connection-string":"mongodb://localhost"}""")]
    [InlineData("""{"provider":"mongodb","mongoProvider":"external","database-name":"db","collection-name":"edits"}""")]
    [InlineData("""{"provider":"mongodb","mongoProvider":"container","database-name":"db","collection-name":"edits","container-name":"container"}""")]
    [InlineData("""{"provider":"mongodb","mongoProvider":"container","database-name":"db","collection-name":"edits","container-name":"container","data-directory":"data","host-port":0}""")]
    [InlineData("""{"provider":"mongodb","mongoProvider":"container","database-name":"db","collection-name":"edits","container-name":"container","data-directory":"data","host-port":65536}""")]
    [InlineData("""{"provider":"mongodb","mongoProvider":"container","database-name":"db","collection-name":"edits","container-name":"container","data-directory":"data","host-port":1.5}""")]
    public void AgentDefinitionJsonSchema_FilesystemConnection_RejectsMalformedSharedProvider(string connection)
        => Assert.False(EvaluateTool($$"""{"kind":"filesystem","connection":{{connection}}}"""));

    [Theory]
    [InlineData("chat-history", "history")]
    [InlineData("vendor", "chat-history")]
    public void AgentDefinitionLoader_ChatHistoryOptionsConnection_RoundTripsProviderConfiguration(string kind, string name)
    {
        var connection = ProviderConnection("external", 27017);
        var definition = AgentDefinitionLoader.LoadAgentFromJson(Definition(
            JsonSerializer.Serialize(new { kind, name, options = new { connection = ParseElement(connection) } })));
        var reloaded = Assert.IsType<AgentSchema.PromptAgent>(
            AgentDefinitionLoader.LoadAgentFromJson(definition.ToJson()));
        var tool = Assert.IsAssignableFrom<AgentSchema.CustomTool>(Assert.Single(reloaded.Tools!));
        var optionsConnection = JsonSerializer.Serialize(tool.Options["connection"]);
        Assert.True(JsonElement.DeepEquals(ParseElement(connection), ParseElement(optionsConnection)));
    }

    [Fact]
    public void PhantomAgentSchema_ManifestTemplateFilesystemConnection_RoundTripsWithoutChangingMcp()
    {
        var connection = ProviderConnection("external", 27017);
        var templateJson = Definition(
            $$"""{"kind":"filesystem","name":"files","connection":{{connection}}},""" +
            """{"kind":"mcp","serverName":"server","connection":{"kind":"Anonymous","endpoint":"https://example.com/mcp"}}""");
        var manifest = PhantomAgentSchema.AgentManifestFromJson(JsonSerializer.Serialize(
            new { name = "manifest", displayName = "Manifest", template = ParseElement(templateJson) }));
        var reloaded = PhantomAgentSchema.AgentManifestFromJson(manifest.ToJson());
        var template = Assert.IsType<AgentSchema.PromptAgent>(reloaded.Template);
        Assert.IsType<FilesystemTool>(template.Tools![0]);
        Assert.IsType<PhantomMcpTool>(template.Tools[1]);
        Assert.True(JsonElement.DeepEquals(ParseElement(connection),
            ParseElement(ToolsetFactory.GetFilesystemConnectionJson(template.Tools[0])!)));
    }

    [Fact]
    public void AgentDefinitionLoader_GithubPolicy_RoundTripsAlongsideFilesystem()
    {
        var json = Definition("""
            {"kind":"filesystem","name":"files"},
            {"kind":"github-cli-builtin-tools","name":"policy","client-mode":"empty",
             "available-tools":{"tools":["builtin:read_file","custom:vendor","mcp:*"]},
             "excluded-tools":{"isolated":true}}
            """);
        var original = AgentDefinitionLoader.LoadAgentFromJson(json);
        var saved = original.ToJson();
        var reloaded = Assert.IsType<AgentSchema.PromptAgent>(AgentDefinitionLoader.LoadAgentFromJson(saved));
        var policy = Assert.IsType<GitHubCliBuiltinToolsTool>(reloaded.Tools![1]);
        Assert.Equal(GitHub.Copilot.CopilotClientMode.Empty, policy.ClientMode);
        Assert.Equal(new[] { "builtin:read_file", "custom:vendor", "mcp:*" }, policy.AvailableTools!.Tools);
        Assert.True(policy.ExcludedTools!.Isolated);
    }

    [Fact]
    public void ToolsetFactory_FilesystemConfiguration_DoesNotSilentlyConsumeLegacyOptionsOrSdkConnections()
    {
        Assert.Null(ToolsetFactory.GetFilesystemConnectionJson(new AgentSchema.CustomTool { Kind = "filesystem" }));
        Assert.Throws<InvalidOperationException>(() => ToolsetFactory.GetFilesystemConnectionJson(
            new AgentSchema.CustomTool
            {
                Kind = "filesystem",
                Options = new Dictionary<string, object> { ["connection"] = new { provider = "mongodb" } },
            }));
        Assert.Throws<InvalidOperationException>(() => ToolsetFactory.GetFilesystemConnectionJson(
            new AgentSchema.CustomTool { Kind = "filesystem", Connection = new AgentSchema.AnonymousConnection() }));
    }

    [Fact]
    public void AgentDefinitionLoader_FilesystemExtensionProperty_DoesNotBypassProviderContract()
    {
        var agent = Assert.IsType<AgentSchema.PromptAgent>(AgentDefinitionLoader.LoadAgentFromJson(Definition(
            """{"kind":"filesystem","__phantom-filesystem-provider":{"provider":"unvalidated"}}""")));
        Assert.Null(ToolsetFactory.GetFilesystemConnectionJson(Assert.Single(agent.Tools!)));
        Assert.Equal("filesystem", new FilesystemTool().Kind);
    }

    [Theory]
    [InlineData("""{"files":{"kind":"filesystem","connection":{}}}""", false)]
    [InlineData("""{"chat-history":{"kind":"vendor","options":{"connection":{}}}}""", false)]
    [InlineData("""{"filesystem":{"kind":"vendor","options":{"connection":"vendor"}}}""", true)]
    [InlineData("""{"helper":{"kind":"agent-definition"}}""", false)]
    [InlineData("""{"helper":{"kind":"agent-definition","description":"Dispatcher"}}""", true)]
    public void AgentDefinitionJsonSchema_SdkMappedTools_EnforcesSameSelectionContracts(string tools, bool expected)
    {
        var definition = System.Text.Json.Nodes.JsonNode.Parse(Definition(""))!.AsObject();
        definition["tools"] = System.Text.Json.Nodes.JsonNode.Parse(tools);
        Assert.Equal(expected, Evaluate(ParseElement(definition.ToJsonString())).IsValid);
    }

    [Fact]
    public void AgentDefinitionLoader_FilesystemYamlConnection_RoundTripsProviderConfiguration()
    {
        var definition = Assert.IsType<AgentSchema.PromptAgent>(AgentDefinitionLoader.LoadAgentFromYaml("""
            kind: prompt
            name: files
            model:
              id: echo
              provider: echo
            tools:
              edits:
                kind: filesystem
                connection:
                  provider: mongodb
                  mongoProvider: external
                  database-name: test-database
                  collection-name: edits
                  connection-string: mongodb://localhost
            """));
        var connection = ToolsetFactory.GetFilesystemConnectionJson(Assert.Single(definition.Tools!));
        Assert.IsType<MongoDbChatHistoryProviderDefinition>(ChatHistoryProviderDefinition.FromJson(connection!));
    }

    [Fact]
    public void PhantomAgentSchema_ManifestYamlTools_UsesSameProviderBridge()
    {
        var manifest = PhantomAgentSchema.AgentManifestFromYaml("""
            name: files
            displayName: Files
            template:
              kind: prompt
              name: files
              model:
                id: echo
                provider: echo
              tools:
                - kind: filesystem
                  name: files
            """);
        var template = Assert.IsType<AgentSchema.PromptAgent>(manifest.Template);
        Assert.IsType<FilesystemTool>(Assert.Single(template.Tools!));
    }

    [Fact]
    public void AgentDefinitionLoader_MalformedKnownYamlConfiguration_FailsValidation()
        => Assert.Throws<InvalidOperationException>(() => AgentDefinitionLoader.LoadAgentFromYaml("""
            kind: prompt
            name: files
            model:
              id: echo
            tools:
              - kind: filesystem
                connection:
                  provider: mongodb
            """));

    private static bool EvaluateTool(string tool) => Evaluate(ParseElement(Definition(tool))).IsValid;

    private static string Definition(string tools)
        => $$"""{"kind":"prompt","name":"test","model":{"id":"echo","provider":"echo"},"tools":[{{tools}}]}""";

    private static string ProviderConnection(string provider, int port)
        => $$"""
        {"provider":"mongodb","mongoProvider":"{{provider}}","database-name":"test-database","collection-name":"edits",
         "container-name":"test-container","data-directory":"data","host-port":{{port}},"connection-string":"mongodb://localhost"}
        """;

    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsInlineAgentDefinitionTool()
    {
        var instance = ParseElement("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": {
            "id": "echo",
            "provider": "echo"
          },
          "tools": [
            {
              "kind": "agent-definition",
              "name": "default",
              "description": "The default sub-agent",
              "definition": {
                "kind": "prompt",
                "name": "sub",
                "model": { "id": "echo", "provider": "echo" }
              }
            }
          ]
        }
        """);

        var result = Evaluate(instance);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AgentDefinitionJsonSchema_AcceptsManifestReferenceAgentDefinitionTool()
    {
        var instance = ParseElement("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": {
            "id": "echo",
            "provider": "echo"
          },
          "tools": [
            {
              "kind": "agent-definition",
              "name": "helper",
              "description": "A referenced sub-agent",
              "manifest-reference": ["agent-manifests", "helper"]
            }
          ]
        }
        """);

        var result = Evaluate(instance);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ModelOptionsExecutor_RoundTrips_InEmbeddedAgentDefinition()
    {
        // Issue #1443: the model executor binding is carried in ModelOptions.AdditionalProperties, so it
        // must survive a Save/Load round-trip of the embedded agent definition without any schema change.
        // AgentSchema persists the open bag under an explicit nested `additionalProperties` object.
        var json = """
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": {
            "id": "gpt-5",
            "provider": "copilot",
            "options": { "additionalProperties": { "executor": "model-host" } }
          }
        }
        """;

        var definition = Phantom.Workspaces.Llm.PhantomAgentSchema.AgentDefinitionFromJson(json);
        var roundTripped = Phantom.Workspaces.Llm.PhantomAgentSchema.AgentDefinitionFromJson(definition.ToJson());

        var prompt = Assert.IsType<AgentSchema.PromptAgent>(roundTripped);
        Assert.NotNull(prompt.Model?.Options?.AdditionalProperties);
        Assert.True(prompt.Model!.Options!.AdditionalProperties!.TryGetValue("executor", out var executor));
        Assert.Equal("model-host", executor?.ToString());
    }

    private static EvaluationResults Evaluate(JsonElement instance)
    {
        return AgentDefinitionJsonSchema.Value.Evaluate(
            instance,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
