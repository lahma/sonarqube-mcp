namespace SonarQube.Mcp.Tools.Models;

/// <summary>A project as a list entry: enough to choose one and call it by key.</summary>
internal sealed record ProjectSummary
{
    /// <summary>The project key — the value every other tool takes as <c>projectKey</c>.</summary>
    public string? Key { get; init; }

    /// <summary>The display name.</summary>
    public string? Name { get; init; }

    /// <summary><c>TRK</c> for a project. Present because the endpoint can return other kinds.</summary>
    public string? Qualifier { get; init; }

    /// <summary>The project's overview page — the link to hand a human.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of projects.</summary>
internal sealed record ProjectListResult
{
    /// <summary>The projects on this page, in SonarQube's order.</summary>
    public IReadOnlyList<ProjectSummary> Projects { get; init; } = [];

    /// <summary>The 1-based page this is.</summary>
    public int Page { get; init; }

    /// <summary>How many items a full page holds.</summary>
    public int PageSize { get; init; }

    /// <summary>Total matching projects, when SonarQube reported a count.</summary>
    public int? TotalCount { get; init; }

    /// <summary>Whether another page exists. Ask for <c>page + 1</c> when true.</summary>
    public bool HasMore { get; init; }

    /// <summary>Set only when there is something about this result the caller has to know.</summary>
    public string? Note { get; init; }
}

/// <summary>A file or directory inside a project.</summary>
internal sealed record ComponentSummary
{
    /// <summary>The component key — <c>project:path/from/root</c>, which other tools take as <c>component</c>.</summary>
    public string? Component { get; init; }

    /// <summary>The project-relative path, which is what a checkout has on disk.</summary>
    public string? Path { get; init; }

    /// <summary>The short name — the file name, for a file.</summary>
    public string? Name { get; init; }

    /// <summary><c>FIL</c> (file), <c>UTS</c> (test file), <c>DIR</c> (directory), <c>TRK</c> (project).</summary>
    public string? Qualifier { get; init; }

    /// <summary>The analysed language, for a file.</summary>
    public string? Language { get; init; }

    /// <summary>The component's page in SonarQube's code browser.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of components.</summary>
internal sealed record ComponentListResult
{
    /// <summary>The components on this page.</summary>
    public IReadOnlyList<ComponentSummary> Components { get; init; } = [];

    /// <summary>The 1-based page this is.</summary>
    public int Page { get; init; }

    /// <summary>How many items a full page holds.</summary>
    public int PageSize { get; init; }

    /// <summary>Total matching components, when SonarQube reported a count.</summary>
    public int? TotalCount { get; init; }

    /// <summary>Whether another page exists.</summary>
    public bool HasMore { get; init; }

    /// <summary>Set only when there is something about this result the caller has to know.</summary>
    public string? Note { get; init; }
}

/// <summary>An analysed branch, with the headline numbers SonarQube keeps per branch.</summary>
internal sealed record BranchSummary
{
    /// <summary>The branch name — pass it back as <c>branch</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Whether this is the project's main branch, which is what a call with no scope reads.</summary>
    public bool IsMain { get; init; }

    /// <summary><c>LONG</c> or <c>SHORT</c>.</summary>
    public string? Type { get; init; }

    /// <summary>When the branch was last analysed. A branch never analysed has none.</summary>
    public DateTimeOffset? AnalysisDate { get; init; }

    /// <summary>Open bug count at that analysis.</summary>
    public int? Bugs { get; init; }

    /// <summary>Open vulnerability count.</summary>
    public int? Vulnerabilities { get; init; }

    /// <summary>Open code smell count.</summary>
    public int? CodeSmells { get; init; }

    /// <summary>
    /// <c>OK</c> or <c>ERROR</c>. Absent on the main branch, where SonarQube does not report it here
    /// — call getQualityGateStatus for the main branch's verdict.
    /// </summary>
    public string? QualityGateStatus { get; init; }

    /// <summary>The commit that analysis ran on.</summary>
    public string? CommitSha { get; init; }

    /// <summary>That commit's message.</summary>
    public string? CommitMessage { get; init; }

    /// <summary>The branch's overview page.</summary>
    public string? Url { get; init; }
}

/// <summary>Every analysed branch. This endpoint is not paginated.</summary>
internal sealed record BranchListResult
{
    /// <summary>The branches, in SonarQube's order.</summary>
    public IReadOnlyList<BranchSummary> Branches { get; init; } = [];
}

/// <summary>A pull request as SonarQube analysed it — the analysis, not the SCM object.</summary>
internal sealed record PullRequestSummary
{
    /// <summary>
    /// The SCM pull request number as a string. <b>This is the value to pass back as
    /// <c>pullRequest</c></b> on every other tool.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>The pull request title.</summary>
    public string? Title { get; init; }

    /// <summary>The source branch.</summary>
    public string? Branch { get; init; }

    /// <summary>The target branch.</summary>
    public string? Base { get; init; }

    /// <summary><c>OK</c> or <c>ERROR</c> for the pull request's new code.</summary>
    public string? QualityGateStatus { get; init; }

    /// <summary>New bug count.</summary>
    public int? Bugs { get; init; }

    /// <summary>New vulnerability count.</summary>
    public int? Vulnerabilities { get; init; }

    /// <summary>New code smell count.</summary>
    public int? CodeSmells { get; init; }

    /// <summary>When it was last analysed.</summary>
    public DateTimeOffset? AnalysisDate { get; init; }

    /// <summary>The pull request's page in the SCM provider — the one link SonarQube itself supplies.</summary>
    public string? ScmUrl { get; init; }

    /// <summary>The pull request's overview page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>Every analysed pull request. This endpoint is not paginated.</summary>
internal sealed record PullRequestListResult
{
    /// <summary>The pull requests, in SonarQube's order.</summary>
    public IReadOnlyList<PullRequestSummary> PullRequests { get; init; } = [];
}

/// <summary>One condition a quality gate evaluated.</summary>
internal sealed record QualityGateCondition
{
    /// <summary>The metric being tested, for example <c>new_coverage</c>.</summary>
    public string? Metric { get; init; }

    /// <summary><c>OK</c> or <c>ERROR</c>.</summary>
    public string? Status { get; init; }

    /// <summary><c>GT</c>, <c>LT</c>, <c>EQ</c> or <c>NE</c> — how the value is compared to the threshold.</summary>
    public string? Comparator { get; init; }

    /// <summary>The value the condition fails at, as text.</summary>
    public string? Threshold { get; init; }

    /// <summary>The measured value, as text.</summary>
    public string? ActualValue { get; init; }

    /// <summary>
    /// Whether the condition applies to the new-code period rather than to the whole project. Most
    /// modern gates are entirely made of new-code conditions.
    /// </summary>
    public bool OnNewCode { get; init; }
}

/// <summary>A quality gate's verdict for one project, branch or pull request.</summary>
internal sealed record QualityGateResult
{
    /// <summary>The project the gate was evaluated for.</summary>
    public string? ProjectKey { get; init; }

    /// <summary>The branch, when the call named one.</summary>
    public string? Branch { get; init; }

    /// <summary>The pull request, when the call named one.</summary>
    public string? PullRequest { get; init; }

    /// <summary>
    /// <c>OK</c>, <c>ERROR</c> or <c>NONE</c>. <c>NONE</c> with no conditions is normal for a scope
    /// that has never been analysed against a gate — see <see cref="Note"/>.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Every condition, passing and failing alike.</summary>
    public IReadOnlyList<QualityGateCondition> Conditions { get; init; } = [];

    /// <summary>
    /// Only the failing conditions — a pre-filtered copy, so the caller does not have to scan
    /// <see cref="Conditions"/> to find out what is wrong.
    /// </summary>
    public IReadOnlyList<QualityGateCondition> FailingConditions { get; init; } = [];

    /// <summary>The overview page where the gate is shown.</summary>
    public string? Url { get; init; }

    /// <summary>Set when the status needs explaining rather than reporting.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// One Compute Engine task: a report that has been uploaded and is queued, running, or done.
/// </summary>
/// <remarks>
/// The scope belongs to the task rather than to the request. A task with neither
/// <see cref="Branch"/> nor <see cref="PullRequest"/> analysed the main branch.
/// </remarks>
internal sealed record AnalysisTask
{
    /// <summary>The task id.</summary>
    public string? Id { get; init; }

    /// <summary><c>PENDING</c>, <c>IN_PROGRESS</c>, <c>SUCCESS</c>, <c>FAILED</c> or <c>CANCELED</c>.</summary>
    public string? Status { get; init; }

    /// <summary>The branch this task analysed, when it analysed one.</summary>
    public string? Branch { get; init; }

    /// <summary>The pull request this task analysed, as the SCM number.</summary>
    public string? PullRequest { get; init; }

    /// <summary>When the scanner uploaded the report.</summary>
    public DateTimeOffset? SubmittedAt { get; init; }

    /// <summary>When processing started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When processing ended. Absent while the task is queued or running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>How long processing took, in milliseconds.</summary>
    public long? ExecutionTimeMs { get; init; }

    /// <summary>Who ran the scanner.</summary>
    public string? SubmittedBy { get; init; }

    /// <summary>Why a <c>FAILED</c> task failed. This is the reason to read a failed task at all.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The failure's category, when SonarQube classified it.</summary>
    public string? ErrorType { get; init; }

    /// <summary>How many warnings the analysis raised.</summary>
    public int? WarningCount { get; init; }

    /// <summary>The warning texts, when this token is allowed to read them.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// The result of <c>getAnalysisStatus</c>: what the Compute Engine is doing with this project.
/// </summary>
/// <remarks>
/// This is the one result in the server that is expected to change between two identical calls, and
/// it is the only way to tell "the gate is green" from "the gate is still the previous commit's".
/// </remarks>
internal sealed record AnalysisStatusResult
{
    /// <summary>The project the tasks belong to.</summary>
    public string? ProjectKey { get; init; }

    /// <summary>Whether anything is queued or running right now.</summary>
    public bool AnalysisInProgress { get; init; }

    /// <summary>Tasks submitted and not yet finished, oldest first.</summary>
    public IReadOnlyList<AnalysisTask> Pending { get; init; } = [];

    /// <summary>The most recently executed task, if the project has ever been analysed.</summary>
    public AnalysisTask? Latest { get; init; }

    /// <summary>What the state means for the caller's next move.</summary>
    public string? Note { get; init; }

    /// <summary>The project's page, for a human.</summary>
    public string? Url { get; init; }
}
