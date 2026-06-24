using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Exposes the <c>get_current_session</c> tool, which reports the running agent session's profile
/// context: the agent-session entity, the host's current user-computer-profile, the host's current
/// user, and the agent-definition the host is running. The profile and user come from the
/// host-provided <see cref="CurrentSessionContext"/> (the live host), not from the session entity,
/// so a session resumed on a different machine reports that host's profile and user.
/// </summary>
public sealed class CurrentSessionContextProvider : AIContextProvider
{
    private readonly string stateKey = $"current-session:{Guid.NewGuid():n}";
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly CurrentSessionContext currentSessionContext;

    public CurrentSessionContextProvider(
        IDataAccessLayer dataAccessLayer,
        CurrentSessionContext currentSessionContext)
        : base(null, null, null)
    {
        this.dataAccessLayer = dataAccessLayer;
        this.currentSessionContext = currentSessionContext;
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        return new ValueTask<AIContext>(new AIContext
        {
            Tools =
            [
                new GetCurrentSessionTool(this.dataAccessLayer, this.currentSessionContext),
            ],
        });
    }

    private sealed class GetCurrentSessionTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "include_profile": {
                  "type": "boolean",
                  "description": "Include the current user-computer-profile and user. Defaults to true."
                },
                "include_definition": {
                  "type": "boolean",
                  "description": "Include the agent-definition the session runs. Defaults to true."
                }
              },
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private static readonly EntityTypeNameSet AgentSessionEntityTypeNames = new(["agent-session"]);

        private readonly IDataAccessLayer dataAccessLayer;
        private readonly CurrentSessionContext currentSessionContext;

        public GetCurrentSessionTool(
            IDataAccessLayer dataAccessLayer,
            CurrentSessionContext currentSessionContext)
        {
            this.dataAccessLayer = dataAccessLayer;
            this.currentSessionContext = currentSessionContext;
        }

        public override string Name => "get_current_session";

        public override string Description =>
            "Return the current agent session's profile context: the agent-session, the current hosting user-computer-profile, the user, and the agent-definition.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var includeProfile = ReadBooleanFlag(arguments, "include_profile");
            var includeDefinition = ReadBooleanFlag(arguments, "include_definition");

            var agentSession = await this.ResolveAgentSessionAsync(cancellationToken);
            var userComputerProfile = includeProfile ? this.currentSessionContext.UserComputerProfile : null;
            var user = includeProfile ? this.currentSessionContext.User : null;
            var agentDefinition = includeDefinition
                ? await this.ResolveAgentDefinitionAsync(cancellationToken)
                : null;

            return JsonSerializer.SerializeToElement(
                new
                {
                    agent_session = ToSerializableEntity(agentSession),
                    user_computer_profile = ToSerializableEntity(userComputerProfile),
                    user = ToSerializableEntity(user),
                    agent_definition = ToSerializableEntity(agentDefinition),
                });
        }

        private async Task<EntitySnapshot?> ResolveAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(this.currentSessionContext.AgentSessionId))
            {
                return null;
            }

            var queryResult = await this.dataAccessLayer.QueryAsync(
                new QueryRequest
                {
                    Clauses =
                    [
                        new TopLevelQueryClause
                        {
                            ClauseIdentifier = new QueryClauseIdentifier("agent-session"),
                            Clause = new AndQueryClause
                            {
                                Clauses =
                                [
                                    new EntityTypeQueryClause { EntityTypeNames = AgentSessionEntityTypeNames },
                                    new EntityFieldQueryClause
                                    {
                                        FieldPath = new FieldPath("agent-session-id"),
                                        ComparisonOperator = FieldComparisonOperator.Equals,
                                        Value = JsonSerializer.SerializeToElement(this.currentSessionContext.AgentSessionId),
                                    },
                                ],
                            },
                        },
                    ],
                },
                cancellationToken);

            return queryResult.Batches
                .SelectMany(static batch => batch.Entities)
                .FirstOrDefault(static entity => entity.Data is not null);
        }

        private async Task<EntitySnapshot?> ResolveAgentDefinitionAsync(CancellationToken cancellationToken)
        {
            if (this.currentSessionContext.AgentDefinitionReference is not EntityName agentDefinitionReference)
            {
                return null;
            }

            var getResult = await this.dataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest { EntityName = agentDefinitionReference },
                    ],
                },
                cancellationToken);

            return getResult.Batches
                .SelectMany(static batch => batch.Entities)
                .FirstOrDefault(static entity => entity.Data is not null);
        }

        private static bool ReadBooleanFlag(AIFunctionArguments arguments, string name)
        {
            if (!arguments.TryGetValue(name, out var rawValue) || rawValue is null)
            {
                return true;
            }

            return rawValue switch
            {
                bool booleanValue => booleanValue,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                string stringValue when bool.TryParse(stringValue, out var parsedValue) => parsedValue,
                _ => true,
            };
        }

        private static object? ToSerializableEntity(EntitySnapshot? entity)
        {
            if (entity is null)
            {
                return null;
            }

            return new
            {
                entityId = entity.EntityId.Value,
                concurrencyTag = entity.ConcurrencyTag?.Value,
                modifiedTime = entity.ModifiedTime.DateTime,
                changeId = entity.ModifiedTime.ChangeId,
                data = entity.Data,
            };
        }
    }
}
