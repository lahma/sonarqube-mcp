using System.Text.Json;
using System.Text.Json.Serialization;

using SonarQube.Mcp.Json;

using Xunit;

namespace SonarQube.Mcp.Tests.Json;

/// <summary>
/// Covers <see cref="SonarDateTimeOffsetConverter"/>, the one custom converter in the codebase.
/// </summary>
/// <remarks>
/// It exists because SonarQube emits ISO 8601 <em>basic</em>-format offsets (<c>+0000</c>, no
/// colon) and <see cref="System.Text.Json"/>'s built-in <see cref="DateTimeOffset"/> reader
/// implements the RFC 3339 profile, which requires <c>+HH:mm</c> and rejects them. Without this,
/// every fixture fails on its first date field.
/// </remarks>
public class SonarDateTimeOffsetConverterTests
{
    /// <summary>Every offset spelling that has to work, with the instant it denotes.</summary>
    public static TheoryData<string, string> AcceptedTimestamps => new()
    {
        // What SonarQube actually sends.
        { "2026-08-11T15:35:10+0000", "2026-08-11T15:35:10+00:00" },

        // The RFC 3339 spelling of the same thing, in case a future version switches.
        { "2026-08-11T15:35:10+00:00", "2026-08-11T15:35:10+00:00" },
        { "2026-08-11T15:35:10Z", "2026-08-11T15:35:10+00:00" },

        // Non-UTC offsets, basic and extended.
        { "2026-08-11T15:35:10-0500", "2026-08-11T15:35:10-05:00" },
        { "2026-08-11T15:35:10-05:00", "2026-08-11T15:35:10-05:00" },
        { "2026-08-11T18:35:10+0300", "2026-08-11T18:35:10+03:00" },

        // Sub-second precision, which measures/search_history entries can carry.
        { "2026-08-11T15:35:10.123+0000", "2026-08-11T15:35:10.123+00:00" },

        // No offset at all: read as UTC, never as the developer's local time.
        { "2026-08-11T15:35:10", "2026-08-11T15:35:10+00:00" },
    };

    /// <summary>
    /// Values that are not timestamps in any accepted form.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>"2026/08/11 15:35"</c>: the invariant parser accepts that, and a test
    /// that asserted otherwise would be asserting a framework detail rather than this converter's
    /// contract. The rule here is "unparsable throws", not "only ISO 8601 is accepted".
    /// </remarks>
    public static TheoryData<string> GarbageTimestamps =>
    [
        "yesterday",
        "2026-13-45T99:99:99+0000",
        "not a date",
        "+0000",
        "2026-08-11T15:35:10+0000 trailing junk",
        "AZ_xeOzImT_q4T_1FWf7",
    ];

    [Theory]
    [MemberData(nameof(AcceptedTimestamps))]
    public void AcceptedOffsetSpellingsParseToTheRightInstant(string wire, string expected)
    {
        var parsed = SonarDateTimeOffsetConverter.Parse(wire);

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), parsed);

        // The offset itself is preserved, not folded to UTC: the same instant expressed in another
        // zone is not the same thing to read back.
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).Offset, parsed.Offset);
    }

    [Theory]
    [MemberData(nameof(AcceptedTimestamps))]
    public void AcceptedSpellingsAlsoParseThroughTheConverterItself(string wire, string expected)
    {
        var holder = Deserialize($$"""{"date":"{{wire}}"}""");

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), holder!.Date);
    }

    [Fact]
    public void TheBuiltInReaderWouldHaveRejectedSonarQubesOwnFormat()
    {
        // The whole justification for the converter, asserted rather than asserted-in-a-comment: if
        // System.Text.Json ever starts accepting the basic-format offset, this test fails and the
        // converter can be reconsidered. Through the source-generated contract rather than the
        // reflection overload, which the test project disables the same way the server does.
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize("\"2026-08-11T15:35:10+0000\"", TestJsonContext.Default.DateTimeOffset));

        // The extended spelling is what it does accept, so the difference really is the colon.
        Assert.Equal(
            new DateTimeOffset(2026, 8, 11, 15, 35, 10, TimeSpan.Zero),
            JsonSerializer.Deserialize("\"2026-08-11T15:35:10+00:00\"", TestJsonContext.Default.DateTimeOffset));
    }

    [Fact]
    public void ANullValueIsNoDate()
    {
        Assert.Null(Deserialize("""{"date":null}""")!.Date);
    }

    [Fact]
    public void AnAbsentPropertyIsNoDate()
    {
        // measures/search_history sends entries with a date and no value, and other endpoints omit
        // closeDate entirely; absent must never become a parse failure.
        Assert.Null(Deserialize("{}")!.Date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyStringIsNoDate(string value)
    {
        Assert.Null(Deserialize($$"""{"date":"{{value}}"}""")!.Date);
    }

    [Theory]
    [MemberData(nameof(GarbageTimestamps))]
    public void GarbageThrowsAJsonExceptionNamingTheValue(string value)
    {
        var exception = Assert.Throws<JsonException>(() => Deserialize($$"""{"date":"{{value}}"}"""));

        // Naming the value is what makes the failure actionable: the property path alone does not
        // say which of a page of issues carried the bad date.
        Assert.Contains(value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonStringTokenThrowsRatherThanSilentlyProducingNull()
    {
        var exception = Assert.Throws<JsonException>(() => Deserialize("""{"date":12345}"""));

        Assert.Contains("Number", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingEmitsTheRfc3339SpellingRatherThanSonarQubesOwn()
    {
        var json = JsonSerializer.Serialize(
            new TimestampHolder { Date = new DateTimeOffset(2026, 8, 11, 15, 35, 10, TimeSpan.Zero) },
            TestJsonContext.Default.TimestampHolder);

        // Nothing production sends a wire DTO back — every write is form-encoded — but this end of
        // the pipe should still emit the standard spelling rather than mirroring the quirk.
        Assert.Equal("""{"date":"2026-08-11T15:35:10+00:00"}""", json);
    }

    [Fact]
    public void WritingANullDateEmitsNull()
    {
        var json = JsonSerializer.Serialize(new TimestampHolder(), TestJsonContext.Default.TimestampHolder);

        Assert.Equal("""{"date":null}""", json);
    }

    private static TimestampHolder? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, TestJsonContext.Default.TimestampHolder);
}

/// <summary>
/// A stand-in for a wire DTO, carrying the converter the same way every real date property does —
/// by attribute, so the source generator can see it.
/// </summary>
internal sealed record TimestampHolder
{
    [JsonPropertyName("date")]
    [JsonConverter(typeof(SonarDateTimeOffsetConverter))]
    public DateTimeOffset? Date { get; init; }
}

/// <summary>
/// The test project also builds with reflection-based serialization disabled, so even a test-only
/// type needs a source-generated contract.
/// </summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TimestampHolder))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
