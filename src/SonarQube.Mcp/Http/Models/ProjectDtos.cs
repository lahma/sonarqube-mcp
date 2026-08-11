using System.Text.Json.Serialization;

using SonarQube.Mcp.Json;

namespace SonarQube.Mcp.Http.Models;

/// <summary>
/// <c>api/components/search</c> — the list-projects endpoint.
/// </summary>
/// <remarks>
/// <c>api/projects/search</c> would be the obvious choice and is the wrong one: it requires
/// organization-administration rights and answers <c>401</c> to everyone else (verified live
/// 2026-08-11), while this one lists a public organization's projects anonymously.
/// </remarks>
internal sealed record ComponentsSearchResponseDto : PagedEnvelopeDto
{
    /// <summary>The matching components.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentDto>? Components { get; init; }
}

/// <summary><c>api/components/tree</c> — walks a project's directories and files.</summary>
internal sealed record ComponentsTreeResponseDto : PagedEnvelopeDto
{
    /// <summary>The subtree root the walk started from.</summary>
    [JsonPropertyName("baseComponent")]
    public ComponentDto? BaseComponent { get; init; }

    /// <summary>The descendants selected by <c>strategy</c> and <c>qualifiers</c>.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentDto>? Components { get; init; }
}

/// <summary><c>api/qualitygates/project_status</c>.</summary>
internal sealed record ProjectStatusResponseDto
{
    /// <summary>The gate's verdict for the requested scope.</summary>
    [JsonPropertyName("projectStatus")]
    public ProjectStatusDto? ProjectStatus { get; init; }
}

/// <summary>
/// A quality gate's verdict.
/// </summary>
/// <remarks>
/// <c>{"status":"NONE","conditions":[],"periods":[]}</c> is the <b>normal</b> answer for a project
/// or pull request that has not been analysed against a gate, not an error (verified live
/// 2026-08-11). The tool layer says so explicitly rather than letting "NONE" read as a failure.
/// </remarks>
internal sealed record ProjectStatusDto
{
    /// <summary><c>OK</c>, <c>ERROR</c> or <c>NONE</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Every condition the gate evaluated, passing and failing alike.</summary>
    [JsonPropertyName("conditions")]
    public IReadOnlyList<QualityGateConditionDto>? Conditions { get; init; }

    /// <summary>The new-code periods the conditions were evaluated against.</summary>
    [JsonPropertyName("periods")]
    public IReadOnlyList<MeasurePeriodDto>? Periods { get; init; }

    /// <summary>Whether some conditions were skipped because their metric had no value.</summary>
    [JsonPropertyName("ignoredConditions")]
    public bool? IgnoredConditions { get; init; }

    /// <summary>Whether the gate is "Clean as You Code"-compliant.</summary>
    [JsonPropertyName("caycStatus")]
    public string? CaycStatus { get; init; }
}

/// <summary>
/// One quality gate condition.
/// </summary>
/// <remarks>
/// The field names here (<c>comparator</c>, <c>errorThreshold</c>) are <em>not</em> the ones
/// <c>qualitygates/list</c> uses for the same concepts (<c>op</c>, <c>error</c>). Nothing here calls
/// that endpoint, but a future tool that does must not reuse this DTO.
/// </remarks>
internal sealed record QualityGateConditionDto
{
    /// <summary><c>OK</c> or <c>ERROR</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>The metric being tested, for example <c>new_coverage</c>.</summary>
    [JsonPropertyName("metricKey")]
    public string? MetricKey { get; init; }

    /// <summary><c>GT</c>, <c>LT</c>, <c>EQ</c> or <c>NE</c>.</summary>
    [JsonPropertyName("comparator")]
    public string? Comparator { get; init; }

    /// <summary>
    /// <c>1</c> when the condition applies to the new-code period. This is the only signal that
    /// separates an "on new code" condition from an overall one.
    /// </summary>
    [JsonPropertyName("periodIndex")]
    public int? PeriodIndex { get; init; }

    /// <summary>The threshold the condition fails at, as text.</summary>
    [JsonPropertyName("errorThreshold")]
    public string? ErrorThreshold { get; init; }

    /// <summary>The measured value, as text.</summary>
    [JsonPropertyName("actualValue")]
    public string? ActualValue { get; init; }
}

/// <summary><c>api/project_branches/list</c> — not paginated.</summary>
internal sealed record BranchesListResponseDto
{
    /// <summary>Every analysed branch.</summary>
    [JsonPropertyName("branches")]
    public IReadOnlyList<BranchDto>? Branches { get; init; }
}

/// <summary>One analysed branch.</summary>
internal sealed record BranchDto
{
    /// <summary>The branch name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Whether this is the project's main branch.</summary>
    [JsonPropertyName("isMain")]
    public bool? IsMain { get; init; }

    /// <summary><c>LONG</c> or <c>SHORT</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Issue counts and, on a non-main branch, the gate status.</summary>
    [JsonPropertyName("status")]
    public BranchStatusDto? Status { get; init; }

    /// <summary>When the branch was last analysed.</summary>
    [JsonPropertyName("analysisDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? AnalysisDate { get; init; }

    /// <summary>The commit that analysis ran on.</summary>
    [JsonPropertyName("commit")]
    public CommitDto? Commit { get; init; }

    /// <summary>SonarQube's internal branch identifier.</summary>
    [JsonPropertyName("branchId")]
    public string? BranchId { get; init; }

    /// <summary>Whether the branch is kept when inactive branches are purged.</summary>
    [JsonPropertyName("excludedFromPurge")]
    public bool? ExcludedFromPurge { get; init; }
}

/// <summary>
/// A branch's headline numbers.
/// </summary>
/// <remarks>
/// Every field is nullable because the main branch's status carries the three counts and <b>no</b>
/// <c>qualityGateStatus</c>, while a pull request's carries all four (verified live 2026-08-11).
/// Kept separate from <see cref="PullRequestStatusDto"/> for that reason: the shapes only look
/// identical.
/// </remarks>
internal sealed record BranchStatusDto
{
    /// <summary><c>OK</c> or <c>ERROR</c>; absent on the main branch.</summary>
    [JsonPropertyName("qualityGateStatus")]
    public string? QualityGateStatus { get; init; }

    /// <summary>Open bug count.</summary>
    [JsonPropertyName("bugs")]
    public int? Bugs { get; init; }

    /// <summary>Open vulnerability count.</summary>
    [JsonPropertyName("vulnerabilities")]
    public int? Vulnerabilities { get; init; }

    /// <summary>Open code smell count.</summary>
    [JsonPropertyName("codeSmells")]
    public int? CodeSmells { get; init; }
}

/// <summary>The commit a branch or pull request was analysed at.</summary>
internal sealed record CommitDto
{
    /// <summary>The full SHA.</summary>
    [JsonPropertyName("sha")]
    public string? Sha { get; init; }

    /// <summary>Who wrote it.</summary>
    [JsonPropertyName("author")]
    public CommitAuthorDto? Author { get; init; }

    /// <summary>When they wrote it.</summary>
    [JsonPropertyName("date")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? Date { get; init; }

    /// <summary>The commit message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>A commit's author.</summary>
internal sealed record CommitAuthorDto
{
    /// <summary>Their display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Their SonarQube login, when the SCM identity is linked to one.</summary>
    [JsonPropertyName("login")]
    public string? Login { get; init; }

    /// <summary>Gravatar-style avatar hash.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }
}

/// <summary><c>api/project_pull_requests/list</c> — not paginated.</summary>
internal sealed record PullRequestsListResponseDto
{
    /// <summary>Every analysed pull request.</summary>
    [JsonPropertyName("pullRequests")]
    public IReadOnlyList<SonarPullRequestDto>? PullRequests { get; init; }
}

/// <summary>
/// One analysed pull request.
/// </summary>
/// <remarks>
/// Named <c>Sonar…</c> rather than <c>PullRequestDto</c> because it is SonarQube's view of a pull
/// request — the analysis, not the SCM object — and confusing the two is how a tool ends up
/// reporting a stale quality gate as the pull request's state.
/// </remarks>
internal sealed record SonarPullRequestDto
{
    /// <summary>
    /// The SCM pull request number, as a string. This is the value that goes back as the
    /// <c>pullRequest</c> parameter on every other endpoint.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The pull request title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The source branch.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    /// <summary>The target branch, as SonarQube recorded it at analysis time.</summary>
    [JsonPropertyName("base")]
    public string? Base { get; init; }

    /// <summary>The target branch again, under the name the newer analysers send.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>Gate status and issue counts for the pull request's new code.</summary>
    [JsonPropertyName("status")]
    public PullRequestStatusDto? Status { get; init; }

    /// <summary>When it was last analysed.</summary>
    [JsonPropertyName("analysisDate")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? AnalysisDate { get; init; }

    /// <summary>The pull request's URL in the SCM provider — the one link that is not derivable.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>The commit that analysis ran on.</summary>
    [JsonPropertyName("commit")]
    public CommitDto? Commit { get; init; }

    /// <summary>Everyone who committed to the pull request.</summary>
    [JsonPropertyName("contributors")]
    public IReadOnlyList<CommitAuthorDto>? Contributors { get; init; }
}

/// <summary>A pull request's gate status and issue counts.</summary>
internal sealed record PullRequestStatusDto
{
    /// <summary><c>OK</c> or <c>ERROR</c>.</summary>
    [JsonPropertyName("qualityGateStatus")]
    public string? QualityGateStatus { get; init; }

    /// <summary>New bug count.</summary>
    [JsonPropertyName("bugs")]
    public int? Bugs { get; init; }

    /// <summary>New vulnerability count.</summary>
    [JsonPropertyName("vulnerabilities")]
    public int? Vulnerabilities { get; init; }

    /// <summary>New code smell count.</summary>
    [JsonPropertyName("codeSmells")]
    public int? CodeSmells { get; init; }
}
