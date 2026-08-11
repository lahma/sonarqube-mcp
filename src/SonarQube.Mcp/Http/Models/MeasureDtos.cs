using System.Text.Json.Serialization;

using SonarQube.Mcp.Json;

namespace SonarQube.Mcp.Http.Models;

/// <summary>
/// <c>api/measures/component</c>.
/// </summary>
/// <remarks>
/// Not paginated — one component, one set of measures — so it does not extend
/// <see cref="PagedEnvelopeDto"/> and the tools built on it expose no <c>page</c> parameter.
/// </remarks>
internal sealed record MeasuresComponentResponseDto
{
    /// <summary>The component, with its measures attached.</summary>
    [JsonPropertyName("component")]
    public ComponentMeasuresDto? Component { get; init; }

    /// <summary>Metric definitions. Present only with <c>additionalFields=metrics</c>.</summary>
    [JsonPropertyName("metrics")]
    public IReadOnlyList<MetricDto>? Metrics { get; init; }

    /// <summary>New-code period definitions. Present only with <c>additionalFields=periods</c>.</summary>
    [JsonPropertyName("periods")]
    public IReadOnlyList<MeasurePeriodDto>? Periods { get; init; }
}

/// <summary>A component with its measures — the shape both measures endpoints return.</summary>
internal sealed record ComponentMeasuresDto : ComponentDto
{
    /// <summary>
    /// The measures that had a value. <b>A metric with no data is silently omitted</b>, so this
    /// array is not a reliable index of what was asked for — which is why the tool layer diffs it
    /// against the request to produce <c>missingMetrics</c>.
    /// </summary>
    [JsonPropertyName("measures")]
    public IReadOnlyList<MeasureDto>? Measures { get; init; }
}

/// <summary>
/// One measure.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Value"/> is a string, always</b> — for <c>INT</c> metrics, for <c>PERCENT</c>
/// metrics, for everything. Typing it as a number would fail on the first <c>WORK_DUR</c> or
/// <c>LEVEL</c> metric, and rounding <c>"119857"</c> through a <c>double</c> to get an integer back
/// is a lossy detour to the same string.
/// </para>
/// <para>
/// <b>New-code values are in <see cref="Periods"/>, never in <see cref="Value"/></b> (verified live
/// 2026-08-11: <c>new_violations</c> came back with a <c>periods</c> array and no <c>value</c> at
/// all). Reading <see cref="Value"/> for a <c>new_*</c> metric reports the overall number as if it
/// were the new-code one, which is the wrong answer in the most consequential direction.
/// </para>
/// </remarks>
internal sealed record MeasureDto
{
    /// <summary>The metric key, for example <c>ncloc</c>.</summary>
    [JsonPropertyName("metric")]
    public string? Metric { get; init; }

    /// <summary>The overall value, as text. Absent for a new-code metric.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Per-period values. Index 1 is the new-code period.</summary>
    [JsonPropertyName("periods")]
    public IReadOnlyList<MeasurePeriodDto>? Periods { get; init; }

    /// <summary>Whether the value is the best possible one for this metric.</summary>
    [JsonPropertyName("bestValue")]
    public bool? BestValue { get; init; }
}

/// <summary>
/// A measure's value within one period, or a period definition on the response envelope. SonarQube
/// uses the same object for both.
/// </summary>
internal sealed record MeasurePeriodDto
{
    /// <summary>The period index; <c>1</c> is the new-code period.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    /// <summary>The value within the period, as text.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Whether that value is the best possible one.</summary>
    [JsonPropertyName("bestValue")]
    public bool? BestValue { get; init; }

    /// <summary>How the new-code period is defined, for example <c>PREVIOUS_VERSION</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>The parameter of that mode, when it takes one.</summary>
    [JsonPropertyName("parameter")]
    public string? Parameter { get; init; }

    /// <summary>When the period started.</summary>
    [JsonPropertyName("date")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? Date { get; init; }
}

/// <summary><c>api/measures/component_tree</c>.</summary>
/// <remarks>
/// The <c>metricKeys</c> parameter is capped at <b>15</b> here (<c>maxValuesAllowed</c>, confirmed
/// from the API's own metadata), against 25 on <c>measures/component</c>. Exceeding it is a 400
/// whose text reads like a value error rather than a count one.
/// </remarks>
internal sealed record MeasuresComponentTreeResponseDto : PagedEnvelopeDto
{
    /// <summary>The subtree root, with its own measures.</summary>
    [JsonPropertyName("baseComponent")]
    public ComponentMeasuresDto? BaseComponent { get; init; }

    /// <summary>The descendants selected by <c>strategy</c> and <c>qualifiers</c>.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<ComponentMeasuresDto>? Components { get; init; }

    /// <summary>Metric definitions. Present only with <c>additionalFields=metrics</c>.</summary>
    [JsonPropertyName("metrics")]
    public IReadOnlyList<MetricDto>? Metrics { get; init; }

    /// <summary>New-code period definitions. Present only with <c>additionalFields=periods</c>.</summary>
    [JsonPropertyName("periods")]
    public IReadOnlyList<MeasurePeriodDto>? Periods { get; init; }
}

/// <summary><c>api/measures/search_history</c>.</summary>
/// <remarks>
/// Its <c>total</c> counts <b>analyses</b>, not measures: a two-metric request over 29 analyses
/// reports 29, and the tool layer says so on the result rather than letting the number read as an
/// item count.
/// </remarks>
internal sealed record MeasuresHistoryResponseDto : PagedEnvelopeDto
{
    /// <summary>One entry per requested metric, each with its own time series.</summary>
    [JsonPropertyName("measures")]
    public IReadOnlyList<MeasureHistoryDto>? Measures { get; init; }
}

/// <summary>One metric's history.</summary>
internal sealed record MeasureHistoryDto
{
    /// <summary>The metric key.</summary>
    [JsonPropertyName("metric")]
    public string? Metric { get; init; }

    /// <summary>The series, oldest first.</summary>
    [JsonPropertyName("history")]
    public IReadOnlyList<HistoryEntryDto>? History { get; init; }
}

/// <summary>One point in a metric's history.</summary>
/// <remarks>
/// <see cref="Value"/> is genuinely optional: an analysis where the metric had no data sends
/// <c>{"date":"…"}</c> with no <c>value</c> key at all (verified live 2026-08-11 for
/// <c>coverage</c>). A consumer that assumes a value per date will read a gap as a zero.
/// </remarks>
internal sealed record HistoryEntryDto
{
    /// <summary>When the analysis ran.</summary>
    [JsonPropertyName("date")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? Date { get; init; }

    /// <summary>The value at that analysis, as text, or absent when the metric had no data.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary><c>api/metrics/search</c> — flat paging, no <c>paging</c> object.</summary>
internal sealed record MetricsSearchResponseDto : PagedEnvelopeDto
{
    /// <summary>The metric definitions.</summary>
    [JsonPropertyName("metrics")]
    public IReadOnlyList<MetricDto>? Metrics { get; init; }
}

/// <summary>
/// One metric definition — the answer to "what is the metric key for X", which is otherwise a guess
/// that fails silently as an omitted measure.
/// </summary>
internal sealed record MetricDto
{
    /// <summary>SonarQube's internal numeric id, as a string.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The key, which is what every other endpoint takes.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary><c>INT</c>, <c>FLOAT</c>, <c>PERCENT</c>, <c>RATING</c>, <c>LEVEL</c>, <c>WORK_DUR</c>, <c>DATA</c>, <c>DISTRIB</c>, <c>BOOL</c>, <c>MILLISEC</c>, <c>STRING</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>What it measures.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The grouping, for example <c>Coverage</c> or <c>Issues</c>.</summary>
    [JsonPropertyName("domain")]
    public string? Domain { get; init; }

    /// <summary>
    /// <c>1</c> when a higher value is better, <c>-1</c> when lower is better, <c>0</c> when neither.
    /// This is the field that answers "is 4.8 % duplication good or bad".
    /// </summary>
    [JsonPropertyName("direction")]
    public int? Direction { get; init; }

    /// <summary>Whether the metric is a quality judgement rather than a raw count.</summary>
    [JsonPropertyName("qualitative")]
    public bool? Qualitative { get; init; }

    /// <summary>Whether the UI hides it. Hidden metrics are internal bookkeeping.</summary>
    [JsonPropertyName("hidden")]
    public bool? Hidden { get; init; }

    /// <summary>How many decimal places the value is meaningful to.</summary>
    [JsonPropertyName("decimalScale")]
    public int? DecimalScale { get; init; }
}
