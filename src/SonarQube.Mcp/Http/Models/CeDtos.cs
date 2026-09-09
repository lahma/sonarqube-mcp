using System.Text.Json.Serialization;

using SonarQube.Mcp.Json;

namespace SonarQube.Mcp.Http.Models;

/// <summary>
/// <c>api/ce/component</c> — the Compute Engine's view of one project.
/// </summary>
/// <remarks>
/// <para>
/// A scanner run does not finish when the CI step goes green: it uploads a report and the Compute
/// Engine processes it afterwards. Until that finishes, every other endpoint answers from the
/// <em>previous</em> analysis and says nothing about it, which is why a quality gate read straight
/// after a pipeline can be confidently wrong.
/// </para>
/// <para>
/// This is the only <c>api/ce/*</c> action this server calls. <c>ce/activity</c> answers
/// <c>401</c> anonymously and needs project-administer rights even with a token, and
/// <c>ce/activity_status</c> answers <c>403</c> (both verified 2026-09-09); this one needs only
/// <em>Browse</em>.
/// </para>
/// </remarks>
internal sealed record CeComponentResponseDto
{
    /// <summary>Submitted but not yet finished tasks, oldest first. Empty when nothing is running.</summary>
    [JsonPropertyName("queue")]
    public IReadOnlyList<CeTaskDto>? Queue { get; init; }

    /// <summary>The most recently executed task, present once the project has ever been analysed.</summary>
    [JsonPropertyName("current")]
    public CeTaskDto? Current { get; init; }
}

/// <summary>
/// One Compute Engine task.
/// </summary>
/// <remarks>
/// <para>
/// The scope a task belongs to is a property of the task, not a request parameter: the endpoint
/// takes no <c>branch</c> or <c>pullRequest</c>, and each task reports its own. A task with neither
/// analysed the main branch.
/// </para>
/// <para>
/// Note <see cref="ExecutedAt"/> and <see cref="FinishedAt"/>. SonarQube Cloud sends
/// <c>executedAt</c> (verified live 2026-09-09); the endpoint's own published response example
/// calls the field <c>finishedAt</c>. Both are read and the tool layer takes whichever arrived,
/// because guessing wrong would silently drop the one timestamp that says the analysis is over.
/// </para>
/// </remarks>
internal sealed record CeTaskDto
{
    /// <summary>The task id. <c>ce/task</c> would expand it, but that needs Execute Analysis rights.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Always <c>REPORT</c> on Cloud — the only task type the catalogue declares.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The analysed project's key.</summary>
    [JsonPropertyName("componentKey")]
    public string? ComponentKey { get; init; }

    /// <summary>The analysed project's display name.</summary>
    [JsonPropertyName("componentName")]
    public string? ComponentName { get; init; }

    /// <summary><c>PENDING</c>, <c>IN_PROGRESS</c>, <c>SUCCESS</c>, <c>FAILED</c> or <c>CANCELED</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>The branch this task analysed, absent when it analysed a pull request or the main branch.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    /// <summary>The pull request this task analysed, as the SCM number.</summary>
    [JsonPropertyName("pullRequest")]
    public string? PullRequest { get; init; }

    /// <summary>The analysis the task produced; absent while it is still queued.</summary>
    [JsonPropertyName("analysisId")]
    public string? AnalysisId { get; init; }

    /// <summary>Who ran the scanner.</summary>
    [JsonPropertyName("submitterLogin")]
    public string? SubmitterLogin { get; init; }

    /// <summary>When the report was uploaded.</summary>
    [JsonPropertyName("submittedAt")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? SubmittedAt { get; init; }

    /// <summary>When processing started; absent while queued.</summary>
    [JsonPropertyName("startedAt")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When processing ended, as SonarQube Cloud spells it.</summary>
    [JsonPropertyName("executedAt")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? ExecutedAt { get; init; }

    /// <summary>The same instant under the name the published response example uses.</summary>
    [JsonPropertyName("finishedAt")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>How long processing took, in milliseconds.</summary>
    [JsonPropertyName("executionTimeMs")]
    public long? ExecutionTimeMs { get; init; }

    /// <summary>Why the task failed. Present only on a <c>FAILED</c> task, and the whole reason to look.</summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    /// <summary>The failure's category, when SonarQube classified it.</summary>
    [JsonPropertyName("errorType")]
    public string? ErrorType { get; init; }

    /// <summary>How many warnings the analysis raised. The texts need Execute Analysis rights to read.</summary>
    [JsonPropertyName("warningCount")]
    public int? WarningCount { get; init; }

    /// <summary>The warning texts, when this token is allowed to see them.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string>? Warnings { get; init; }
}
