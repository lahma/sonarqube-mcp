using System.Globalization;

using SonarQube.Mcp.Http.Models;
using SonarQube.Mcp.Tools.Models;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// The three things a measure needs doing to it before a model should read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>New-code values live in <c>periods[0].value</c>, never in <c>value</c></b>, so a tool that
/// reads <c>value</c> for a <c>new_*</c> metric reports the overall number as if it were the
/// new-code one — wrong in the most consequential direction, because new code is what a quality gate
/// actually judges.
/// </para>
/// <para>
/// <b>A <c>*_rating</c> is a letter, not a score.</b> SonarQube sends <c>"1.0"</c> for A and
/// <c>"5.0"</c> for E, which reads as a rating out of five where higher is better and means exactly
/// the reverse. This is the single most confusing value in the whole API.
/// </para>
/// <para>
/// <b>A metric with no data is silently omitted.</b> The diff between what was asked for and what
/// came back is therefore information, not bookkeeping: a model that reads an absent <c>coverage</c>
/// as 0% concludes the opposite of the truth.
/// </para>
/// </remarks>
internal static class MeasureFormatting
{
    /// <summary>The metric-key suffix SonarQube uses for every A–E rating.</summary>
    private const string RatingSuffix = "_rating";

    /// <summary>The letters, indexed by rating minus one.</summary>
    private static readonly string[] RatingLetters = ["A", "B", "C", "D", "E"];

    /// <summary>Maps a component's measures, dropping any that carry no metric key.</summary>
    internal static IReadOnlyList<MeasureValue> Values(IReadOnlyList<MeasureDto>? measures)
    {
        if (measures is null || measures.Count == 0)
        {
            return [];
        }

        var values = new List<MeasureValue>(measures.Count);

        foreach (var measure in measures)
        {
            if (string.IsNullOrEmpty(measure.Metric))
            {
                continue;
            }

            // Index 1 is the new-code period. SonarQube has sent exactly one entry on every
            // response seen so far, so the first entry is taken rather than searched for — but the
            // index is checked, because a future second period must not be reported as new code.
            var period = FirstNewCodePeriod(measure.Periods);

            values.Add(new MeasureValue
            {
                Metric = measure.Metric,
                Value = Format(measure.Metric, measure.Value),
                NewCodeValue = Format(measure.Metric, period?.Value),
                BestValue = measure.BestValue ?? period?.BestValue,
            });
        }

        return values;
    }

    /// <summary>Adds the metric keys a component reported to a set, for the missing-metric diff.</summary>
    internal static void CollectMetrics(IReadOnlyList<MeasureDto>? measures, HashSet<string> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        if (measures is null)
        {
            return;
        }

        foreach (var measure in measures)
        {
            if (!string.IsNullOrEmpty(measure.Metric))
            {
                into.Add(measure.Metric);
            }
        }
    }

    /// <summary>
    /// The requested metrics that came back with nothing.
    /// </summary>
    /// <remarks>
    /// A metric that was returned but never requested does not appear and does not throw: the caller
    /// asked a question and this answers it, rather than auditing SonarQube's response.
    /// </remarks>
    internal static IReadOnlyList<string> MissingMetrics(
        IReadOnlyList<string> requested,
        IReadOnlyCollection<string> returned)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(returned);

        var missing = new List<string>();

        foreach (var metric in requested)
        {
            if (!returned.Contains(metric))
            {
                missing.Add(metric);
            }
        }

        return missing;
    }

    /// <summary>
    /// Translates a <c>*_rating</c> value into its letter and leaves every other metric alone.
    /// </summary>
    /// <returns>
    /// The letter for a rating in 1–5, or the original text for anything else — an unparsable or
    /// out-of-range value is passed through rather than dropped, because the raw number is still
    /// more useful than nothing.
    /// </returns>
    internal static string? Format(string? metric, string? value)
    {
        if (string.IsNullOrEmpty(value) || metric is null || !metric.EndsWith(RatingSuffix, StringComparison.Ordinal))
        {
            return value;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return value;
        }

        var rounded = (int) Math.Round(numeric, MidpointRounding.AwayFromZero);

        return rounded >= 1 && rounded <= RatingLetters.Length ? RatingLetters[rounded - 1] : value;
    }

    /// <summary>Finds the new-code period entry, which is the one with index 1.</summary>
    private static MeasurePeriodDto? FirstNewCodePeriod(IReadOnlyList<MeasurePeriodDto>? periods)
    {
        if (periods is null)
        {
            return null;
        }

        foreach (var period in periods)
        {
            // Index absent means the response predates multi-period measures, where the single entry
            // is the new-code one.
            if (period.Index is null or 1)
            {
                return period;
            }
        }

        return null;
    }
}
