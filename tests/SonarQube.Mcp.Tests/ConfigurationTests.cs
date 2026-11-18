using Microsoft.Extensions.Logging;

using NUnit.Framework;

using SonarQube.Mcp.Configuration;

namespace SonarQube.Mcp.Tests;

/// <summary>
/// Covers <see cref="SonarQubeMcpOptions.FromEnvironment(Func{string, string?})"/>: every
/// variable's default, its clamp, and what a bad value falls back to.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through the <see cref="Func{T, TResult}"/> overload, so no test mutates the real
/// process environment — which is global, shared with every parallel test, and would make these
/// results depend on execution order.
/// </para>
/// <para>
/// The rule under test throughout is that <b>reading configuration never throws</b>. A typo in an
/// MCP client's environment block must not produce a server that dies at startup, because stdout is
/// the protocol channel and a dead server has nowhere to explain itself.
/// </para>
/// </remarks>
public class ConfigurationTests
{
    /// <summary>Values that mean true, and the ones that mean false.</summary>
    public static IEnumerable<TestCaseData> BooleanValues =>
    [
        new("1", true),
        new("true", true),
        new("TRUE", true),
        new("yes", true),
        new("on", true),
        new("0", false),
        new("false", false),
        new("no", false),
        new("off", false),

        // Anything else is not a considered opinion, so it falls back to the default (false).
        new("maybe", false),
        new("2", false),
        new("", false),
    ];

    /// <summary>URLs the allowlist accepts, and the normalised base address each produces.</summary>
    public static IEnumerable<TestCaseData> AcceptedUrls =>
    [
        new("https://sonarcloud.io", "https://sonarcloud.io/"),
        new("https://sonarcloud.io/", "https://sonarcloud.io/"),
        new("https://www.sonarcloud.io", "https://www.sonarcloud.io/"),
        new("https://sonarqube.us", "https://sonarqube.us/"),
        new("https://sonarqube.us/", "https://sonarqube.us/"),
        new("  https://sonarcloud.io  ", "https://sonarcloud.io/"),
        new("https://SonarCloud.IO", "https://sonarcloud.io/"),
    ];

    /// <summary>URLs the allowlist rejects, each for a different reason.</summary>
    public static IEnumerable<string> RejectedUrls =>
    [
        "https://evil.example.com",
        "http://sonarcloud.io",
        "https://api.sonarcloud.io",
        "https://sonarcloud.io.evil.example.com",
        "https://sonarcloud.io:8443",
        "https://user:pass@sonarcloud.io",
        "https://sonarcloud.io/some/path",
        "https://sonarcloud.io/?a=b",
        "sonarcloud.io",
        "not a url at all",
        "ftp://sonarcloud.io",
    ];

    [Test]
    public void AnEmptyEnvironmentProducesEveryDocumentedDefault()
    {
        var options = Read();

        Assert.That(options.Token, Is.Null);
        Assert.That(options.Organization, Is.Null);
        Assert.That(options.BaseUrl, Is.EqualTo(SonarQubeMcpOptions.DefaultBaseUrl));
        Assert.That(options.RejectedBaseUrl, Is.Null);
        Assert.That(options.DefaultProject, Is.Null);
        Assert.That(options.ReadOnly, Is.False);
        Assert.That(options.LogLevel, Is.EqualTo(LogLevel.Information));
        Assert.That(options.MaxPageSize, Is.EqualTo(100));
        Assert.That(options.DefaultPageSize, Is.EqualTo(50));
        Assert.That(options.MaxSourceLines, Is.EqualTo(2000));
        Assert.That(options.HttpTimeoutSeconds, Is.EqualTo(100));
    }

    [Test]
    public void FromEnvironmentReadsTheRealProcessEnvironmentWithoutThrowing()
    {
        // The no-argument overload is what production calls; it must be exercised at least once,
        // whatever the machine's environment happens to hold.
        var options = SonarQubeMcpOptions.FromEnvironment();

        Assert.That(options.BaseUrl, Is.Not.Null);
        Assert.That(options.MaxPageSize, Is.InRange(1, SonarQubeMcpOptions.PageSizeLimit));
        Assert.That(options.DefaultPageSize, Is.InRange(1, options.MaxPageSize));
    }

    // ------------------------------------------------------------------ strings

    [Test]
    public void TheTokenIsTrimmed()
    {
        Assert.That(Read(("SONARQUBE_TOKEN", "  squ_abc  ")).Token, Is.EqualTo("squ_abc"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t")]
    public void ABlankTokenIsTheSameAsNoToken(string value)
    {
        // A variable set to the empty string is how a client config expresses "not configured";
        // treating it as a token would send "Bearer " and get a 401 nobody could explain.
        Assert.That(Read(("SONARQUBE_TOKEN", value)).Token, Is.Null);
    }

    [Test]
    public void TheOrganizationAndDefaultProjectAreTrimmed()
    {
        var options = Read(
            ("SONARQUBE_ORG", " quartznet "),
            ("SONARQUBE_MCP_DEFAULT_PROJECT", " quartznet_quartznet "));

        Assert.That(options.Organization, Is.EqualTo("quartznet"));
        Assert.That(options.DefaultProject, Is.EqualTo("quartznet_quartznet"));
    }

    // ------------------------------------------------------------------ base URL allowlist

    [TestCaseSource(nameof(AcceptedUrls))]
    public void AnAllowlistedOriginIsAcceptedAndNormalisedWithATrailingSlash(string value, string expected)
    {
        var options = Read(("SONARQUBE_URL", value));

        // The trailing slash is not cosmetic: HttpClient drops the last path segment of a base
        // address that lacks one, so "https://sonarcloud.io" would resolve "api/x" against the root.
        Assert.That(options.BaseUrl, Is.EqualTo(new Uri(expected)));
        Assert.That(options.RejectedBaseUrl, Is.Null);
    }

    [TestCaseSource(nameof(RejectedUrls))]
    public void AnythingElseFallsBackToTheDefaultAndIsRecordedForLogging(string value)
    {
        var options = Read(("SONARQUBE_URL", value));

        Assert.That(options.BaseUrl, Is.EqualTo(SonarQubeMcpOptions.DefaultBaseUrl));

        // Recorded rather than logged here, because this type is built before the logging pipeline
        // exists; McpServerSetup emits the warning at the first moment a logger does.
        Assert.That(options.RejectedBaseUrl, Is.EqualTo(value.Trim()));
    }

    [Test]
    public void AnUnsetUrlIsNotARejection()
    {
        Assert.That(Read().RejectedBaseUrl, Is.Null);
    }

    [Test]
    public void TheBaseUrlTextDropsTheTrailingSlashForMessages()
    {
        // What the token-missing message interpolates: "{base}/account/security", which must not
        // come out as "https://sonarcloud.io//account/security".
        Assert.That(Read().BaseUrlText, Is.EqualTo("https://sonarcloud.io"));
    }

    // ------------------------------------------------------------------ booleans and enums

    [TestCaseSource(nameof(BooleanValues))]
    public void ReadOnlyModeAcceptsTheDocumentedSpellings(string value, bool expected)
    {
        Assert.That(Read(("SONARQUBE_MCP_READ_ONLY", value)).ReadOnly, Is.EqualTo(expected));
    }

    [TestCase("Debug", LogLevel.Debug)]
    [TestCase("debug", LogLevel.Debug)]
    [TestCase("TRACE", LogLevel.Trace)]
    [TestCase("Warning", LogLevel.Warning)]
    [TestCase("None", LogLevel.None)]
    public void TheLogLevelIsParsedCaseInsensitively(string value, LogLevel expected)
    {
        Assert.That(Read(("SONARQUBE_MCP_LOG_LEVEL", value)).LogLevel, Is.EqualTo(expected));
    }

    [TestCase("42")]
    [TestCase("-1")]
    [TestCase("Verbose")]
    [TestCase("")]
    public void AnUnrecognisedLogLevelFallsBackToInformation(string value)
    {
        // Enum.TryParse accepts bare numbers, so "42" would otherwise become a LogLevel of 42 and
        // silently suppress every log line.
        Assert.That(Read(("SONARQUBE_MCP_LOG_LEVEL", value)).LogLevel, Is.EqualTo(LogLevel.Information));
    }

    // ------------------------------------------------------------------ numbers

    [TestCase("1", 1)]
    [TestCase("250", 250)]
    [TestCase("500", 500)]
    public void MaxPageSizeAcceptsItsWholeRange(string value, int expected)
    {
        Assert.That(Read(("SONARQUBE_MCP_MAX_PAGE_SIZE", value)).MaxPageSize, Is.EqualTo(expected));
    }

    [TestCase("0")]
    [TestCase("-5")]
    [TestCase("501")]
    [TestCase("banana")]
    [TestCase("1e3")]
    [TestCase("")]
    public void AnOutOfRangeMaxPageSizeFallsBackToTheDefault(string value)
    {
        // 500 is SonarQube Cloud's own ceiling; a configured value above it could only ever produce
        // a 400 on every call.
        Assert.That(Read(("SONARQUBE_MCP_MAX_PAGE_SIZE", value)).MaxPageSize, Is.EqualTo(100));
    }

    [Test]
    public void DefaultPageSizeIsAcceptedWithinTheMaximum()
    {
        var options = Read(
            ("SONARQUBE_MCP_MAX_PAGE_SIZE", "200"),
            ("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", "150"));

        Assert.That(options.MaxPageSize, Is.EqualTo(200));
        Assert.That(options.DefaultPageSize, Is.EqualTo(150));
    }

    [Test]
    public void DefaultPageSizeIsReclampedWhenTheMaximumIsLoweredBelowIt()
    {
        var options = Read(("SONARQUBE_MCP_MAX_PAGE_SIZE", "10"));

        // Lowering only the ceiling must not leave every omitted pageSize silently above it. The
        // two variables are read independently, so this is a second pass, not a parse rule.
        Assert.That(options.MaxPageSize, Is.EqualTo(10));
        Assert.That(options.DefaultPageSize, Is.EqualTo(10));
    }

    [Test]
    public void DefaultPageSizeIsReclampedEvenWhenBothAreSetInconsistently()
    {
        var options = Read(
            ("SONARQUBE_MCP_MAX_PAGE_SIZE", "20"),
            ("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", "400"));

        Assert.That(options.MaxPageSize, Is.EqualTo(20));
        Assert.That(options.DefaultPageSize, Is.EqualTo(20));
    }

    [TestCase("0")]
    [TestCase("501")]
    [TestCase("nope")]
    public void AnOutOfRangeDefaultPageSizeFallsBackToFifty(string value)
    {
        Assert.That(Read(("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", value)).DefaultPageSize, Is.EqualTo(50));
    }

    [TestCase("1", 1)]
    [TestCase("500", 500)]
    [TestCase("20000", 20_000)]
    public void MaxSourceLinesAcceptsItsRange(string value, int expected)
    {
        Assert.That(Read(("SONARQUBE_MCP_MAX_SOURCE_LINES", value)).MaxSourceLines, Is.EqualTo(expected));
    }

    [TestCase("0")]
    [TestCase("20001")]
    [TestCase("lots")]
    public void AnOutOfRangeMaxSourceLinesFallsBackToTwoThousand(string value)
    {
        Assert.That(Read(("SONARQUBE_MCP_MAX_SOURCE_LINES", value)).MaxSourceLines, Is.EqualTo(2000));
    }

    [TestCase("5", 5)]
    [TestCase("30", 30)]
    [TestCase("600", 600)]
    public void TheHttpTimeoutAcceptsItsRange(string value, int expected)
    {
        Assert.That(Read(("SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS", value)).HttpTimeoutSeconds, Is.EqualTo(expected));
    }

    [TestCase("4")]
    [TestCase("601")]
    [TestCase("0")]
    [TestCase("soon")]
    public void AnOutOfRangeHttpTimeoutFallsBackToOneHundredSeconds(string value)
    {
        // The lower bound matters: a one-second timeout would fail every real call, and a server
        // that starts and then times out everything is harder to diagnose than one that ignores a
        // silly value.
        Assert.That(Read(("SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS", value)).HttpTimeoutSeconds, Is.EqualTo(100));
    }

    [Test]
    public void EveryVariableCanBeSetAtOnce()
    {
        var options = Read(
            ("SONARQUBE_TOKEN", "squ_abc"),
            ("SONARQUBE_ORG", "quartznet"),
            ("SONARQUBE_URL", "https://sonarqube.us/"),
            ("SONARQUBE_MCP_DEFAULT_PROJECT", "quartznet_quartznet"),
            ("SONARQUBE_MCP_READ_ONLY", "1"),
            ("SONARQUBE_MCP_LOG_LEVEL", "Debug"),
            ("SONARQUBE_MCP_MAX_PAGE_SIZE", "200"),
            ("SONARQUBE_MCP_DEFAULT_PAGE_SIZE", "25"),
            ("SONARQUBE_MCP_MAX_SOURCE_LINES", "500"),
            ("SONARQUBE_MCP_HTTP_TIMEOUT_SECONDS", "30"));

        Assert.That(options.Token, Is.EqualTo("squ_abc"));
        Assert.That(options.Organization, Is.EqualTo("quartznet"));
        Assert.That(options.BaseUrl, Is.EqualTo(new Uri("https://sonarqube.us/")));
        Assert.That(options.DefaultProject, Is.EqualTo("quartznet_quartznet"));
        Assert.That(options.ReadOnly, Is.True);
        Assert.That(options.LogLevel, Is.EqualTo(LogLevel.Debug));
        Assert.That(options.MaxPageSize, Is.EqualTo(200));
        Assert.That(options.DefaultPageSize, Is.EqualTo(25));
        Assert.That(options.MaxSourceLines, Is.EqualTo(500));
        Assert.That(options.HttpTimeoutSeconds, Is.EqualTo(30));
    }

    [Test]
    public void AVariableSourceThatThrowsIsNotSomethingThisTypePromisesToSurvive()
    {
        // The contract is "a bad *value* never throws", not "a broken *source* never throws" — the
        // real source is Environment.GetEnvironmentVariable, which does not fail. Stated as a test
        // so the boundary is deliberate rather than assumed.
        Assert.Throws<InvalidOperationException>(
            () => SonarQubeMcpOptions.FromEnvironment(_ => throw new InvalidOperationException("boom")));
    }

    [Test]
    public void ANullVariableSourceIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SonarQubeMcpOptions.FromEnvironment(null!));
    }

    /// <summary>Builds a variable source from the pairs a test cares about; everything else is unset.</summary>
    private static SonarQubeMcpOptions Read(params (string Name, string Value)[] variables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            map[name] = value;
        }

        return SonarQubeMcpOptions.FromEnvironment(name => map.TryGetValue(name, out var value) ? value : null);
    }

    // ---------------------------------------------------------------------------------------
    // The plugin-option layer (issue #1)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The regression for issue #1. A Claude Code plugin substitutes an option the user never filled
    /// in as the <b>empty string</b> rather than omitting it, so an option mapped straight onto
    /// <c>SONARQUBE_TOKEN</c> sets that variable to <c>""</c> in the child process and shadows a
    /// value the user already had. Blank from the plugin layer therefore means <em>absent</em>, and
    /// the plain variable still wins the day.
    /// </summary>
    [TestCase("")]
    [TestCase("   ")]
    public void AnEmptyPluginOptionDoesNotShadowTheAmbientVariable(string pluginValue)
    {
        var options = SonarQubeMcpOptions.FromEnvironment(name => name switch
        {
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_TOKEN" => pluginValue,
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_ORG" => pluginValue,
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_MCP_DEFAULT_PROJECT" => pluginValue,
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_URL" => pluginValue,
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_MCP_READ_ONLY" => pluginValue,
            "SONARQUBE_TOKEN" => "ambient-token",
            "SONARQUBE_ORG" => "ambient-org",
            "SONARQUBE_MCP_DEFAULT_PROJECT" => "ambient_project",
            "SONARQUBE_URL" => "https://sonarqube.us/",
            "SONARQUBE_MCP_READ_ONLY" => "1",
            _ => null,
        });

        Assert.That(options.Token, Is.EqualTo("ambient-token"));
        Assert.That(options.TokenVariable, Is.EqualTo("SONARQUBE_TOKEN"));
        Assert.That(options.Organization, Is.EqualTo("ambient-org"));
        Assert.That(options.DefaultProject, Is.EqualTo("ambient_project"));
        Assert.That(options.BaseUrlText, Is.EqualTo("https://sonarqube.us"));
        Assert.That(options.ReadOnly, Is.True);
    }

    /// <summary>A filled-in option is the point of the prompt, so it wins over the environment.</summary>
    [Test]
    public void AConfiguredPluginOptionWinsOverTheAmbientVariable()
    {
        var options = SonarQubeMcpOptions.FromEnvironment(name => name switch
        {
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_TOKEN" => "plugin-token",
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_ORG" => "plugin-org",
            "CLAUDE_PLUGIN_OPTION_SONARQUBE_MCP_READ_ONLY" => "1",
            "SONARQUBE_TOKEN" => "ambient-token",
            "SONARQUBE_ORG" => "ambient-org",
            "SONARQUBE_MCP_READ_ONLY" => "0",
            _ => null,
        });

        Assert.That(options.Token, Is.EqualTo("plugin-token"));
        Assert.That(options.TokenVariable, Is.EqualTo("CLAUDE_PLUGIN_OPTION_SONARQUBE_TOKEN"));
        Assert.That(options.Organization, Is.EqualTo("plugin-org"));
        Assert.That(options.ReadOnly, Is.True);
    }

    /// <summary>With neither set there is no token and nothing to attribute it to.</summary>
    [Test]
    public void WithNeitherVariableSetThereIsNoTokenAndNoSource()
    {
        var options = SonarQubeMcpOptions.FromEnvironment(static _ => null);

        Assert.That(options.Token, Is.Null);
        Assert.That(options.TokenVariable, Is.Null);
    }
}
