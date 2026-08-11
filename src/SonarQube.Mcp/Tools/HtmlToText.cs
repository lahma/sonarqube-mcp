using System.Globalization;
using System.Text;

namespace SonarQube.Mcp.Tools;

/// <summary>
/// Turns SonarQube's rule and hotspot prose — which is HTML — into plain text a model can read.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled and deliberately small. The input is not arbitrary web HTML: it is SonarQube's own
/// rule documentation, which uses a handful of tags (<c>p</c>, <c>h2</c>–<c>h4</c>, <c>ul</c>,
/// <c>li</c>, <c>pre</c>, <c>code</c>, <c>a</c>, <c>br</c>) and is already well-formed. A parser
/// dependency would be several hundred kilobytes of Native AOT binary to answer a question this
/// answers in ninety lines.
/// </para>
/// <para>
/// What survives: paragraph and heading breaks, list bullets, and code blocks as fenced blocks —
/// the fence matters, because a rule's "how to fix it" section is mostly code and unfenced code
/// reads as prose. Everything else is dropped, including link targets: the text of a link is the
/// part worth keeping, and a URL a model cannot fetch is noise.
/// </para>
/// <para>
/// Truncation is never silent. A section over the cap ends with a visible marker and the caller is
/// told, so a model does not act on half a fix believing it is the whole one.
/// </para>
/// </remarks>
internal static class HtmlToText
{
    /// <summary>What replaces the tail of a section that hit the cap.</summary>
    private const string TruncationMarker = "… [truncated]";

    /// <summary>The tags that end a block and therefore produce a line break.</summary>
    private static readonly string[] BlockTags =
        ["p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "table", "tr", "blockquote"];

    /// <summary>
    /// Converts one HTML fragment.
    /// </summary>
    /// <param name="html">The fragment, or <see langword="null"/>.</param>
    /// <param name="maxChars">The cap, in characters, after conversion.</param>
    /// <returns>The text and whether it was cut short.</returns>
    internal static (string Text, bool Truncated) Convert(string? html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return (string.Empty, false);
        }

        var text = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var c = html[index];

            if (c == '<')
            {
                var close = html.IndexOf('>', index + 1);

                if (close < 0)
                {
                    // An unterminated tag: everything from here on is markup we cannot read, and
                    // emitting it raw would put angle brackets in the model's context.
                    break;
                }

                AppendTagBreak(text, html.AsSpan(index + 1, close - index - 1));
                index = close + 1;
                continue;
            }

            if (c == '&')
            {
                var semicolon = html.IndexOf(';', index + 1);

                // 12 is longer than every entity SonarQube emits; a stray ampersand in prose has no
                // terminator anywhere near, and must not swallow the rest of the paragraph.
                if (semicolon > index && semicolon - index <= 12)
                {
                    text.Append(Unescape(html.AsSpan(index + 1, semicolon - index - 1)));
                    index = semicolon + 1;
                    continue;
                }
            }

            text.Append(c);
            index++;
        }

        return Finish(text.ToString(), maxChars);
    }

    /// <summary>Emits the whitespace or marker a tag stands for, and nothing for the rest.</summary>
    private static void AppendTagBreak(StringBuilder text, ReadOnlySpan<char> tag)
    {
        var closing = tag.Length > 0 && tag[0] == '/';
        var name = TagName(closing ? tag[1..] : tag);

        if (name.Length == 0)
        {
            return;
        }

        if (name.Equals("pre", StringComparison.OrdinalIgnoreCase))
        {
            // Both ends of a <pre> become a fence. <code> immediately inside it is ignored below, so
            // the common <pre><code>…</code></pre> produces exactly one pair.
            text.Append("\n```\n");
            return;
        }

        if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            text.Append('\n');
            return;
        }

        if (name.Equals("li", StringComparison.OrdinalIgnoreCase))
        {
            text.Append(closing ? "\n" : "\n- ");
            return;
        }

        foreach (var block in BlockTags)
        {
            if (name.Equals(block, StringComparison.OrdinalIgnoreCase))
            {
                text.Append('\n');
                return;
            }
        }
    }

    /// <summary>Reads the tag name out of <c>h2 class="x"</c> or <c>br /</c>.</summary>
    private static ReadOnlySpan<char> TagName(ReadOnlySpan<char> tag)
    {
        var end = 0;

        while (end < tag.Length && char.IsAsciiLetterOrDigit(tag[end]))
        {
            end++;
        }

        return tag[..end];
    }

    /// <summary>Resolves the entities SonarQube's rule HTML actually contains, plus numeric ones.</summary>
    private static string Unescape(ReadOnlySpan<char> entity)
    {
        if (entity.Length > 1 && entity[0] == '#')
        {
            return UnescapeNumeric(entity);
        }

        return entity switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" or "#39" => "'",
            "nbsp" => " ",
            "hellip" => "…",
            "mdash" => "—",
            "ndash" => "–",
            _ => "&" + entity.ToString() + ";",
        };
    }

    /// <summary>Resolves <c>&amp;#39;</c> and <c>&amp;#x2019;</c>, leaving anything unreadable as it was.</summary>
    private static string UnescapeNumeric(ReadOnlySpan<char> entity)
    {
        var digits = entity[1..];
        int code;

        if (digits.Length > 1 && digits[0] is 'x' or 'X')
        {
            if (!int.TryParse(digits[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
            {
                return "&" + entity.ToString() + ";";
            }
        }
        else if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
        {
            return "&" + entity.ToString() + ";";
        }

        // Surrogate halves and out-of-range values would throw; an entity nobody can render is
        // better left visible than turned into an exception halfway through a rule description.
        if (code is <= 0 or > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
        {
            return "&" + entity.ToString() + ";";
        }

        return char.ConvertFromUtf32(code);
    }

    /// <summary>Collapses the whitespace the tag stripping left behind, then applies the cap.</summary>
    private static (string Text, bool Truncated) Finish(string text, int maxChars)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new StringBuilder(text.Length);
        var blankRun = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();

            if (trimmed.Length == 0)
            {
                blankRun++;

                // One blank line separates paragraphs; more is the residue of nested block tags.
                if (blankRun > 1 || result.Length == 0)
                {
                    continue;
                }
            }
            else
            {
                blankRun = 0;
            }

            result.Append(trimmed).Append('\n');
        }

        var finished = result.ToString().Trim();

        if (finished.Length <= maxChars)
        {
            return (finished, false);
        }

        return (string.Concat(finished.AsSpan(0, Math.Max(0, maxChars - TruncationMarker.Length)), TruncationMarker), true);
    }
}
