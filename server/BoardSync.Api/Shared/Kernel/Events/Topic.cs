namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// The channels a real-time client subscribes to.
/// </summary>
/// <remarks>
/// <para>
/// A topic names <em>what a client is looking at</em>, not what kind of event occurred. That is why
/// they are scope identities — an org, a project, a team, a sprint, one user — rather than event
/// names: a client watching a board subscribes once and receives everything relevant to it.
/// </para>
/// <para>
/// There is deliberately no <c>board:</c> topic. A board is one-to-one with its project, so a board
/// topic would always have exactly the same subscribers as the project topic and would only add a
/// second thing to keep in sync. Board changes travel on <see cref="Project"/>.
/// </para>
/// </remarks>
public static class Topic
{
    /// <summary>One user's private channel — notifications, assignment to them, forced re-auth.</summary>
    public static string User(Guid userId) => $"user:{userId}";

    /// <summary>Everything happening in an organization, including its activity feed.</summary>
    public static string Organization(Guid organizationId) => $"org:{organizationId}";

    /// <summary>Work items, comments and board changes inside one project.</summary>
    public static string Project(Guid projectId) => $"project:{projectId}";

    /// <summary>Team membership and the team's sprints.</summary>
    public static string Team(Guid teamId) => $"team:{teamId}";

    /// <summary>One sprint's backlog and status.</summary>
    public static string Sprint(Guid sprintId) => $"sprint:{sprintId}";

    /// <summary>
    /// Splits a topic string into its kind and id, or returns false if it is not a well-formed
    /// topic. Callers get this straight from a client, so a malformed value is a bad request rather
    /// than an exception.
    /// </summary>
    public static bool TryParse(string? topic, out TopicKind kind, out Guid id)
    {
        kind = default;
        id = default;

        if (string.IsNullOrWhiteSpace(topic)) return false;

        var separator = topic.IndexOf(':');
        if (separator <= 0 || separator == topic.Length - 1) return false;

        if (!Guid.TryParse(topic.AsSpan(separator + 1), out id)) return false;

        kind = topic.AsSpan(0, separator) switch
        {
            "user" => TopicKind.User,
            "org" => TopicKind.Organization,
            "project" => TopicKind.Project,
            "team" => TopicKind.Team,
            "sprint" => TopicKind.Sprint,
            _ => TopicKind.Unknown
        };

        return kind != TopicKind.Unknown;
    }
}

/// <summary>What a topic is scoped to. Determines how a subscription is authorized.</summary>
public enum TopicKind
{
    Unknown = 0,
    User,
    Organization,
    Project,
    Team,
    Sprint
}
