namespace SonarQube.Mcp.Tools.Models;

/// <summary>
/// One metric's value for one component.
/// </summary>
/// <remarks>
/// <para>
/// Values are strings, exactly as SonarQube sends them, because <c>WORK_DUR</c> and <c>LEVEL</c>
/// metrics are not numbers and rounding <c>"119857"</c> through a double to get an integer back is a
/// lossy detour to the same string.
/// </para>
/// <para>
/// <b>A <c>*_rating</c> value is translated to its letter.</b> SonarQube reports ratings as
/// <c>"1.0"</c>–<c>"5.0"</c>, which reads as a score where higher is better and means the exact
/// opposite: <c>1.0</c> is <c>A</c> and <c>5.0</c> is <c>E</c>.
/// </para>
/// </remarks>
internal sealed record MeasureValue
{
    /// <summary>The metric key.</summary>
    public string? Metric { get; init; }

    /// <summary>The overall value. Absent for a metric that only has a new-code value.</summary>
    public string? Value { get; init; }

    /// <summary>
    /// The value within the new-code period. SonarQube never puts this in <c>value</c>, so a
    /// <c>new_*</c> metric read from <see cref="Value"/> alone comes back empty.
    /// </summary>
    public string? NewCodeValue { get; init; }

    /// <summary>Whether the value is the best this metric can have.</summary>
    public bool? BestValue { get; init; }
}

/// <summary>The measures of one component.</summary>
internal sealed record MeasuresResult
{
    /// <summary>The component key the measures belong to.</summary>
    public string? Component { get; init; }

    /// <summary>Its project-relative path, for a file or directory.</summary>
    public string? Path { get; init; }

    /// <summary>The project key.</summary>
    public string? ProjectKey { get; init; }

    /// <summary>The branch, when the call named one.</summary>
    public string? Branch { get; init; }

    /// <summary>The pull request, when the call named one.</summary>
    public string? PullRequest { get; init; }

    /// <summary>The measures that had a value.</summary>
    public IReadOnlyList<MeasureValue> Measures { get; init; } = [];

    /// <summary>
    /// The requested metrics that came back with nothing. <b>Absent is not zero</b>: SonarQube
    /// silently omits a metric it has no data for, so a missing <c>coverage</c> means "coverage is
    /// not measured here", never "coverage is 0%".
    /// </summary>
    public IReadOnlyList<string> MissingMetrics { get; init; } = [];

    /// <summary>The component's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>One component in a subtree measure listing.</summary>
internal sealed record ComponentMeasures
{
    /// <summary>The component key.</summary>
    public string? Component { get; init; }

    /// <summary>The project-relative path.</summary>
    public string? Path { get; init; }

    /// <summary>The short name.</summary>
    public string? Name { get; init; }

    /// <summary><c>FIL</c>, <c>UTS</c>, <c>DIR</c> or <c>TRK</c>.</summary>
    public string? Qualifier { get; init; }

    /// <summary>This component's measures.</summary>
    public IReadOnlyList<MeasureValue> Measures { get; init; } = [];

    /// <summary>The component's page in SonarQube.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of components with their measures — the "worst files by metric" answer.</summary>
internal sealed record ComponentMeasuresResult
{
    /// <summary>The components on this page, in the requested order.</summary>
    public IReadOnlyList<ComponentMeasures> Components { get; init; } = [];

    /// <summary>The 1-based page this is.</summary>
    public int Page { get; init; }

    /// <summary>How many items a full page holds.</summary>
    public int PageSize { get; init; }

    /// <summary>Total matching components.</summary>
    public int? TotalCount { get; init; }

    /// <summary>Whether another page exists.</summary>
    public bool HasMore { get; init; }

    /// <summary>Set only when there is something about this result the caller has to know.</summary>
    public string? Note { get; init; }

    /// <summary>The requested metrics no component on this page reported. Absent is not zero.</summary>
    public IReadOnlyList<string> MissingMetrics { get; init; } = [];
}

/// <summary>One point in a metric's history.</summary>
internal sealed record MeasureHistoryPoint
{
    /// <summary>When the analysis ran.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>The value at that analysis, or absent when the metric had no data then — a gap, not a zero.</summary>
    public string? Value { get; init; }
}

/// <summary>One metric's time series.</summary>
internal sealed record MetricHistory
{
    /// <summary>The metric key.</summary>
    public string? Metric { get; init; }

    /// <summary>The series, oldest first.</summary>
    public IReadOnlyList<MeasureHistoryPoint> History { get; init; } = [];
}

/// <summary>A component's measures over time.</summary>
internal sealed record MeasuresHistoryResult
{
    /// <summary>The component the history belongs to.</summary>
    public string? Component { get; init; }

    /// <summary>One series per requested metric.</summary>
    public IReadOnlyList<MetricHistory> Metrics { get; init; } = [];

    /// <summary>The 1-based page this is.</summary>
    public int Page { get; init; }

    /// <summary>How many items a full page holds.</summary>
    public int PageSize { get; init; }

    /// <summary>
    /// <b>The number of analyses, not the number of measures.</b> This endpoint pages over the
    /// project's analysis history, so every metric shares one page.
    /// </summary>
    public int? TotalCount { get; init; }

    /// <summary>Whether another page of analyses exists.</summary>
    public bool HasMore { get; init; }

    /// <summary>Set only when there is something about this result the caller has to know.</summary>
    public string? Note { get; init; }
}

/// <summary>One metric definition — the answer to "what is the key for X".</summary>
internal sealed record MetricSummary
{
    /// <summary>The key, which is what every measures tool takes.</summary>
    public string? Key { get; init; }

    /// <summary>The display name.</summary>
    public string? Name { get; init; }

    /// <summary><c>INT</c>, <c>FLOAT</c>, <c>PERCENT</c>, <c>RATING</c>, <c>LEVEL</c>, <c>WORK_DUR</c>, <c>MILLISEC</c>, <c>BOOL</c>, <c>STRING</c>.</summary>
    public string? Type { get; init; }

    /// <summary>The grouping, for example <c>Coverage</c>, <c>Issues</c> or <c>Duplications</c>.</summary>
    public string? Domain { get; init; }

    /// <summary>What it measures.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether a higher value is better. Absent when the metric is neither good nor bad — a line
    /// count is just a line count. This is the answer to "is 4.8% duplication good?".
    /// </summary>
    public bool? HigherIsBetter { get; init; }
}

/// <summary>Every metric this SonarQube instance knows, filtered client-side.</summary>
internal sealed record MetricListResult
{
    /// <summary>The matching metric definitions.</summary>
    public IReadOnlyList<MetricSummary> Metrics { get; init; } = [];

    /// <summary>How many are in <see cref="Metrics"/>. This tool fetches every metric in one call, so there is no paging.</summary>
    public int TotalCount { get; init; }

    /// <summary>Records what the client-side filtering removed, so the filtering is visible rather than silent.</summary>
    public string? Note { get; init; }
}

/// <summary>One source line's coverage, duplication and new-code facts. The source text is never returned.</summary>
internal sealed record FileCoverageLine
{
    /// <summary>The line number, 1-based.</summary>
    public int Line { get; init; }

    /// <summary>How many times tests executed the line. <c>0</c> is uncovered; absent means not measured.</summary>
    public int? Hits { get; init; }

    /// <summary>How many branches the line has.</summary>
    public int? Conditions { get; init; }

    /// <summary>How many of them tests exercised. Fewer than <see cref="Conditions"/> is partial coverage.</summary>
    public int? CoveredConditions { get; init; }

    /// <summary>Whether the line falls inside the new-code period.</summary>
    public bool? IsNew { get; init; }

    /// <summary>Whether the line is part of a duplicated block.</summary>
    public bool? Duplicated { get; init; }
}

/// <summary>
/// A file's per-line coverage facts — the thing SonarQube knows that a checkout does not.
/// </summary>
/// <remarks>
/// The source text is deliberately absent. SonarQube returns it as syntax-highlighted HTML, the
/// agent already has the file on disk, and including it would triple the payload for no information.
/// </remarks>
internal sealed record FileCoverageResult
{
    /// <summary>The component key that was read.</summary>
    public string? Component { get; init; }

    /// <summary>Its project-relative path.</summary>
    public string? Path { get; init; }

    /// <summary>First line read.</summary>
    public int From { get; init; }

    /// <summary>Last line read, inclusive.</summary>
    public int To { get; init; }

    /// <summary>Lines tests never executed.</summary>
    public IReadOnlyList<int> UncoveredLines { get; init; } = [];

    /// <summary>Lines that ran but whose branches were not all taken — the <c>if</c> with one side untested.</summary>
    public IReadOnlyList<int> PartiallyCoveredLines { get; init; } = [];

    /// <summary>Lines inside the new-code period.</summary>
    public IReadOnlyList<int> NewLines { get; init; } = [];

    /// <summary>Lines belonging to a duplicated block.</summary>
    public IReadOnlyList<int> DuplicatedLines { get; init; } = [];

    /// <summary>Per-line detail for the lines this call returned.</summary>
    public IReadOnlyList<FileCoverageLine> Lines { get; init; } = [];

    /// <summary>Says which lines were included and warns when the file has no coverage data at all.</summary>
    public string? Note { get; init; }

    /// <summary>The file's page in SonarQube's code browser.</summary>
    public string? Url { get; init; }
}
