using SonarQube.Mcp.Http.Models;
using SonarQube.Mcp.Tools;

using Xunit;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// The three things a measure needs doing to it before a model should read it: a rating turned into
/// its letter, a new-code value lifted out of <c>periods</c>, and the diff between what was asked
/// for and what came back.
/// </summary>
/// <remarks>
/// Each of the three is a wrong answer waiting to happen, and each is wrong in a direction that
/// looks plausible: <c>"1.0"</c> reads as a score out of five where higher is better and means
/// exactly the reverse; a <c>new_*</c> metric read from <c>value</c> comes back empty or, worse,
/// carrying the overall number; and an absent <c>coverage</c> read as zero says the opposite of
/// "coverage is not measured here".
/// </remarks>
public class MeasureFormattingTests
{
    [Theory]
    [InlineData("1.0", "A")]
    [InlineData("2.0", "B")]
    [InlineData("3", "C")]
    [InlineData("4.0", "D")]
    [InlineData("5.0", "E")]
    [InlineData("1", "A")]
    public void ARatingIsTranslatedIntoItsLetter(string value, string expected) =>
        Assert.Equal(expected, MeasureFormatting.Format("sqale_rating", value));

    [Theory]
    [InlineData("new_reliability_rating")]
    [InlineData("security_review_rating")]
    [InlineData("software_quality_maintainability_rating")]
    public void EveryRatingMetricIsRecognisedByItsSuffix(string metric) =>
        Assert.Equal("A", MeasureFormatting.Format(metric, "1.0"));

    /// <summary>
    /// A value outside 1–5, or one that is not a number at all, is passed through: the raw text is
    /// still more useful than a dropped measure, and inventing a letter for it would be a lie.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("6.0")]
    [InlineData("")]
    [InlineData("unknown")]
    public void AnUntranslatableRatingIsLeftExactlyAsItArrived(string value) =>
        Assert.Equal(value, MeasureFormatting.Format("sqale_rating", value));

    [Theory]
    [InlineData("ncloc", "119857")]
    [InlineData("coverage", "83.4")]
    [InlineData("alert_status", "ERROR")]
    [InlineData("sqale_index", "13115")]
    public void ANonRatingMetricIsNeverTouched(string metric, string value) =>
        Assert.Equal(value, MeasureFormatting.Format(metric, value));

    [Fact]
    public void ANullValueOrMetricIsAnsweredWithTheValueItself()
    {
        Assert.Null(MeasureFormatting.Format("sqale_rating", null));
        Assert.Equal("1.0", MeasureFormatting.Format(null, "1.0"));
    }

    /// <summary>
    /// The new-code value lives in <c>periods[0].value</c> and must never be reported as
    /// <c>value</c>: a quality gate judges new code, so mixing the two is wrong in the direction
    /// that matters most.
    /// </summary>
    [Fact]
    public void ANewCodeValueIsReportedSeparatelyFromTheOverallOne()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto
            {
                Metric = "new_violations",
                Periods = [new MeasurePeriodDto { Index = 1, Value = "13", BestValue = false }],
            },
        ]);

        var measure = Assert.Single(values);

        Assert.Equal("new_violations", measure.Metric);
        Assert.Null(measure.Value);
        Assert.Equal("13", measure.NewCodeValue);
        Assert.False(measure.BestValue);
    }

    [Fact]
    public void AMeasureCanCarryBothAnOverallAndANewCodeValue()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto
            {
                Metric = "coverage",
                Value = "83.4",
                Periods = [new MeasurePeriodDto { Index = 1, Value = "91.2" }],
            },
        ]);

        var measure = Assert.Single(values);

        Assert.Equal("83.4", measure.Value);
        Assert.Equal("91.2", measure.NewCodeValue);
    }

    /// <summary>
    /// Index 1 is the new-code period. A future second period must not be reported as new code, so
    /// the index is checked rather than the position taken on trust.
    /// </summary>
    [Fact]
    public void APeriodThatIsNotTheNewCodeOneIsIgnored()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto
            {
                Metric = "coverage",
                Value = "83.4",
                Periods = [new MeasurePeriodDto { Index = 2, Value = "12.0" }],
            },
        ]);

        Assert.Null(Assert.Single(values).NewCodeValue);
    }

    /// <summary>An older response has one unnumbered period, and that one is the new-code one.</summary>
    [Fact]
    public void AnUnnumberedPeriodIsTreatedAsTheNewCodePeriod()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto { Metric = "new_coverage", Periods = [new MeasurePeriodDto { Value = "77.7" }] },
        ]);

        Assert.Equal("77.7", Assert.Single(values).NewCodeValue);
    }

    [Fact]
    public void ARatingIsTranslatedInsideTheNewCodePeriodToo()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto
            {
                Metric = "new_maintainability_rating",
                Periods = [new MeasurePeriodDto { Index = 1, Value = "5.0" }],
            },
        ]);

        Assert.Equal("E", Assert.Single(values).NewCodeValue);
    }

    [Fact]
    public void AMeasureWithNoMetricKeyIsDroppedRatherThanReturnedNameless()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto { Value = "1" },
            new MeasureDto { Metric = "ncloc", Value = "2" },
        ]);

        Assert.Equal("ncloc", Assert.Single(values).Metric);
    }

    [Fact]
    public void NoMeasuresAtAllIsAnEmptyListRatherThanNull()
    {
        Assert.Empty(MeasureFormatting.Values(null));
        Assert.Empty(MeasureFormatting.Values([]));
    }

    /// <summary>
    /// <c>missingMetrics</c> is the whole answer to "absent is not zero": it names exactly what was
    /// requested and did not come back, in the order it was requested.
    /// </summary>
    [Fact]
    public void MissingMetricsNamesExactlyWhatWasAskedForAndNotReturned()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal);
        MeasureFormatting.CollectMetrics([new MeasureDto { Metric = "ncloc" }], returned);

        var missing = MeasureFormatting.MissingMetrics(["coverage", "ncloc", "new_coverage"], returned);

        Assert.Equal(["coverage", "new_coverage"], missing);
    }

    /// <summary>
    /// A metric that came back without being asked for does not appear and does not throw: the
    /// caller asked a question, and this answers it rather than auditing SonarQube's response.
    /// </summary>
    [Fact]
    public void AMetricReturnedButNeverRequestedIsSimplyIgnored()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal);

        MeasureFormatting.CollectMetrics(
            [new MeasureDto { Metric = "ncloc" }, new MeasureDto { Metric = "duplicated_lines" }],
            returned);

        Assert.Empty(MeasureFormatting.MissingMetrics(["ncloc"], returned));
    }

    [Fact]
    public void CollectMetricsIgnoresMeasuresWithNoKeyAndAnAbsentArray()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal);

        MeasureFormatting.CollectMetrics(null, returned);
        MeasureFormatting.CollectMetrics([new MeasureDto { Value = "1" }], returned);

        Assert.Empty(returned);
    }

    [Fact]
    public void EverythingRequestedComingBackLeavesNothingMissing()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal) { "ncloc", "coverage" };

        Assert.Empty(MeasureFormatting.MissingMetrics(["ncloc", "coverage"], returned));
    }
}
