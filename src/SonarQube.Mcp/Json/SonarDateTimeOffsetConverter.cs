using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonarQube.Mcp.Json;

/// <summary>
/// Reads the timestamps SonarQube actually sends.
/// </summary>
/// <remarks>
/// <para>
/// This is the one custom converter in the codebase, and it exists because SonarQube emits
/// <c>2026-08-11T15:35:10+0000</c> — an ISO 8601 <em>basic</em>-format offset, with no colon.
/// <see cref="System.Text.Json"/>'s built-in <see cref="DateTimeOffset"/> reader implements the
/// RFC 3339 profile, which requires <c>+HH:mm</c>, and rejects that value outright. Without this
/// converter every fixture in the test suite fails on the first date field, and every live call
/// fails on the first issue.
/// </para>
/// <para>
/// Accepted: <c>+0000</c>, <c>+00:00</c>, <c>Z</c>, and negative offsets such as <c>-0500</c>. A
/// missing offset is read as UTC rather than as local time, because a server-side timestamp with no
/// zone is never in the client's zone and a silent local-time reading would shift every date by the
/// developer's own offset.
/// </para>
/// <para>
/// It is applied by <c>[JsonConverter]</c> on each wire property rather than registered in
/// <c>JsonSerializerOptions.Converters</c>. Attribute application is what the source generator can
/// see: a converter added to the options collection at runtime is invisible to it, which under
/// Native AOT means either a reflection fallback that is trimmed away or a silent switch back to
/// the built-in reader. The declaration is also then visible on the property it affects.
/// </para>
/// <para>
/// Typed for <see cref="Nullable{T}"/> because every wire property is nullable, and because doing
/// so lets <c>null</c> and <c>""</c> be answered here — as "no date" — instead of surfacing as a
/// parse failure on a field the endpoint simply did not fill in.
/// </para>
/// </remarks>
internal sealed class SonarDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    /// <inheritdoc />
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a SonarQube timestamp as a JSON string, but found {reader.TokenType}.");
        }

        var text = reader.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Parse(text);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writing is not on any production path — nothing here ever sends a wire DTO back, because
    /// every write is form-encoded. It is implemented anyway so that a round-trip in a test is not
    /// a <see cref="NotSupportedException"/>, and it writes the RFC 3339 <c>+HH:mm</c> form rather
    /// than SonarQube's basic-format one: this end of the pipe should emit the standard spelling.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.GetValueOrDefault());
    }

    /// <summary>
    /// Parses one timestamp, normalising a basic-format offset into the extended form the framework
    /// parser understands.
    /// </summary>
    /// <exception cref="JsonException">The value is not a timestamp in any accepted form.</exception>
    internal static DateTimeOffset Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var candidate = Normalise(text.Trim());

        // AssumeUniversal for a value that carries no offset at all; AdjustToUniversal is
        // deliberately NOT set, so an offset that *is* present is preserved rather than folded away
        // — "2026-08-11T18:35:10+03:00" and its UTC equivalent are the same instant but not the
        // same thing to read.
        if (DateTimeOffset.TryParse(
                candidate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new JsonException(
            $"\"{text}\" is not a timestamp this server can read. SonarQube sends " +
            "\"2026-08-11T15:35:10+0000\"; \"+00:00\" and \"Z\" offsets are accepted too.");
    }

    /// <summary>
    /// Turns a trailing basic-format offset (<c>±HHmm</c>) into the extended form (<c>±HH:mm</c>),
    /// and leaves everything else exactly as it arrived.
    /// </summary>
    /// <remarks>
    /// The sign is looked for only in the last five characters, and only after a digit, so the
    /// <c>-</c> characters inside the date part cannot be mistaken for an offset sign.
    /// </remarks>
    private static string Normalise(string text)
    {
        // "…±HHmm" is exactly five trailing characters, the first of which is the sign.
        if (text.Length < 6)
        {
            return text;
        }

        var signIndex = text.Length - 5;
        var sign = text[signIndex];

        if (sign is not ('+' or '-') || !char.IsAsciiDigit(text[signIndex - 1]))
        {
            return text;
        }

        for (var i = signIndex + 1; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return text;
            }
        }

        return string.Concat(text.AsSpan(0, signIndex + 3), ":", text.AsSpan(signIndex + 3));
    }
}
