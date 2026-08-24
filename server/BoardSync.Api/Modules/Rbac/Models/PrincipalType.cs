using System.Text.Json.Serialization;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// What kind of thing holds a grant or made a change.
/// </summary>
/// <remarks>
/// <para>
/// Every actor used to be assumed to be a person, which stops being true the moment a webhook worker
/// moves a card. There were two wrong ways to model that, and both were considered:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Act as the commit author.</b> Requires resolving a git email to a BoardSync user, which
///     fails for external contributors and bots — and worse, the integration would inherit whatever
///     that person can do. A merge authored by a Tester could then close the item, defeating the QA
///     gate entirely.
///   </description></item>
///   <item><description>
///     <b>Act as a superuser and skip the checks.</b> Every automated transition becomes
///     unauditable, and the gate degrades to an <c>if</c> statement someone will eventually delete.
///   </description></item>
/// </list>
/// <para>
/// So an integration is a principal in its own right, holding a grant of its own. The consequence is
/// the design's best property: <b>the QA gate is not a rule the git worker follows, it is a
/// permission the git worker does not have.</b> A bug in a webhook handler, a spoofed payload, or a
/// future contributor "simplifying" the transition logic cannot close a work item, because the same
/// evaluator that guards every endpoint denies it.
/// </para>
/// <para>
/// <b>Authority and attribution are separate.</b> What an integration may do comes from its grant;
/// who it says did the work is metadata carried alongside — see
/// <c>WorkItemHistory.AttributedToUserId</c>.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrincipalType
{
    /// <summary>A person. The default, so every row written before this existed reads correctly.</summary>
    User = 0,

    /// <summary>A connected git provider installation, acting on webhook events.</summary>
    Integration = 1
}
