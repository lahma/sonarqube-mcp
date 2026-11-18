using NUnit.Framework;

using SonarQube.Mcp.Tools;

namespace SonarQube.Mcp.Tests.Tools;

/// <summary>
/// The ninety lines that turn SonarQube's rule and hotspot prose — which is HTML — into text a
/// model can read.
/// </summary>
/// <remarks>
/// The fencing is the load-bearing part: a rule's "how to fix it" section is mostly code, and
/// unfenced code reads as prose. Everything else is subtraction, and the only rule about
/// subtraction is that it must not silently remove half a fix — hence the visible truncation
/// marker and the flag beside it.
/// </remarks>
public class HtmlToTextTests
{
    /// <summary>Long enough that nothing in these fixtures is truncated by accident.</summary>
    private const int NoCap = 4000;

    [Test]
    public void APreCodeBlockBecomesExactlyOneFencedBlock()
    {
        var (text, truncated) = HtmlToText.Convert(
            "<p>Fix it:</p><pre><code>var x = 1;\nvar y = 2;</code></pre>",
            NoCap);

        Assert.That(text, Is.EqualTo("Fix it:\n\n```\nvar x = 1;\nvar y = 2;\n```"));
        Assert.That(truncated, Is.False);
    }

    /// <summary>
    /// <c>&lt;code&gt;</c> outside a <c>&lt;pre&gt;</c> is inline and must not fence: a sentence
    /// mentioning <c>null</c> is still a sentence.
    /// </summary>
    [Test]
    public void InlineCodeIsNotFenced()
    {
        var (text, _) = HtmlToText.Convert(
            "<p>A reference to <code>null</code> should never be dereferenced.</p>",
            NoCap);

        Assert.That(text, Is.EqualTo("A reference to null should never be dereferenced."));
    }

    [Test]
    public void ListItemsBecomeBullets()
    {
        var (text, _) = HtmlToText.Convert(
            "<ul><li>First point</li><li>Second point</li></ul>",
            NoCap);

        // A blank line between the bullets: </li> ends the line and the next <li> starts a new one,
        // which markdown reads as a loose list and renders the same way.
        Assert.That(text, Is.EqualTo("- First point\n\n- Second point"));
    }

    [Test]
    public void HeadingsAndParagraphsBecomeLineBreaks()
    {
        var (text, _) = HtmlToText.Convert(
            "<h2>Why</h2><p>Because.</p><h3>How</h3><p>Like this.<br>And this.</p>",
            NoCap);

        // One blank line per block boundary; <br> is a line break inside a block, not a boundary.
        Assert.That(text, Is.EqualTo("Why\n\nBecause.\n\nHow\n\nLike this.\nAnd this."));
    }

    /// <summary>A link's text is the part worth keeping; a URL a model cannot fetch is noise.</summary>
    [Test]
    public void LinkTargetsAreDroppedAndTheirTextIsKept()
    {
        var (text, _) = HtmlToText.Convert(
            """<p>See <a href="https://cwe.mitre.org/data/definitions/476">CWE-476</a>.</p>""",
            NoCap);

        Assert.That(text, Is.EqualTo("See CWE-476."));
    }

    [TestCase("a &amp; b", "a & b")]
    [TestCase("&lt;script&gt;", "<script>")]
    [TestCase("&quot;quoted&quot;", "\"quoted\"")]
    [TestCase("it&apos;s", "it's")]
    [TestCase("it&#39;s", "it's")]
    [TestCase("provider&#x2019;s", "provider’s")]
    [TestCase("a&nbsp;b", "a b")]
    [TestCase("wait&hellip;", "wait…")]
    [TestCase("a &mdash; b", "a — b")]
    public void EntitiesAreUnescaped(string html, string expected) =>
        Assert.That(HtmlToText.Convert(html, NoCap).Text, Is.EqualTo(expected));

    /// <summary>
    /// An entity nobody can render is left visible rather than turned into an exception halfway
    /// through a rule description.
    /// </summary>
    [TestCase("&nosuchentity;")]
    [TestCase("&#xD800;")]
    [TestCase("&#0;")]
    public void AnUnreadableEntityIsLeftAsItWas(string html) =>
        Assert.That(HtmlToText.Convert(html, NoCap).Text, Is.EqualTo(html));

    /// <summary>
    /// A bare ampersand in prose has no terminator anywhere near, and must not swallow the rest of
    /// the paragraph looking for one.
    /// </summary>
    [Test]
    public void ABareAmpersandIsLeftAloneAndKeepsTheRestOfTheSentence()
    {
        var (text, _) = HtmlToText.Convert("Tom & Jerry are a well known pair of characters.", NoCap);

        Assert.That(text, Is.EqualTo("Tom & Jerry are a well known pair of characters."));
    }

    [Test]
    public void AttributesAndUnknownTagsAreStripped()
    {
        var (text, _) = HtmlToText.Convert(
            """<div class="rule"><span data-x="1">Text</span><em>!</em></div>""",
            NoCap);

        Assert.That(text, Is.EqualTo("Text!"));
    }

    /// <summary>
    /// An unterminated tag means everything after it is markup we cannot read; emitting it raw would
    /// put angle brackets into the model's context.
    /// </summary>
    [Test]
    public void AnUnterminatedTagStopsTheConversionInsteadOfLeakingMarkup()
    {
        var (text, _) = HtmlToText.Convert("<p>Kept.</p><p class=\"broken", NoCap);

        Assert.That(text, Is.EqualTo("Kept."));
    }

    [Test]
    public void NestedBlockTagsDoNotLeaveARunOfBlankLines()
    {
        var (text, _) = HtmlToText.Convert(
            "<div><div><p>One</p></div></div><div><p>Two</p></div>",
            NoCap);

        // Three block tags close at the same point and still leave exactly one blank line.
        Assert.That(text, Is.EqualTo("One\n\nTwo"));
        Assert.That(text, !Contains.Substring("\n\n\n").Using(StringComparison.Ordinal));
    }

    /// <summary>Truncation is never silent: there is a visible marker and a flag beside it.</summary>
    [Test]
    public void TextOverTheCapEndsWithAVisibleMarkerAndSaysSo()
    {
        var html = "<p>" + new string('x', 500) + "</p>";

        var (text, truncated) = HtmlToText.Convert(html, 100);

        Assert.That(truncated, Is.True);
        Assert.That(text.Length, Is.EqualTo(100));
        Assert.That(text, Does.EndWith("… [truncated]").Using(StringComparison.Ordinal));
    }

    [Test]
    public void TextExactlyAtTheCapIsNotTruncated()
    {
        var (text, truncated) = HtmlToText.Convert(new string('x', 100), 100);

        Assert.That(truncated, Is.False);
        Assert.That(text.Length, Is.EqualTo(100));
        Assert.That(text, !Contains.Substring("truncated").Using(StringComparison.Ordinal));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   \n  ")]
    public void NothingToConvertIsAnEmptyStringRatherThanNull(string? html)
    {
        var (text, truncated) = HtmlToText.Convert(html, NoCap);

        Assert.That(text, Is.EqualTo(string.Empty));
        Assert.That(truncated, Is.False);
    }

    /// <summary>
    /// The real thing: one of SonarQube's own <c>how_to_fix</c> sections, entities, diff attributes
    /// and all.
    /// </summary>
    [Test]
    public void ARealRuleSectionSurvivesTheRoundTrip()
    {
        const string Html = """
            <h4>Compliant solution</h4>
            <pre data-diff-id="1" data-diff-type="compliant">
            object o = null;
            if (condition &amp;&amp; o is not null)
            {
                o.ToString();
            }
            </pre>
            """;

        var (text, truncated) = HtmlToText.Convert(Html, NoCap);

        Assert.That(truncated, Is.False);
        Assert.That(text, Does.StartWith("Compliant solution").Using(StringComparison.Ordinal));
        Assert.That(text, Contains.Substring("if (condition && o is not null)").Using(StringComparison.Ordinal));
        Assert.That(CountFences(text), Is.EqualTo(2));
        Assert.That(text, !Contains.Substring("data-diff-id").Using(StringComparison.Ordinal));
    }

    private static int CountFences(string text)
    {
        var count = 0;

        foreach (var line in text.Split('\n'))
        {
            if (string.Equals(line, "```", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
