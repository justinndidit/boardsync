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
        You are writing the sprint section of a status report that goes to management. It must read
        as an account of what happened, not as a list of numbers.

        You are given the sprint's figures and the work items themselves. Name the work: a reader
        wants to know what the team built, not only how many points it was.

        Only name items that appear in the lists you were given, by their reference and title. Do
        not invent, merge, rename, or infer work items. If the lists are empty, say so plainly.

        outcome: one or two sentences on what the sprint set out to do and whether it did it. Use the
        goal if there is one.

        shipped: one line per delivered item worth reporting — reference, then what it does in
        plain words. Group trivia rather than listing it. Empty if nothing landed.

        didNotLand: one line per item that did not finish, saying where it stopped. Do not speculate
        about why; you were not told.

        whereWorkIsSitting: work finished and waiting to be tested is a QA queue, not slow
        development; items never started are committed work nobody picked up. These have different
        owners, so say which it is. Empty when neither applies.

        You are given a sprint report as JSON. Every number you write MUST appear in it.
        Do not calculate, estimate, extrapolate, or compare against anything not present
        — no trends, no "up from last sprint", no percentages you worked out yourself.
        If something interesting would require a figure you were not given, leave it out.

        Prefer saying less. A sprint where nothing stands out should get an empty
        observations list rather than filler, and a section with nothing in it should be empty
        rather than padded.

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

        Then order the work into delivery phases, and put every leaf item — every Task, Bug, and any
        User Story with no Tasks under it — into one of them with `phase`, counting from 1.

        Phase by what has to be true before something else can start. Schema before the endpoints
        that read it; authentication before the screens behind it; a thing before the thing that
        reports on it. Where nothing forces an order, put the work that makes the product usable
        first, and hardening, polish and edge cases last. Give each phase a short name and say in
        `rationale` what makes it a phase — "the API cannot be built until this exists" is useful,
        "the first phase of work" is not.

        Between two and six phases. A single phase is fine for a document small enough to build in
        one go, and if you find yourself writing more than six you are listing items, not phasing
        them.

        **Do not say how long any of this will take.** Not in a phase name, not in a rationale, not
        anywhere. You do not know this team's throughput; it is measured from the sprints they have
        already finished, and the schedule is worked out from that measurement and your ordering. A
        duration from you would be a number nobody can check, presented to people who would plan
        against it.
        """;
}
