using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Applies the <c>not-interesting</c> exclusion to a query at the query level: every top-level clause
/// is wrapped as <c>And(clause, Not(participation in a not-interesting relationship as target))</c>, so
/// the data-access layer excludes not-interesting targets via a join (see the participation query
/// contract). This replaces any client-side post-filtering.
/// </summary>
public static class NotInterestingQuery
{
    /// <summary>The interest (relationship) type whose targets are hidden unless explicitly shown.</summary>
    public const string NotInterestingRelationshipType = "not-interesting";

    /// <summary>Returns the query with each clause excluding not-interesting targets via a join.</summary>
    public static QueryRequest ExcludingNotInteresting(QueryRequest request)
    {
        return request with
        {
            Clauses = request.Clauses.Select(WrapClause).ToArray(),
        };
    }

    private static TopLevelQueryClause WrapClause(TopLevelQueryClause topLevelClause)
    {
        return topLevelClause with
        {
            Clause = new AndQueryClause
            {
                Clauses =
                [
                    topLevelClause.Clause,
                    new NotQueryClause { Clause = NotInterestingTargetClause() },
                ],
            },
        };
    }

    private static EntityParticipationQueryClause NotInterestingTargetClause()
        => new()
        {
            RelationshipTypeNames = new RelationshipTypeNameSet([NotInterestingRelationshipType]),
            ParticipationRoleNames = new RoleNameSet(["target"]),
        };
}
