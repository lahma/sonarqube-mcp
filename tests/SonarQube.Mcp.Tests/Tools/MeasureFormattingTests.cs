using NUnit.Framework;

using SonarQube.Mcp.Http.Models;
using SonarQube.Mcp.Tools;

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
    [TestCase("1.0", "A")]
    [TestCase("2.0", "B")]
    [TestCase("3", "C")]
    [TestCase("4.0", "D")]
    [TestCase("5.0", "E")]
    [TestCase("1", "A")]
    public void ARatingIsTranslatedIntoItsLetter(string value, string expected) =>
        Assert.That(MeasureFormatting.Format("sqale_rating", value), Is.EqualTo(expected));

    [TestCase("new_reliability_rating")]
    [TestCase("security_review_rating")]
    [TestCase("software_quality_maintainability_rating")]
    public void EveryRatingMetricIsRecognisedByItsSuffix(string metric) =>
        Assert.That(MeasureFormatting.Format(metric, "1.0"), Is.EqualTo("A"));

    /// <summary>
    /// A value outside 1–5, or one that is not a number at all, is passed through: the raw text is
    /// still more useful than a dropped measure, and inventing a letter for it would be a lie.
    /// </summary>
    [TestCase("0")]
    [TestCase("6.0")]
    [TestCase("")]
    [TestCase("unknown")]
    public void AnUntranslatableRatingIsLeftExactlyAsItArrived(string value) =>
        Assert.That(MeasureFormatting.Format("sqale_rating", value), Is.EqualTo(value));

    [TestCase("ncloc", "119857")]
    [TestCase("coverage", "83.4")]
    [TestCase("alert_status", "ERROR")]
    [TestCase("sqale_index", "13115")]
    public void ANonRatingMetricIsNeverTouched(string metric, string value) =>
        Assert.That(MeasureFormatting.Format(metric, value), Is.EqualTo(value));

    [Test]
    public void ANullValueOrMetricIsAnsweredWithTheValueItself()
    {
        Assert.That(MeasureFormatting.Format("sqale_rating", null), Is.Null);
        Assert.That(MeasureFormatting.Format(null, "1.0"), Is.EqualTo("1.0"));
    }

    /// <summary>
    /// The new-code value lives in <c>periods[0].value</c> and must never be reported as
    /// <c>value</c>: a quality gate judges new code, so mixing the two is wrong in the direction
    /// that matters most.
    /// </summary>
    [Test]
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

        Assert.That(values, Has.Exactly(1).Items);
        var measure = values.Single();

        Assert.That(measure.Metric, Is.EqualTo("new_violations"));
        Assert.That(measure.Value, Is.Null);
        Assert.That(measure.NewCodeValue, Is.EqualTo("13"));
        Assert.That(measure.BestValue, Is.False);
    }

    [Test]
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

        Assert.That(values, Has.Exactly(1).Items);
        var measure = values.Single();

        Assert.That(measure.Value, Is.EqualTo("83.4"));
        Assert.That(measure.NewCodeValue, Is.EqualTo("91.2"));
    }

    /// <summary>
    /// Index 1 is the new-code period. A future second period must not be reported as new code, so
    /// the index is checked rather than the position taken on trust.
    /// </summary>
    [Test]
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

        Assert.That(values, Has.Exactly(1).Items);
        Assert.That(values.Single().NewCodeValue, Is.Null);
    }

    /// <summary>An older response has one unnumbered period, and that one is the new-code one.</summary>
    [Test]
    public void AnUnnumberedPeriodIsTreatedAsTheNewCodePeriod()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto { Metric = "new_coverage", Periods = [new MeasurePeriodDto { Value = "77.7" }] },
        ]);

        Assert.That(values, Has.Exactly(1).Items);
        Assert.That(values.Single().NewCodeValue, Is.EqualTo("77.7"));
    }

    [Test]
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

        Assert.That(values, Has.Exactly(1).Items);
        Assert.That(values.Single().NewCodeValue, Is.EqualTo("E"));
    }

    [Test]
    public void AMeasureWithNoMetricKeyIsDroppedRatherThanReturnedNameless()
    {
        var values = MeasureFormatting.Values(
        [
            new MeasureDto { Value = "1" },
            new MeasureDto { Metric = "ncloc", Value = "2" },
        ]);

        Assert.That(values, Has.Exactly(1).Items);
        Assert.That(values.Single().Metric, Is.EqualTo("ncloc"));
    }

    [Test]
    public void NoMeasuresAtAllIsAnEmptyListRatherThanNull()
    {
        Assert.That(MeasureFormatting.Values(null), Is.Empty);
        Assert.That(MeasureFormatting.Values([]), Is.Empty);
    }

    /// <summary>
    /// <c>missingMetrics</c> is the whole answer to "absent is not zero": it names exactly what was
    /// requested and did not come back, in the order it was requested.
    /// </summary>
    [Test]
    public void MissingMetricsNamesExactlyWhatWasAskedForAndNotReturned()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal);
        MeasureFormatting.CollectMetrics([new MeasureDto { Metric = "ncloc" }], returned);

        var missing = MeasureFormatting.MissingMetrics(["coverage", "ncloc", "new_coverage"], returned);

        Assert.That(missing, Is.EqualTo<string[]>(["coverage", "new_coverage"]));
    }

    /// <summary>
    /// A metric that came back without being asked for does not appear and does not throw: the
    /// caller asked a question, and this answers it rather than auditing SonarQube's response.
    /// </summary>
    [Test]
    public void AMetricReturnedButNeverRequestedIsSimplyIgnored()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal);

        MeasureFormatting.CollectMetrics(
            [new MeasureDto { Metric = "ncloc" }, new MeasureDto { Metric = "duplicated_lines" }],
            returned);

        Assert.That(MeasureFormatting.MissingMetrics(["ncloc"], returned), Is.Empty);
    }

    [Test]
    public void CollectMetricsIgnoresMeasuresWithNoKeyAndAnAbsentArray()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal);

        MeasureFormatting.CollectMetrics(null, returned);
        MeasureFormatting.CollectMetrics([new MeasureDto { Value = "1" }], returned);

        Assert.That(returned, Is.Empty);
    }

    [Test]
    public void EverythingRequestedComingBackLeavesNothingMissing()
    {
        var returned = new HashSet<string>(StringComparer.Ordinal) { "ncloc", "coverage" };

        Assert.That(MeasureFormatting.MissingMetrics(["ncloc", "coverage"], returned), Is.Empty);
    }
}
