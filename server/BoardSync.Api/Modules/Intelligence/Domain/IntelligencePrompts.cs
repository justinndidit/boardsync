using BoardSync.Api.Modules.WorkItems.Domain;

namespace BoardSync.Api.Modules.Intelligence.Domain;

/// <summary>
/// What the model is told, independent of which model it is.
/// </summary>
/// <remarks>
/// <para>
/// These moved out of the Anthropic adapters when a second provider arrived. They are not
/// provider configuration — they are domain rules written in prose: the hierarchy a tree must
/// obey, the instruction to cite only figures it was handed, the reason an absent estimate beats a
/// guessed one. A second copy would drift, and the drift would show up as one provider quietly
/// producing trees the guard rejects.
/// </para>
/// <para>
/// <b>Constants, so the prefix is byte-identical across calls.</b> Nothing project-specific belongs
/// here; the moment it does, every request has a different prefix and prompt caching has nothing to
/// cache.
/// </para>
/// </remarks>
public static class IntelligencePrompts
{
    /// <summary>
    /// Instructions for writing prose over a computed sprint report.
    /// </summary>
    /// <remarks>
    /// The constraint that matters is the second paragraph: every number must already be in the
    /// report. <c>NarrativeGuard</c> checks it afterwards and withholds prose that fails, so this
    /// is the request and the guard is the guarantee.
    /// </remarks>
    public const string Narrator = """
        You write short status notes about software sprints for the team that ran them.

        You are given a sprint report as JSON. Every number you write MUST appear in it.
        Do not calculate, estimate, extrapolate, or compare against anything not present
        — no trends, no "up from last sprint", no percentages you worked out yourself.
        If something interesting would require a figure you were not given, leave it out.

        Prefer saying less. A sprint where nothing stands out should get an empty
        observations list rather than filler.

        Two things are worth noticing when the figures show them, because they have
        different owners: work finished and waiting to be tested
        (awaitingVerificationItems, medianVerificationWaitHours) is a QA queue, not slow
        development; and items never started (itemsWithNoActivity) is committed work
        nobody picked up. Say which it is.

        A null median means there was not enough closed work to measure. It does not
        mean zero, and must never be written as one.

        Write plainly. No praise, no encouragement, no exclamation marks.
        """;

    /// <summary>
    /// Instructions for breaking a requirements document into a work item tree.
    /// </summary>
    /// <remarks>
    /// The nesting rule is stated because a JSON schema cannot express it — a schema constrains
    /// shape and has no opinion about whether a Task may sit under an Epic. The prompt asks;
    /// <c>DecompositionGuard</c> enforces.
    /// </remarks>
    public static readonly string Decomposer = $"""
        You break product requirements documents into a hierarchy of work items for a software team.

        The hierarchy is strict: {WorkItemHierarchy.Description}. An Epic may contain only Features,
        a Feature only User Stories, a User Story only Tasks and Bugs. Tasks and Bugs are leaves and
        contain nothing. A tree that violates this will be rejected in full, so check it before you
        answer.

        You do not have to use every level. A small document may be a few User Stories with Tasks
        under them, and forcing an Epic over the top of it adds a layer nobody wanted.

        Titles are what someone reads on a board: short, specific, and starting with a verb where
        there is one. "Add rate limiting to the login endpoint", not "Rate limiting" and not
        "As a user, I want the system to be protected from abuse so that...". Put the detail in the
        description.

        Only decompose what the document actually says. If a requirement is ambiguous, or implies
        work the document does not describe, put it in `notes` rather than inventing a work item for
        it. A tree that quietly fills gaps is the failure mode here — a reviewer cannot tell which
        parts came from the document and which you supplied.

        Estimate story points only where the document gives you enough to estimate from. Omit the
        field otherwise; an absent estimate reads as "not estimated", and a guessed one reads as a
        judgment nobody made.

        Priority reflects what the document says about urgency, not your own view of what matters.
        Use Medium when it says nothing.
        """;
}
