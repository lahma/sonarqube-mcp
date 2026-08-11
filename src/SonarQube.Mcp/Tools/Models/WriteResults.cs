namespace SonarQube.Mcp.Tools.Models;

/// <summary>
/// An issue after a transition was applied.
/// </summary>
/// <remarks>
/// <see cref="AvailableTransitions"/> is the reason this result is structured rather than a
/// confirmation string: it says what can be done to the issue <em>next</em>, which is exactly what a
/// caller that just changed its state needs and would otherwise have to fetch again.
/// </remarks>
internal sealed record IssueTransitionResult
{
    /// <summary>The issue key.</summary>
    public string? Key { get; init; }

    /// <summary>The status now: <c>OPEN</c>, <c>CONFIRMED</c>, <c>FALSE_POSITIVE</c>, <c>ACCEPTED</c> or <c>FIXED</c>.</summary>
    public string? IssueStatus { get; init; }

    /// <summary>The legacy status now, which SonarQube still reports alongside it.</summary>
    public string? Status { get; init; }

    /// <summary>The legacy resolution, for a resolved issue.</summary>
    public string? Resolution { get; init; }

    /// <summary>What the rule flagged — repeated so the result is readable without the original issue.</summary>
    public string? Message { get; init; }

    /// <summary>The project-relative file path.</summary>
    public string? File { get; init; }

    /// <summary>The line.</summary>
    public int? Line { get; init; }

    /// <summary>The transitions that are legal from the issue's new state.</summary>
    public IReadOnlyList<string> AvailableTransitions { get; init; } = [];

    /// <summary>The issue's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>An issue after its assignee changed.</summary>
internal sealed record IssueAssignResult
{
    /// <summary>The issue key.</summary>
    public string? Key { get; init; }

    /// <summary>The assignee's login now, or absent when the issue is unassigned.</summary>
    public string? Assignee { get; init; }

    /// <summary>What the rule flagged.</summary>
    public string? Message { get; init; }

    /// <summary>The project-relative file path.</summary>
    public string? File { get; init; }

    /// <summary>The line.</summary>
    public int? Line { get; init; }

    /// <summary>The issue's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>A comment that was just posted to an issue.</summary>
internal sealed record IssueCommentResult
{
    /// <summary>The issue key.</summary>
    public string? Key { get; init; }

    /// <summary>The new comment's key — the newest entry of the issue's comment list after the call.</summary>
    public string? CommentKey { get; init; }

    /// <summary>The comment text as SonarQube stored it.</summary>
    public string? Text { get; init; }

    /// <summary>When it was posted.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>What the rule flagged on the commented issue.</summary>
    public string? Message { get; init; }

    /// <summary>The project-relative file path.</summary>
    public string? File { get; init; }

    /// <summary>The issue's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// A security hotspot's state after its status was changed.
/// </summary>
/// <remarks>
/// <c>hotspots/change_status</c> answers <c>204</c> with no body, so this is read back from
/// <c>hotspots/show</c>: the returned state is what SonarQube actually stored, not an echo of what
/// was asked for.
/// </remarks>
internal sealed record HotspotStatusResult
{
    /// <summary>The hotspot key.</summary>
    public string? Key { get; init; }

    /// <summary><c>TO_REVIEW</c> or <c>REVIEWED</c>, as stored.</summary>
    public string? Status { get; init; }

    /// <summary><c>FIXED</c> or <c>SAFE</c>, as stored.</summary>
    public string? Resolution { get; init; }

    /// <summary>What the rule flagged.</summary>
    public string? Message { get; init; }

    /// <summary>The project-relative file path.</summary>
    public string? File { get; init; }

    /// <summary>The line.</summary>
    public int? Line { get; init; }

    /// <summary>The hotspot's page in SonarQube.</summary>
    public string? Url { get; init; }
}
